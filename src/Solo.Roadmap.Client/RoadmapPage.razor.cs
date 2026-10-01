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
    private TimelineWindow _window = new(default, default);
    private DateOnly _today;
    private (int First, int Last) _rendered;
    private Dictionary<Guid, ProjectMemberDto> _members = [];
    private readonly Dictionary<Guid, LabelDto> _labels = [];
    private readonly HashSet<Guid> _labelBoards = [];
    private MenuState? _menu;
    private string? _error;

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
        _menu = null;
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
        if (_menu is { } menu && _entries.All(e => e.Id != menu.Entry.Id))
        {
            _menu = null;
        }
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

    [JSInvokable]
    public Task OnBarClick(string entryId) =>
        Find(entryId) is { } entry ? InvokeAsync(() => OpenTaskAsync(entry)) : Task.CompletedTask;

    [JSInvokable]
    public Task OnBarMenu(string entryId, double clientX, double clientY) => InvokeAsync(() =>
    {
        if (Find(entryId) is { } entry)
        {
            OpenMenu(entry, clientX, clientY);
            StateHasChanged();
        }
    });

    [JSInvokable]
    public Task OnDayClick(int index) => InvokeAsync(() => PickAsync(_window.DayAt(index)));

    /// <summary>
    /// A row dropped at <paramref name="index"/> among the others. Reordered here
    /// at once, then saved; a failed save puts the rows back.
    /// </summary>
    [JSInvokable]
    public Task OnRowDrop(string entryId, int index) => InvokeAsync(async () =>
    {
        if (!CanWrite || Find(entryId) is not { } moved)
        {
            return;
        }

        var before = _entries.ToList();
        _entries.Remove(moved);
        var at = Math.Clamp(index, 0, _entries.Count);
        _entries.Insert(at, moved);
        if (_entries.SequenceEqual(before))
        {
            return;
        }

        await using var saving = Saving();
        var preceding = at > 0 ? _entries[at - 1].Id : (Guid?)null;
        var following = at < _entries.Count - 1 ? _entries[at + 1].Id : (Guid?)null;
        StateHasChanged();
        try
        {
            await Api.RerankAsync(_project!.Id, RoadmapId, moved.Id, preceding, following);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            _entries = before;
            Snackbar.Add($"Couldn't move {Serial(moved)}: {e.Message}", Severity.Error);
            StateHasChanged();
        }
    });

    /// <summary>
    /// A row dragged <paramref name="days"/> along the timeline: both ends move
    /// together, so it keeps its length, and it stops at the window's edge. A task
    /// with dates is rescheduled through core like any resize; a no-deadline pill
    /// has no dates to move, so the roadmap moves the day it's drawn on.
    /// </summary>
    /// <remarks>
    /// Returns once the bar is redrawn in its new place and leaves the save to
    /// finish on its own, so the bar settles the moment it's dropped rather than
    /// staying lifted until the server answers. Both saves handle their own
    /// failures, putting the bar back.
    /// </remarks>
    [JSInvokable]
    public Task OnMove(string entryId, int days) => InvokeAsync(() =>
    {
        if (!CanWrite || Find(entryId) is not { } entry)
        {
            return;
        }

        var span = Timeline.Span(entry.Task, entry.AnchorDay);
        var shift = _window.ClampMove(span, days);
        if (shift == 0)
        {
            return;
        }

        if (entry.Task.DeadlineAt is null)
        {
            _ = MoveAnchorAsync(entry, entry.AnchorDay.AddDays(shift));
            return;
        }

        var moved = Timeline.Move(span, shift);
        _ = RescheduleAsync(entry, (moved.Start, moved.End));
    });

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

    /// <summary>
    /// Saves new dates through core's schedule endpoint. The bar moves at once;
    /// a conflict redraws it from the server's copy and says why, and any other
    /// failure puts it back where it was.
    /// </summary>
    private async Task RescheduleAsync(RoadmapEntryDto entry, (DateOnly? Start, DateOnly Deadline) dates)
    {
        var before = entry.Task;
        if (before.StartAt == dates.Start && before.DeadlineAt == dates.Deadline)
        {
            return;
        }

        await using var saving = Saving();
        Replace(entry.Id, before with { StartAt = dates.Start, DeadlineAt = dates.Deadline });
        StateHasChanged();
        try
        {
            Replace(entry.Id, await Api.SetScheduleAsync(before.Id, dates.Start, dates.Deadline, before.Version));
        }
        catch (SoloApiException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            await RefetchTaskAsync(entry.Id, before);
            Snackbar.Add("Someone else changed this task. Try again.", Severity.Warning);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Replace(entry.Id, before);
            Snackbar.Add(e.Message, Severity.Error);
        }

        StateHasChanged();
    }

    /// <summary>Moves a pill at once; a failed save puts it back.</summary>
    private async Task MoveAnchorAsync(RoadmapEntryDto entry, DateOnly day)
    {
        await using var saving = Saving();
        var index = _entries.FindIndex(e => e.Id == entry.Id);
        _entries[index] = entry with { AnchorDay = day };
        StateHasChanged();
        try
        {
            await Api.SetAnchorAsync(_project!.Id, RoadmapId, entry.Id, day);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            if (_entries.FindIndex(x => x.Id == entry.Id) is var at and >= 0)
            {
                _entries[at] = _entries[at] with { AnchorDay = entry.AnchorDay };
            }

            Snackbar.Add($"Couldn't move {Serial(entry)}: {e.Message}", Severity.Error);
            StateHasChanged();
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

    // -- Adding and removing ----------------------------------------------------------

    private async Task PickAsync(DateOnly day)
    {
        if (!CanWrite || _project is null)
        {
            return;
        }

        var result = await AskAsync<TaskPickerDialog>(
            $"Add a task on {Dates.Day(day)}",
            new DialogParameters<TaskPickerDialog>
            {
                { d => d.ProjectId, _project.Id },
                { d => d.ProjectAlias, _project.Alias },
                { d => d.RoadmapId, RoadmapId },
            },
            new DialogOptions { MaxWidth = MaxWidth.Small, FullWidth = true });

        if (result is { Canceled: false, Data: RoadmapCandidateDto picked })
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
    private async Task AddAsync(RoadmapCandidateDto picked, DateOnly day)
    {
        await using var saving = Saving();
        RoadmapEntryDto entry;
        try
        {
            entry = await Api.AddEntryAsync(_project!.Id, RoadmapId, picked.Task.Id, day);
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

    private async Task RemoveAsync(RoadmapEntryDto entry)
    {
        await using var saving = Saving();
        try
        {
            await Api.RemoveEntryAsync(_project!.Id, RoadmapId, entry.Id);
            _entries.RemoveAll(e => e.Id == entry.Id);
            Snackbar.Add($"Removed {Serial(entry)} from the roadmap.", Severity.Info);
        }
        catch (Exception e) when (e is SoloApiException or HttpRequestException)
        {
            Snackbar.Add(e.Message, Severity.Error);
        }
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
                break;
            case TaskDrawerChangeKind.Moved:
                // Still in the project, on another board: the board alias in the
                // row is stale, and the timeline read knows the new one.
                await ReloadAsync();
                break;
        }

        StateHasChanged();
    });

    // -- The bar menu -----------------------------------------------------------------

    private sealed record MenuState(RoadmapEntryDto Entry, double X, double Y);

    private void OpenMenu(RoadmapEntryDto entry, double clientX, double clientY) =>
        _menu = new MenuState(entry, clientX, clientY);

    private Task CloseMenu()
    {
        _menu = null;
        return Task.CompletedTask;
    }

    private async Task MenuOpenAsync(RoadmapEntryDto entry)
    {
        _menu = null;
        await OpenTaskAsync(entry);
    }

    private async Task MenuRemoveAsync(RoadmapEntryDto entry)
    {
        _menu = null;
        await RemoveAsync(entry);
    }

    private static string MenuStyle(MenuState menu) =>
        FormattableString.Invariant($"left:min({menu.X}px, calc(100vw - 13rem));top:min({menu.Y}px, calc(100vh - 7rem))");

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
    private async Task<DialogResult?> AskAsync<TDialog>(
        string title, DialogParameters<TDialog> parameters, DialogOptions? options = null)
        where TDialog : IComponent
    {
        _inDialog = true;
        await ReportPresenceAsync();
        try
        {
            var dialog = await Dialogs.ShowAsync<TDialog>(title, parameters, options ?? new DialogOptions());
            return await dialog.Result;
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
        _menu = null;
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
