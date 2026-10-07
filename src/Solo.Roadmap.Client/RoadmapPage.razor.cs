using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using Solo.Roadmap.Contracts;
using Solo.Shared;
using Solo.Shared.Dtos;
using Solo.Shared.Extensibility;
using Solo.Shared.Hubs;

namespace Solo.Roadmap.Client;

public sealed partial class RoadmapPage
{
    /// <summary>Pixels per day column. Also written to --roadmap-day-width, so CSS and layout agree.</summary>
    public const int DayWidth = 40;

    public const int RowHeight = 40;

    /// <summary>How wide a bar has to be before its assignees' avatars fit beside the name.</summary>
    private const int AvatarBarWidth = 200;

    /// <summary>Avatars of others on the page before the rest fold into a count, as in the task drawer.</summary>
    private const int MaxPresent = 4;

    private ElementReference _scroller;
    private DotNetObjectReference<RoadmapPage>? _self;
    private IJSObjectReference? _timelineHandle;

    /// <summary>
    /// The attach in flight. Attaching awaits the module import, and any render
    /// that lands meanwhile would otherwise start a second one — two sets of
    /// gesture handlers on one grid, and every click on it counted twice.
    /// </summary>
    private Task? _attaching;

    private (string Alias, Guid Roadmap)? _loadedFor;

    private ProjectSummaryDto? _project;
    private RoadmapTimelineDto? _timeline;
    private List<RoadmapEntryDto> _entries = [];

    /// <summary>The roadmap's milestones, earliest first.</summary>
    private List<RoadmapMilestoneDto> _milestones = [];
    private TimelineWindow _window = new(default, default);
    private DateOnly _today;
    private (int First, int Last) _rendered;
    private Dictionary<Guid, ProjectMemberDto> _members = [];
    private readonly Dictionary<Guid, LabelDto> _labels = [];
    private readonly HashSet<Guid> _labelBoards = [];
    private string? _error;

    /// <summary>The entries selected for the bar menu to act on together.</summary>
    private readonly HashSet<Guid> _selected = [];

    /// <summary>Where a Shift-click's run of rows starts: the bar last clicked or Cmd/Ctrl-clicked.</summary>
    private Guid? _anchor;

    private IAsyncDisposable? _live;
    private IReadOnlyList<TopicPresenceEntry> _present = [];
    private PresenceMode _reported;
    private bool _gesturing;
    private bool _inDialog;

    /// <summary>This person's saves in flight, from the optimistic redraw to the server's answer.</summary>
    private int _saving;

    /// <summary>Counts this person's changes, so a read that started before one can tell it's behind.</summary>
    private int _edits;

    /// <summary>Someone else changed the roadmap and this page hasn't caught up yet.</summary>
    private bool _stale;

    private bool _catchingUp;

    [Inject] private ISoloClientApi Client { get; set; } = null!;

    [Inject] private ISoloTaskDrawer Drawer { get; set; } = null!;

    [Inject] private ISoloTaskPicker Picker { get; set; } = null!;

    [Inject] private ISoloContextMenu Menu { get; set; } = null!;

    [Inject] private IJSRuntime JS { get; set; } = null!;

    [Inject] private IDialogService Dialogs { get; set; } = null!;

    [Inject] private ISnackbar Snackbar { get; set; } = null!;

    [Inject] private NavigationManager Navigation { get; set; } = null!;

    [Inject] private ISoloLiveTopics Live { get; set; } = null!;

    [Parameter]
    public string ProjectAlias { get; set; } = "";

    [Parameter]
    public Guid RoadmapId { get; set; }

    private RoadmapApi Api => new(Client);

    /// <summary>The board's write rule: an owner, or a read/write member.</summary>
    private bool CanWrite => _project is { } project && (project.IsOwner || project.Role == ProjectRole.ReadWrite);

    // -- Loading ------------------------------------------------------------------

    protected override void OnInitialized()
    {
        _self = DotNetObjectReference.Create(this);
        Drawer.Changed += OnDrawerChanged;
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFor is (var alias, var roadmap) &&
            string.Equals(alias, ProjectAlias, StringComparison.OrdinalIgnoreCase) && roadmap == RoadmapId)
        {
            return;
        }

        _loadedFor = (ProjectAlias, RoadmapId);
        await StopLiveAsync();
        await DetachTimelineAsync();
        _timeline = null;
        _error = null;
        _selected.Clear();
        _anchor = null;
        _gesturing = false;
        _stale = false;

        // In parallel with the reads, so the grid's first frame is already styled.
        var styles = RoadmapAssets.EnsureStylesAsync(JS, Api);
        await LoadAsync();
        await styles;
    }

    private async Task LoadAsync()
    {
        try
        {
            _project = (await Api.ProjectsAsync()).FirstOrDefault(p =>
                string.Equals(p.Alias, ProjectAlias, StringComparison.OrdinalIgnoreCase));
            if (_project is null)
            {
                _error = "That project doesn't exist, or you aren't a member of it.";
                return;
            }

            // Listening before the read, so a change made while it's on its way isn't missed.
            await StartLiveAsync();
            Show(await Api.TimelineAsync(_project.Id, RoadmapId));
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _error = "That roadmap doesn't exist — it may have been deleted.";
            await StopLiveAsync();
            return;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            _error = "Couldn't load this roadmap.";
            await StopLiveAsync();
            return;
        }

        await LoadPeopleAndLabelsAsync();
        await CatchUpAsync();
    }

    /// <summary>Opens the roadmap: its rows, and a view around the day it starts on.</summary>
    private void Show(RoadmapTimelineDto timeline)
    {
        _timeline = timeline;
        _entries = timeline.Entries.ToList();
        _milestones = timeline.Milestones.OrderBy(m => m.Day).ToList();
        _today = DateOnly.FromDateTime(DateTime.Now);
        _window = TimelineWindow.Around(_today, timeline.Settings);

        // Until the browser reports the real viewport, render around where the
        // view is about to open.
        var start = _window.IndexOf(_window.Clamp(StartingDay()));
        _rendered = Viewport.Rendered((start, start + 40), _window.Days);
    }

    /// <summary>
    /// Takes the server's copy of an open roadmap without moving the view: the
    /// window, the scroll position and the columns drawn all stay put.
    /// </summary>
    private void Apply(RoadmapTimelineDto timeline)
    {
        _timeline = timeline;
        _entries = timeline.Entries.ToList();
        _milestones = timeline.Milestones.OrderBy(m => m.Day).ToList();
        _selected.IntersectWith(_entries.Select(e => e.Id));
    }

    /// <summary>Who's who and which colour each label is — fetched once, and never allowed to block the grid.</summary>
    private async Task LoadPeopleAndLabelsAsync()
    {
        try
        {
            _members = (await Api.MembersAsync(_project!.Id))
                .Where(m => m.UserId is not null)
                .ToDictionary(m => m.UserId!.Value);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            // Bars and the missed table go without avatars.
        }

        await LoadLabelsAsync();
    }

    /// <summary>
    /// Labels are per board; fetch any board's whose tasks are on the roadmap and
    /// not yet known. True when that learned any.
    /// </summary>
    private async Task<bool> LoadLabelsAsync()
    {
        var learned = false;
        foreach (var boardId in _entries.Select(e => e.Task.BoardId).Distinct().Where(_labelBoards.Add).ToList())
        {
            try
            {
                foreach (var label in await Client.GetAsync<List<LabelDto>>($"api/boards/{boardId}/labels"))
                {
                    _labels[label.Id] = label;
                    learned = true;
                }
            }
            catch (Exception e) when (e is SoloApiException or HttpRequestException)
            {
                _labelBoards.Remove(boardId);
            }
        }

        return learned;
    }

    private async Task ReloadAsync()
    {
        if (_project is null)
        {
            return;
        }

        try
        {
            Apply(await Api.TimelineAsync(_project.Id, RoadmapId));
            await LoadLabelsAsync();
            await RefreshTimelineAsync();
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add("Couldn't refresh this roadmap.", Severity.Warning);
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_timeline is null || _error is not null)
        {
            return;
        }

        if (_timelineHandle is null)
        {
            _attaching ??= AttachTimelineAsync();
            await _attaching;
            return;
        }

        await RefreshTimelineAsync();
    }

    private async Task AttachTimelineAsync()
    {
        try
        {
            var module = await RoadmapAssets.ModuleAsync(JS, Api);
            _timelineHandle = await module.InvokeAsync<IJSObjectReference>("attach", _scroller, _self, new
            {
                dayWidth = DayWidth,
                rowHeight = RowHeight,
                days = _window.Days,
                canWrite = CanWrite,
                firstDay = _window.First.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
            await ScrollToDayAsync(_window.IndexOf(StartingDay()));
        }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            // No module means no scroll tracking or gestures. The grid still
            // renders around the opening day, and clicks still open tasks.
        }
    }

    private async Task RefreshTimelineAsync()
    {
        if (_timelineHandle is null)
        {
            return;
        }

        try
        {
            await _timelineHandle.InvokeVoidAsync("configure",
                new { days = _window.Days, canWrite = CanWrite });
            await _timelineHandle.InvokeVoidAsync("refresh");
        }
        catch (JSDisconnectedException)
        {
        }
    }

    /// <remarks>
    /// The handle leaves the field before anything is awaited: a render landing
    /// mid-dispose would otherwise refresh through a reference already disposed.
    /// </remarks>
    private async Task DetachTimelineAsync()
    {
        var handle = _timelineHandle;
        _timelineHandle = null;
        _attaching = null;
        if (handle is null)
        {
            return;
        }

        try
        {
            await handle.InvokeVoidAsync("dispose");
            await handle.DisposeAsync();
        }
        catch (JSDisconnectedException)
        {
        }
    }

    private async Task ScrollToDayAsync(int index)
    {
        if (_timelineHandle is not null)
        {
            await _timelineHandle.InvokeVoidAsync("scrollToDay", Math.Clamp(index, 0, _window.Days - 1));
        }
    }

    /// <summary>Where the view opens: the oldest start among rows in flight today, or today.</summary>
    private DateOnly StartingDay() =>
        Timeline.StartingDay(_entries.Select(e => Timeline.Span(e.Task, e.AnchorDay)), _today);

    // -- From roadmap.js --------------------------------------------------------------

    [JSInvokable]
    public Task OnViewport(double scrollLeft, double width) => InvokeAsync(() =>
    {
        var visible = Viewport.Visible(scrollLeft, width, DayWidth, _window.Days);
        if (Viewport.NeedsRecut(visible, _rendered, _window.Days))
        {
            _rendered = Viewport.Rendered(visible, _window.Days);
            StateHasChanged();
        }
    });

    /// <summary>A bar picked up (or put down) to drag or resize.</summary>
    [JSInvokable]
    public Task OnGesture(bool active) => InvokeAsync(async () =>
    {
        _gesturing = active;
        await ReportPresenceAsync();
        await CatchUpAsync();
    });

    /// <summary>A plain click on a bar: the selection gives way to the task it opens.</summary>
    [JSInvokable]
    public Task OnBarClick(string entryId) => InvokeAsync(async () =>
    {
        if (Find(entryId) is not { } entry)
        {
            return;
        }

        _anchor = entry.Id;
        if (_selected.Count > 0)
        {
            _selected.Clear();
            StateHasChanged();
        }

        await OpenTaskAsync(entry);
    });

    /// <summary>
    /// Cmd-click (Ctrl-click off a Mac) adds a bar to the selection or takes it
    /// out; Shift-click selects the run of rows from the last one clicked.
    /// </summary>
    [JSInvokable]
    public Task OnBarSelect(string entryId, bool range) => InvokeAsync(() =>
    {
        if (CanWrite && Find(entryId) is { } entry)
        {
            Select(entry, range);
            StateHasChanged();
        }
    });

    /// <summary>
    /// The bars a box dragged across the grid touched, scrolled out of view or
    /// not. <paramref name="additive"/> (Cmd/Ctrl or Shift held) keeps what was
    /// already selected.
    /// </summary>
    [JSInvokable]
    public Task OnMarquee(string[] entryIds, bool additive) => InvokeAsync(() =>
    {
        if (!CanWrite)
        {
            return;
        }

        if (!additive)
        {
            _selected.Clear();
            _anchor = null;
        }

        _selected.UnionWith(entryIds.Select(Find).OfType<RoadmapEntryDto>().Select(e => e.Id));
        StateHasChanged();
    });

    /// <summary>A click on the grid away from the bars, or Escape.</summary>
    [JSInvokable]
    public Task OnClearSelection() => InvokeAsync(() =>
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            StateHasChanged();
        }
    });

    [JSInvokable]
    public Task OnBarMenu(string entryId, double clientX, double clientY) =>
        Find(entryId) is { } entry
            ? InvokeAsync(() => ShowBarMenuAsync(entry, clientX, clientY))
            : Task.CompletedTask;

    /// <summary>A right-click (or a long press) on a day no bar covers.</summary>
    [JSInvokable]
    public Task OnDayMenu(int index, double clientX, double clientY) => InvokeAsync(async () =>
    {
        if (_selected.Count > 0)
        {
            _selected.Clear();
            StateHasChanged();
        }

        if (!CanWrite)
        {
            return;
        }

        var day = _window.DayAt(Math.Clamp(index, 0, _window.Days - 1));
        switch (await Menu.ShowAsync(clientX, clientY,
                [
                    new ContextMenuItem(AddItem, $"Add a task on {Dates.Day(day)}") { Icon = Icons.Material.Filled.Add },
                    new ContextMenuItem(AddMilestoneItem, $"Add a milestone on {Dates.Day(day)}")
                    {
                        Icon = Icons.Material.Filled.Flag,
                    },
                ]))
        {
            case AddItem:
                await PickAsync(day);
                break;
            case AddMilestoneItem:
                await AddMilestoneAsync(day);
                break;
        }
    });

    /// <summary>A click on a milestone's flag or line, without dragging it: writers get its dialog.</summary>
    [JSInvokable]
    public Task OnMilestoneClick(string milestoneId) =>
        CanWrite && FindMilestone(milestoneId) is { } milestone
            ? InvokeAsync(() => EditMilestoneAsync(milestone))
            : Task.CompletedTask;

    /// <summary>A right-click (or a long press) on a milestone's flag or line.</summary>
    [JSInvokable]
    public Task OnMilestoneMenu(string milestoneId, double clientX, double clientY) => InvokeAsync(async () =>
    {
        if (!CanWrite || FindMilestone(milestoneId) is not { } milestone)
        {
            return;
        }

        switch (await Menu.ShowAsync(clientX, clientY,
                [
                    new ContextMenuItem(EditItem, "Edit milestone") { Icon = Icons.Material.Filled.Edit },
                    new ContextMenuItem(RemoveItem, "Delete milestone")
                    {
                        Icon = Icons.Material.Filled.Delete,
                        Danger = true,
                        SeparatorBefore = true,
                    },
                ]))
        {
            case EditItem:
                await EditMilestoneAsync(milestone);
                break;
            case RemoveItem:
                await DeleteMilestoneAsync(milestone);
                break;
        }
    });

    /// <summary>A milestone's flag or line dropped on column <paramref name="index"/>.</summary>
    [JSInvokable]
    public Task OnMilestoneMove(string milestoneId, int index) => InvokeAsync(() =>
    {
        if (CanWrite && FindMilestone(milestoneId) is { } milestone)
        {
            // As with a bar, the line settles where it was dropped and the save finishes on its own.
            _ = SaveMilestoneAsync(milestone,
                milestone with { Day = _window.DayAt(Math.Clamp(index, 0, _window.Days - 1)) });
        }
    });

    /// <summary>Delete, or Cmd-Delete on a Mac: the selection comes off the roadmap, as from the bar menu.</summary>
    [JSInvokable]
    public Task OnRemoveSelected() => InvokeAsync(async () =>
    {
        if (CanWrite && _selected.Count > 0)
        {
            await RemoveAsync(_entries.Where(e => _selected.Contains(e.Id)).ToList());
            StateHasChanged();
        }
    });

    /// <summary>
    /// A row dropped at <paramref name="index"/> among the others, taking the
    /// rest of the selection with it when it's part of one: those land together
    /// where it was dropped, in the order they had. Reordered here at once, then
    /// saved; a failed save puts the rows back.
    /// </summary>
    [JSInvokable]
    public Task OnRowDrop(string entryId, int index) => InvokeAsync(async () =>
    {
        if (!CanWrite || Find(entryId) is not { } dropped)
        {
            return;
        }

        var before = _entries.ToList();
        var moving = Dragged(dropped);
        var movingIds = moving.Select(e => e.Id).ToHashSet();

        // roadmap.js counts the index among every row but the one dragged; the
        // slot it means in the whole list is the same either way, and the rows
        // land after however many staying put come before it.
        var from = before.IndexOf(dropped);
        var slot = Math.Clamp(index >= from ? index + 1 : index, 0, before.Count);
        var at = before.Take(slot).Count(e => !movingIds.Contains(e.Id));

        var after = before.Where(e => !movingIds.Contains(e.Id)).ToList();
        after.InsertRange(at, moving);
        if (after.SequenceEqual(before))
        {
            return;
        }

        await using var saving = Saving();
        _entries = after;
        var preceding = at > 0 ? after[at - 1].Id : (Guid?)null;
        var following = at + moving.Count < after.Count ? after[at + moving.Count].Id : (Guid?)null;
        StateHasChanged();
        try
        {
            if (moving.Count == 1)
            {
                await Api.RerankAsync(_project!.Id, RoadmapId, dropped.Id, preceding, following);
            }
            else
            {
                await Api.RerankManyAsync(_project!.Id, RoadmapId, [.. movingIds], preceding, following);
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            _entries = before;
            var what = moving.Count == 1 ? Serial(dropped) : $"{moving.Count} tasks";
            Snackbar.Add($"Couldn't move {what}: {e.Message}", Severity.Error);
            StateHasChanged();
        }
    });

    /// <summary>
    /// A row dragged <paramref name="days"/> along the timeline, with the rest of
    /// the selection when it's part of one: every bar moves the same number of
    /// days, keeping its length, and they all stop when the first reaches the
    /// window's edge. A task with dates is rescheduled through core like any
    /// resize; a no-deadline pill has no dates to move, so the roadmap moves the
    /// day it's drawn on.
    /// </summary>
    /// <remarks>
    /// Returns once the bars are redrawn in their new places and leaves the saves
    /// to finish on their own, so the bars settle the moment they're dropped
    /// rather than staying lifted until the server answers. Each save puts its
    /// own bar back if it fails.
    /// </remarks>
    [JSInvokable]
    public Task OnMove(string entryId, int days) => InvokeAsync(() =>
    {
        if (!CanWrite || Find(entryId) is not { } entry)
        {
            return;
        }

        var moving = Dragged(entry);
        var shift = moving.Aggregate(days, (shift, e) => _window.ClampMove(Timeline.Span(e.Task, e.AnchorDay), shift));
        if (shift == 0)
        {
            return;
        }

        _ = moving.Count == 1 ? ReportAsync(ShiftAsync(entry, shift)) : ShiftTogetherAsync(moving, shift);
    });

    /// <summary>What a drag of this bar takes along: the whole selection when the bar is in it, else the bar alone.</summary>
    private List<RoadmapEntryDto> Dragged(RoadmapEntryDto entry) =>
        _selected.Count > 1 && _selected.Contains(entry.Id)
            ? _entries.Where(e => _selected.Contains(e.Id)).ToList()
            : [entry];

    /// <summary>
    /// Saves every bar's move at once, and says once how many didn't stick
    /// rather than once per bar.
    /// </summary>
    private async Task ShiftTogetherAsync(IReadOnlyList<RoadmapEntryDto> moving, int days)
    {
        var failures = (await Task.WhenAll(moving.Select(e => ShiftAsync(e, days)))).OfType<SaveFailure>().ToList();
        if (failures.Count > 0)
        {
            Snackbar.Add($"{failures.Count} of {moving.Count} tasks couldn't move: {failures[0].Message}",
                Severity.Warning);
        }
    }

    private Task<SaveFailure?> ShiftAsync(RoadmapEntryDto entry, int days)
    {
        if (entry.Task.DeadlineAt is null)
        {
            return SaveAnchorAsync(entry, entry.AnchorDay.AddDays(days));
        }

        var moved = Timeline.Move(Timeline.Span(entry.Task, entry.AnchorDay), days);
        return SaveScheduleAsync(entry, (moved.Start, moved.End));
    }

    /// <summary>A bar's edge dropped on column <paramref name="index"/>.</summary>
    [JSInvokable]
    public Task OnResize(string entryId, string edge, int index) => InvokeAsync(async () =>
    {
        if (CanWrite && Find(entryId) is { } entry)
        {
            var side = edge == "start" ? Edge.Start : Edge.End;
            await RescheduleAsync(entry, Timeline.Resize(entry.Task, entry.AnchorDay, side, _window.DayAt(index)));
        }
    });

    // -- Rescheduling -------------------------------------------------------------

    /// <summary>Why a save didn't stick, for whoever reports it: once per bar, or once for a group.</summary>
    private sealed record SaveFailure(string Message, Severity Severity);

    private Task RescheduleAsync(RoadmapEntryDto entry, (DateOnly? Start, DateOnly Deadline) dates) =>
        ReportAsync(SaveScheduleAsync(entry, dates));

    private async Task ReportAsync(Task<SaveFailure?> save)
    {
        if (await save is { } failure)
        {
            Snackbar.Add(failure.Message, failure.Severity);
        }
    }

    /// <summary>
    /// Saves new dates through core's schedule endpoint. The bar moves at once,
    /// before the first await; a conflict redraws it from the server's copy, and
    /// any other failure puts it back where it was.
    /// </summary>
    private async Task<SaveFailure?> SaveScheduleAsync(RoadmapEntryDto entry, (DateOnly? Start, DateOnly Deadline) dates)
    {
        var before = entry.Task;
        if (before.StartAt == dates.Start && before.DeadlineAt == dates.Deadline)
        {
            return null;
        }

        await using var saving = Saving();
        Replace(entry.Id, before with { StartAt = dates.Start, DeadlineAt = dates.Deadline });
        StateHasChanged();
        SaveFailure? failure = null;
        try
        {
            Replace(entry.Id, await Api.SetScheduleAsync(before.Id, dates.Start, dates.Deadline, before.Version));
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            await RefetchTaskAsync(entry.Id, before);
            failure = new SaveFailure("Someone else changed this task. Try again.", Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Replace(entry.Id, before);
            failure = new SaveFailure(e.Message, Severity.Error);
        }

        StateHasChanged();
        return failure;
    }

    /// <summary>Moves a pill at once, before the first await; a failed save puts it back.</summary>
    private async Task<SaveFailure?> SaveAnchorAsync(RoadmapEntryDto entry, DateOnly day)
    {
        await using var saving = Saving();
        var index = _entries.FindIndex(e => e.Id == entry.Id);
        _entries[index] = entry with { AnchorDay = day };
        StateHasChanged();
        try
        {
            await Api.SetAnchorAsync(_project!.Id, RoadmapId, entry.Id, day);
            return null;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            if (_entries.FindIndex(x => x.Id == entry.Id) is var at and >= 0)
            {
                _entries[at] = _entries[at] with { AnchorDay = entry.AnchorDay };
            }

            StateHasChanged();
            return new SaveFailure($"Couldn't move {Serial(entry)}: {e.Message}", Severity.Error);
        }
    }

    private async Task RefetchTaskAsync(Guid entryId, TaskDto fallback)
    {
        try
        {
            Replace(entryId, await Api.TaskAsync(fallback.Id));
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Replace(entryId, fallback);
        }
    }

    /// <summary>
    /// Keyboard resizing: Shift+←/→ moves the deadline a day, Alt+Shift+←/→ the
    /// start. Enter opens the task.
    /// </summary>
    private async Task OnBarKeyDownAsync(RoadmapEntryDto entry, KeyboardEventArgs e)
    {
        if (e.Key is "Enter" or " ")
        {
            await OpenTaskAsync(entry);
            return;
        }

        if (!CanWrite || !e.ShiftKey || e.Key is not ("ArrowLeft" or "ArrowRight"))
        {
            return;
        }

        var step = e.Key == "ArrowLeft" ? -1 : 1;
        var span = Timeline.Span(entry.Task, entry.AnchorDay);
        var edge = e.AltKey ? Edge.Start : Edge.End;
        var day = _window.Clamp((edge == Edge.Start ? span.Start : span.End).AddDays(step));
        await RescheduleAsync(entry, Timeline.Resize(entry.Task, entry.AnchorDay, edge, day));
    }

    // -- Milestones -------------------------------------------------------------------

    /// <summary>Shift+←/→ moves a milestone a day, as it does a bar's deadline. Enter opens its dialog.</summary>
    private async Task OnMilestoneKeyDownAsync(RoadmapMilestoneDto milestone, KeyboardEventArgs e)
    {
        if (!CanWrite)
        {
            return;
        }

        if (e.Key is "Enter" or " ")
        {
            await EditMilestoneAsync(milestone);
        }
        else if (e.ShiftKey && e.Key is "ArrowLeft" or "ArrowRight")
        {
            var day = _window.Clamp(milestone.Day.AddDays(e.Key == "ArrowLeft" ? -1 : 1));
            await SaveMilestoneAsync(milestone, milestone with { Day = day });
        }
    }

    private async Task AddMilestoneAsync(DateOnly day)
    {
        if (!CanWrite || _project is null)
        {
            return;
        }

        var result = await AskAsync<RoadmapMilestoneDialog>("Add a milestone",
            new DialogParameters<RoadmapMilestoneDialog> { { d => d.InitialDay, day } });
        if (result is not { Canceled: false, Data: RoadmapMilestoneDialog.MilestoneEdit edit })
        {
            return;
        }

        await using var saving = Saving();
        try
        {
            var added = await Api.AddMilestoneAsync(_project.Id, RoadmapId,
                new CreateRoadmapMilestoneRequest { Name = edit.Name, Day = edit.Day, Color = edit.Color });
            PutMilestone(added);
            if (added.Day < _window.First || added.Day > _window.Last)
            {
                Snackbar.Add($"Added. {Dates.Day(added.Day)} is outside this roadmap's range, so its line doesn't show.",
                    Severity.Info);
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add($"Couldn't add the milestone: {e.Message}", Severity.Error);
        }

        StateHasChanged();
    }

    private async Task EditMilestoneAsync(RoadmapMilestoneDto milestone)
    {
        var result = await AskAsync<RoadmapMilestoneDialog>("Edit milestone",
            new DialogParameters<RoadmapMilestoneDialog>
            {
                { d => d.InitialName, milestone.Name },
                { d => d.InitialDay, milestone.Day },
                { d => d.HexColor, milestone.Color },
                { d => d.SaveText, "Save" },
                { d => d.CanDelete, true },
            });
        if (result is not { Canceled: false, Data: RoadmapMilestoneDialog.MilestoneEdit edit })
        {
            return;
        }

        if (ReferenceEquals(edit, RoadmapMilestoneDialog.MilestoneEdit.Deleted))
        {
            await DeleteMilestoneAsync(milestone);
        }
        else
        {
            // Saved with the version the dialog opened on, so someone else's
            // change made meanwhile is a conflict rather than overwritten.
            await SaveMilestoneAsync(milestone, milestone with { Name = edit.Name, Day = edit.Day, Color = edit.Color });
        }
    }

    /// <summary>
    /// Saves a milestone's new name, day or colour. It's redrawn at once, before
    /// the first await; a conflict re-reads the roadmap, and any other failure
    /// puts it back.
    /// </summary>
    private async Task SaveMilestoneAsync(RoadmapMilestoneDto before, RoadmapMilestoneDto after)
    {
        if (before == after || _project is null)
        {
            return;
        }

        await using var saving = Saving();
        PutMilestone(after);
        StateHasChanged();
        try
        {
            PutMilestone(await Api.UpdateMilestoneAsync(_project.Id, RoadmapId, before.Id,
                new UpdateRoadmapMilestoneRequest
                {
                    Name = after.Name,
                    Day = after.Day,
                    Color = after.Color,
                    Version = before.Version,
                }));
        }
        catch (SoloApiException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.NotFound)
        {
            _stale = true;
            Snackbar.Add(e.StatusCode == HttpStatusCode.NotFound
                ? "Someone else deleted that milestone."
                : "Someone else changed that milestone first. Try again.", Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            PutMilestone(before);
            Snackbar.Add($"Couldn't save {before.Name}: {e.Message}", Severity.Error);
        }

        StateHasChanged();
    }

    private async Task DeleteMilestoneAsync(RoadmapMilestoneDto milestone)
    {
        if (_project is null)
        {
            return;
        }

        await using var saving = Saving();
        try
        {
            await Api.DeleteMilestoneAsync(_project.Id, RoadmapId, milestone.Id);
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Gone already, which is what was wanted.
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add($"Couldn't delete {milestone.Name}: {e.Message}", Severity.Error);
            return;
        }

        _milestones.RemoveAll(m => m.Id == milestone.Id);
        Snackbar.Add($"Deleted the milestone {milestone.Name}.", Severity.Info);
        StateHasChanged();
    }

    /// <summary>Adds or replaces a milestone, keeping the list in day order.</summary>
    private void PutMilestone(RoadmapMilestoneDto milestone)
    {
        _milestones.RemoveAll(m => m.Id == milestone.Id);
        _milestones.Add(milestone);
        _milestones.Sort((a, b) => a.Day.CompareTo(b.Day));
    }

    // -- Adding and removing ----------------------------------------------------------

    private async Task PickAsync(DateOnly day)
    {
        if (!CanWrite || _project is null)
        {
            return;
        }

        var project = _project.Id;
        var picked = await WhileEditingAsync(() => Picker.PickAsync(project, new TaskPickerOptions
        {
            Title = $"Add a task on {Dates.Day(day)}",
            Exclude = _entries.Select(e => e.Task.Id).ToHashSet(),
            AllExcludedText = "Every task in this project is already on this roadmap.",
        }));

        if (picked is not null)
        {
            await AddAsync(picked, day);
        }
    }

    /// <summary>
    /// Puts the task on the roadmap. A task with no dates gets them from the chosen
    /// day (see <see cref="Timeline.ScheduleOnAdd"/>); one that has dates keeps
    /// them — nobody's deadline moves without them seeing it — and the view goes
    /// to where its bar is.
    /// </summary>
    private async Task AddAsync(TaskDto picked, DateOnly day)
    {
        await using var saving = Saving();
        RoadmapEntryDto entry;
        try
        {
            entry = await Api.AddEntryAsync(_project!.Id, RoadmapId, picked.Id, day);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
            return;
        }

        _entries.Add(entry);
        await LoadLabelsAsync();

        if (entry.Task.DeadlineAt is null)
        {
            var dates = Timeline.ScheduleOnAdd(day, _today);
            if (await ScheduleOnAddAsync(entry.Task, dates) is { } scheduled)
            {
                Replace(entry.Id, scheduled);
                if (dates.End != dates.Start && scheduled.DeadlineAt == dates.End)
                {
                    Snackbar.Add($"Added. {Dates.Day(day)} has passed, so it runs through today.", Severity.Info);
                }
            }
        }
        else
        {
            var span = Timeline.Span(entry.Task, entry.AnchorDay);
            Snackbar.Add($"Added. It keeps its existing dates ({Dates.Range(span.Start, span.End)}).", Severity.Info);
        }

        StateHasChanged();
        await ScrollToAsync(entry.Id);
    }

    /// <summary>Gives a dateless task its first dates — retrying once over a stale version.</summary>
    private async Task<TaskDto?> ScheduleOnAddAsync(TaskDto task, DaySpan dates)
    {
        try
        {
            return await Api.SetScheduleAsync(task.Id, dates.Start, dates.End, task.Version);
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            try
            {
                var fresh = await Api.TaskAsync(task.Id);
                if (fresh.DeadlineAt is not null)
                {
                    // Someone dated it in the meantime; theirs stands.
                    return fresh;
                }

                return await Api.SetScheduleAsync(fresh.Id, dates.Start, dates.End, fresh.Version);
            }
            catch (Exception retry) when (retry is SoloApiException or HttpRequestException)
            {
                Snackbar.Add($"Added, but its date couldn't be set: {retry.Message}", Severity.Warning);
                return null;
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add($"Added, but its date couldn't be set: {e.Message}", Severity.Warning);
            return null;
        }
    }

    /// <summary>
    /// Brings a row into view both ways: a task added from a gap beside another
    /// bar lands at the end, which on a long roadmap is below the fold.
    /// </summary>
    private async Task ScrollToAsync(Guid entryId)
    {
        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index < 0 || _timelineHandle is null)
        {
            return;
        }

        var entry = _entries[index];
        var span = Timeline.Span(entry.Task, entry.AnchorDay);
        await ScrollToDayAsync(_window.IndexOf(span.Start) - 2);
        await _timelineHandle.InvokeVoidAsync("revealRow", index);
    }

    /// <summary>
    /// Takes the tasks off the roadmap, in one request unless there are more
    /// than one request takes. They go from the page once the server has them
    /// gone; if a request fails part way, the page re-reads to show what's left.
    /// </summary>
    private async Task RemoveAsync(IReadOnlyList<RoadmapEntryDto> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        await using var saving = Saving();
        var ids = targets.Select(t => t.Id).ToHashSet();
        try
        {
            foreach (var chunk in ids.Chunk(RoadmapLimits.MaxEntriesAtOnce))
            {
                await Api.RemoveEntriesAsync(_project!.Id, RoadmapId, chunk);
            }
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            if (targets.Count > RoadmapLimits.MaxEntriesAtOnce)
            {
                _stale = true;
            }

            Snackbar.Add(e.Message, Severity.Error);
            return;
        }

        _entries.RemoveAll(e => ids.Contains(e.Id));
        _selected.ExceptWith(ids);
        Snackbar.Add(targets.Count == 1
            ? $"Removed {Serial(targets[0])} from the roadmap."
            : $"Removed {targets.Count} tasks from the roadmap.", Severity.Info);
    }

    // -- The task drawer --------------------------------------------------------------

    private async Task OpenTaskAsync(RoadmapEntryDto entry)
    {
        try
        {
            await Drawer.OpenAsync(entry.Task.Id);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    /// <summary>Keeps a row in step with what the drawer did to its task.</summary>
    private void OnDrawerChanged(TaskDrawerChange change) => _ = InvokeAsync(async () =>
    {
        var entry = _entries.FirstOrDefault(e => e.Task.Id == change.TaskId);
        if (entry is null)
        {
            return;
        }

        _edits++;

        switch (change.Kind)
        {
            case TaskDrawerChangeKind.Saved when change.Task is { } saved:
                Replace(entry.Id, saved);
                break;
            case TaskDrawerChangeKind.Deleted:
                _entries.Remove(entry);
                _selected.Remove(entry.Id);
                break;
            case TaskDrawerChangeKind.Moved:
                // Still in the project, on another board: the board alias in the
                // row is stale, and the timeline read knows the new one.
                await ReloadAsync();
                break;
        }

        StateHasChanged();
    });

    // -- Selection and the menus ---------------------------------------------------------

    private const string AddItem = "add";
    private const string AddMilestoneItem = "add-milestone";
    private const string EditItem = "edit";
    private const string OpenItem = "open";
    private const string RemoveItem = "remove";

    private void Select(RoadmapEntryDto entry, bool range)
    {
        var to = _entries.IndexOf(entry);
        if (range && _anchor is { } anchor && _entries.FindIndex(e => e.Id == anchor) is var from and >= 0)
        {
            _selected.Clear();
            _selected.UnionWith(_entries
                .Skip(Math.Min(from, to))
                .Take(Math.Abs(to - from) + 1)
                .Select(e => e.Id));
            return;
        }

        if (!_selected.Add(entry.Id))
        {
            _selected.Remove(entry.Id);
        }

        _anchor = entry.Id;
    }

    /// <summary>
    /// The menu for a bar, from a right-click, a long press or its ⋯ button.
    /// On a selected bar it acts on the whole selection; on any other it
    /// selects that bar alone first, so what the menu acts on is always what's
    /// highlighted.
    /// </summary>
    private async Task ShowBarMenuAsync(RoadmapEntryDto entry, double clientX, double clientY)
    {
        if (CanWrite && !_selected.Contains(entry.Id))
        {
            _selected.Clear();
            _selected.Add(entry.Id);
            _anchor = entry.Id;
            StateHasChanged();
        }

        var targets = CanWrite ? _entries.Where(e => _selected.Contains(e.Id)).ToList() : [entry];
        if (targets.Count == 0)
        {
            return;
        }

        var items = new List<ContextMenuItem>();
        if (targets.Count == 1)
        {
            items.Add(new ContextMenuItem(OpenItem, "Open task") { Icon = Icons.Material.Filled.OpenInNew });
        }

        if (CanWrite)
        {
            items.Add(new ContextMenuItem(RemoveItem, targets.Count == 1
                ? "Remove from roadmap"
                : $"Remove {targets.Count} tasks from roadmap")
            {
                Icon = Icons.Material.Filled.RemoveCircleOutline,
                Danger = true,
                SeparatorBefore = true,
            });
        }

        switch (await Menu.ShowAsync(clientX, clientY, items))
        {
            case OpenItem:
                await OpenTaskAsync(targets[0]);
                break;
            case RemoveItem:
                await RemoveAsync(targets);
                StateHasChanged();
                break;
        }
    }

    // -- The roadmap itself -------------------------------------------------------------

    private async Task RenameAsync()
    {
        if (_timeline is null || _project is null)
        {
            return;
        }

        var result = await AskAsync<RoadmapTitleDialog>("Rename roadmap",
            new DialogParameters<RoadmapTitleDialog>
            {
                { d => d.InitialTitle, _timeline.Title },
                { d => d.SaveText, "Rename" },
            });
        if (result is not { Canceled: false, Data: string title } || title == _timeline.Title)
        {
            return;
        }

        await using var saving = Saving();
        try
        {
            var renamed = await Api.RenameAsync(_project.Id, RoadmapId, title, _timeline.Version);
            _timeline = _timeline with { Title = renamed.Title, Version = renamed.Version };
            RoadmapNavEvents.Raise(_project.Id);
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            await ReloadAsync();
            Snackbar.Add("Someone else changed this roadmap first. It's been refreshed; try again.", Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    private async Task DeleteAsync()
    {
        if (_timeline is null || _project is null)
        {
            return;
        }

        var result = await AskAsync<RoadmapConfirmDialog>("Delete roadmap",
            new DialogParameters<RoadmapConfirmDialog>
            {
                { d => d.Message, $"Delete the roadmap \"{_timeline.Title}\"? Its tasks stay where they are." },
            });
        if (result is not { Canceled: false })
        {
            return;
        }

        try
        {
            await Api.DeleteAsync(_project.Id, RoadmapId);
            RoadmapNavEvents.Raise(_project.Id);
            Navigation.NavigateTo($"projects/{_project.Alias}");
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
    }

    /// <summary>Shows a dialog, with this person reported as editing for as long as it's open.</summary>
    private Task<DialogResult?> AskAsync<TDialog>(
        string title, DialogParameters<TDialog> parameters, DialogOptions? options = null)
        where TDialog : IComponent =>
        WhileEditingAsync(async () =>
        {
            var dialog = await Dialogs.ShowAsync<TDialog>(title, parameters, options ?? new DialogOptions());
            return await dialog.Result;
        });

    /// <summary>
    /// Reports this person as editing while <paramref name="ask"/> has a dialog
    /// open, the host's task picker included.
    /// </summary>
    private async Task<T> WhileEditingAsync<T>(Func<Task<T>> ask)
    {
        _inDialog = true;
        await ReportPresenceAsync();
        try
        {
            return await ask();
        }
        finally
        {
            _inDialog = false;
            await ReportPresenceAsync();
        }
    }

    // -- Everyone else on the roadmap ---------------------------------------------------

    private async Task StartLiveAsync()
    {
        _live = await Live.SubscribeAsync(_project!.Id, RoadmapTopics.Roadmap(RoadmapId), OnLiveAsync, OnPresenceAsync);
        await ReportPresenceAsync();
    }

    /// <remarks>Leaving the topic takes this person's avatar off everyone else's page with it.</remarks>
    private async Task StopLiveAsync()
    {
        var live = _live;
        _live = null;
        _present = [];
        _reported = PresenceMode.None;
        if (live is not null)
        {
            await live.DisposeAsync();
        }
    }

    /// <summary>Tells the others here whether this person is looking, or in the middle of changing something.</summary>
    private async Task ReportPresenceAsync()
    {
        if (_live is null || _project is null)
        {
            return;
        }

        var mode = _gesturing || _inDialog ? PresenceMode.Editing : PresenceMode.Viewing;
        if (mode != _reported)
        {
            _reported = mode;
            await Live.SetPresenceAsync(_project.Id, RoadmapTopics.Roadmap(RoadmapId), mode);
        }
    }

    private Task OnPresenceAsync(IReadOnlyList<TopicPresenceEntry> present) => InvokeAsync(() =>
    {
        _present = present
            .OrderByDescending(p => p.Mode)
            .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        StateHasChanged();
    });

    /// <summary>Someone else changed the roadmap, or a task on it.</summary>
    private Task OnLiveAsync(TopicMessage message) => InvokeAsync(async () =>
    {
        if (message.Kind == RoadmapTopics.Deleted)
        {
            await GoneAsync();
            return;
        }

        _stale = true;
        await CatchUpAsync();
    });

    /// <summary>
    /// Takes the server's copy once nothing of this person's is in flight.
    /// Mid-gesture the rows are the browser's to move; with a save on its way the
    /// server's copy doesn't have it yet, and would snap the row back until it
    /// lands. Whatever's heard meanwhile folds into one read once both settle.
    /// </summary>
    private async Task CatchUpAsync()
    {
        if (!_stale || _catchingUp || _gesturing || _saving > 0 ||
            _project is null || _timeline is null || _error is not null)
        {
            return;
        }

        _stale = false;
        _catchingUp = true;
        var edits = _edits;
        try
        {
            var timeline = await Api.TimelineAsync(_project.Id, RoadmapId);
            if (edits != _edits || _gesturing || _saving > 0)
            {
                // This person changed something while the read was on its way.
                _stale = true;
            }
            else
            {
                Apply(timeline);
                StateHasChanged();
                if (await LoadLabelsAsync())
                {
                    StateHasChanged();
                }
            }
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await GoneAsync();
            return;
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            // Behind until the next change or a reload: a warning for every missed
            // nudge would be noise about something the page can't fix.
        }
        finally
        {
            _catchingUp = false;
        }

        await CatchUpAsync();
    }

    /// <summary>Deleted by someone else while it was open.</summary>
    private async Task GoneAsync()
    {
        await StopLiveAsync();
        await DetachTimelineAsync();
        _selected.Clear();
        _error = "Someone deleted this roadmap while you had it open. Its tasks are still on their boards.";
        StateHasChanged();
    }

    /// <summary>
    /// Marks one of this person's saves as in flight, so a live change waits for
    /// it (see <see cref="CatchUpAsync"/>). Taken before the first await, so no
    /// read can start between the redraw and the save.
    /// </summary>
    private SaveInFlight Saving()
    {
        _saving++;
        _edits++;
        return new SaveInFlight(this);
    }

    private sealed class SaveInFlight(RoadmapPage page) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            page._saving--;
            await page.CatchUpAsync();
        }
    }

    private ProjectMemberDto PresentMember(TopicPresenceEntry person) =>
        _members.GetValueOrDefault(person.UserId)
        ?? new ProjectMemberDto(person.UserId, person.UserId, "", person.DisplayName, null, ProjectRole.Read, false, false);

    private string DescribePresence(TopicPresenceEntry person)
    {
        var member = PresentMember(person);
        var name = member.DisplayName ?? (member.Email is { Length: > 0 } email ? email : "Someone");
        return $"{name} is {(person.Mode == PresenceMode.Editing ? "editing" : "viewing")} this roadmap";
    }

    // -- Rendering helpers -------------------------------------------------------------

    private RoadmapEntryDto? Find(string entryId) =>
        Guid.TryParse(entryId, out var id) ? _entries.FirstOrDefault(e => e.Id == id) : null;

    private RoadmapMilestoneDto? FindMilestone(string milestoneId) =>
        Guid.TryParse(milestoneId, out var id) ? _milestones.FirstOrDefault(m => m.Id == id) : null;

    private void Replace(Guid entryId, TaskDto task)
    {
        var index = _entries.FindIndex(e => e.Id == entryId);
        if (index >= 0)
        {
            _entries[index] = _entries[index] with { Task = task };
        }
    }

    private string Serial(RoadmapEntryDto entry) =>
        Aliases.Serial(_project?.Alias ?? ProjectAlias, entry.BoardAlias, entry.Task.Number);

    private IReadOnlyList<ProjectMemberDto> Assignees(TaskDto task) =>
        task.AssigneeIds.Select(id => _members.GetValueOrDefault(id)).OfType<ProjectMemberDto>().ToList();

    /// <summary>Missed rows, most overdue first — the board's own rule.</summary>
    private List<RoadmapEntryDto> Missed() => _entries
        .Where(e => Timeline.IsMissed(e.Task, _today))
        .OrderByDescending(e => Timeline.DaysOverdue(e.Task, _today))
        .ToList();

    private string BodyStyle
    {
        get
        {
            var settings = _timeline!.Settings;
            var sprintDays = settings.SprintLengthWeeks * 7;
            var period = 2 * sprintDays * DayWidth;
            var offset = _window.StripeOffset(RoadmapSprints.Anchor(settings.SprintStartDay), sprintDays, DayWidth);
            return FormattableString.Invariant(
                $"height:{Timeline.Rows(_entries.Count) * RowHeight}px;--roadmap-stripe-period:{period}px;--roadmap-stripe-offset:{offset}px");
        }
    }

    private string BarClass(RoadmapEntryDto entry) =>
        "roadmap-bar" +
        (_selected.Contains(entry.Id) ? " roadmap-bar--selected" : "") +
        (entry.Task.DeadlineAt is null ? " roadmap-bar--pill" : "") +
        (entry.Task.Status == TaskState.Finished ? " roadmap-bar--finished" : "") +
        (Timeline.IsMissed(entry.Task, _today) ? " roadmap-bar--missed" : "");

    private string BarStyle(RoadmapEntryDto entry, BarPlacement place)
    {
        var style = $"--bar-left:{place.StartIndex * DayWidth}px;--bar-width:{place.Length * DayWidth}px";
        return entry.Task.LabelId is { } labelId && _labels.TryGetValue(labelId, out var label)
            ? $"{style};--bar-label:{label.Color}"
            : style;
    }

    private string BarTitle(RoadmapEntryDto entry, DaySpan span)
    {
        var when = entry.Task.DeadlineAt is null
            ? $"no deadline yet, added {Dates.Day(span.Start)}"
            : Dates.Range(span.Start, span.End);
        return $"{Serial(entry)} {entry.Task.Name} ({when})";
    }

    /// <summary>The milestones whose day is inside the window, and so have a line to draw.</summary>
    private IEnumerable<RoadmapMilestoneDto> Placed() =>
        _milestones.Where(m => m.Day >= _window.First && m.Day <= _window.Last);

    private string MilestoneClass(RoadmapMilestoneDto milestone, string element) =>
        element +
        (CanWrite ? " roadmap-milestone--movable" : "") +
        (Timeline.IsMilestoneDue(milestone.Day, _today, _timeline!.Settings) ? " roadmap-milestone--due" : "");

    /// <summary>Where the line sits — flush with the end of its day — and its colours.</summary>
    private string MilestoneStyle(RoadmapMilestoneDto milestone) => FormattableString.Invariant(
        $"--milestone-x:{(_window.IndexOf(milestone.Day) + 1) * DayWidth}px;--milestone:{milestone.Color};--milestone-ink:{MilestoneColors.Ink(milestone.Color)}");

    private static string MilestoneTitle(RoadmapMilestoneDto milestone) =>
        $"{milestone.Name} (milestone, due {Dates.Day(milestone.Day)})";

    private string DayClass(DateOnly day) =>
        "roadmap-day" +
        (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? " roadmap-day--weekend" : "") +
        (day == _today ? " roadmap-day--today" : "");

    private static string Weekday(DateOnly day) =>
        CultureInfo.CurrentCulture.DateTimeFormat.GetAbbreviatedDayName(day.DayOfWeek)[..1];

    private sealed record MonthSegment(int First, int Length, bool StartsMonth, string Label);

    /// <summary>The month band for the rendered columns: one segment per month they touch.</summary>
    private IEnumerable<MonthSegment> Months()
    {
        var index = _rendered.First;
        while (index <= _rendered.Last)
        {
            var day = _window.DayAt(index);
            var monthEnd = _window.IndexOf(new DateOnly(day.Year, day.Month, 1).AddMonths(1)) - 1;
            var last = Math.Min(monthEnd, _rendered.Last);
            yield return new MonthSegment(index, last - index + 1, day.Day == 1,
                day.ToString("MMM yyyy", CultureInfo.CurrentCulture));
            index = last + 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Drawer.Changed -= OnDrawerChanged;
        await StopLiveAsync();
        await DetachTimelineAsync();
        _self?.Dispose();
    }
}
