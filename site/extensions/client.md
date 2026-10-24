---
title: Client half
lede: The client half is a Razor class library that the browser loads after sign-in. It holds your pages and the components that Solo draws in its own pages.
---

## The project

```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <StaticWebAssetsEnabled>false</StaticWebAssetsEnabled>
  </PropertyGroup>

  <ItemGroup>
    <SupportedPlatform Include="browser" />
    <PackageReference Include="HunterAhlquist.Solo.Shared" Version="1.10.0" />
    <PackageReference Include="Microsoft.AspNetCore.Components.Web" Version="10.0.13" />
    <PackageReference Include="MudBlazor" Version="9.11.0" />
  </ItemGroup>
</Project>
```

Three rules apply to this project:

- **Reference `HunterAhlquist.Solo.Shared`, not `HunterAhlquist.Solo.Extensibility`.** The second package depends on ASP.NET Core and EF Core. They do not build for the browser.
- **Use the versions of the instance** for `Microsoft.AspNetCore.Components.Web` and `MudBlazor`. The instance supplies these libraries at run time. If you compile against a newer MudBlazor, your code can call members that the instance does not have.
- **Set `StaticWebAssetsEnabled` to `false`.** Static files go in the `content/` folder of the package.

## How Solo loads the client half

1. A person signs in.
2. The browser gets the list of extensions and compares each `soloClientVersion` with its own contract version.
3. The browser downloads each client assembly that it accepts.
4. Solo adds the `@page` routes of the assembly to its router.

This has three results:

- Your components get MudBlazor, dialogs, navigation and JS interop from Solo. You do not register them.
- A client half cannot register services. Its assembly loads after Solo builds its service container.
- Your pages do not exist before sign-in. A change to the sign-in page is the work of a [theme](../themes/index.md), not of an extension.

## Pages and navigation

A page is a Razor component with a route:

```html
@page "/roadmap/{ProjectAlias}"
@attribute [Authorize]
@inject ISoloClientApi Api

<PageTitle>Roadmap · Solo</PageTitle>
<MudText Typo="Typo.h1">Roadmap</MudText>

@code {
    [Parameter] public string ProjectAlias { get; set; } = "";
}
```

To put a link to the page in the sidebar, add a `nav` entry to the manifest. See [Navigation entries](manifest.md#navigation-entries).

## Call the API

Inject `ISoloClientApi`. It is the API client of Solo itself, so your requests use the same sign-in and the same address.

```csharp
var plans = await Api.GetAsync<List<PlanDto>>($"api/ext/plans/projects/{projectId}/plans");

await Api.SendAsync(HttpMethod.Put, $"api/tasks/{taskId}/schedule",
    new { startAt, deadlineAt, version });
```

| Method | Purpose |
|---|---|
| `GetAsync<T>(path)` | A `GET` request. Returns the response as `T`. |
| `SendAsync<T>(method, path, body)` | A request with a response body. Solo sends `body` as JSON. |
| `SendAsync(method, path, body)` | A request with no response body. |
| `Resolve(path)` | A full address for a file that the browser gets. |
| `ResolveExternal(path)` | A full address that a person copies out of Solo. |

A response with an error status throws `SoloApiException`. The exception has the `StatusCode`, the `Title` and the `Detail` of the error.

Use the same client to change core data. A change then goes through authorization, hooks, the audit log and live updates.

### Resolve and ResolveExternal

The two methods give the same address in a browser. They give different addresses in the desktop app.

The desktop app serves the client from `http://localhost:41321` and sends API requests to the instance.

| Method | In the desktop app | Use it for |
|---|---|---|
| `Resolve` | `http://localhost:41321/…` | A stylesheet, a script or an image that the page loads. |
| `ResolveExternal` | The address of the instance | An address that a person pastes into a different program. |

If you show `Resolve` output to a person, the address works only on their own computer.

## Static content

Files in the `content/` folder of the package are available at:

```
/api/extensions/<id>/content/<path>
```

- **Solo serves `content/` without sign-in.** A `<link>` or an `<img>` request sends no credential, so Solo cannot check it. Put no secret in `content/`.
- **Add the version to each address.** Use `?v=` and your package version, for example `roadmap.js?v=1.7.0`. A proxy or the desktop app can keep an old file. Without the version, a new page can then load an old script.

Solo sets a content type for these file types: `.css`, `.js`, `.json`, `.png`, `.jpg`, `.jpeg`, `.webp`, `.gif`, `.svg`, `.woff`, `.woff2`.

### Add a stylesheet

Put the `<link>` in the markup of the page:

```html
<link rel="stylesheet" href="@Api.Resolve("api/extensions/plans/content/plans.css?v=1.0.0")" />
```

> Do not put a stylesheet in `<HeadContent>`. Solo uses `<HeadContent>` for the stylesheet and fonts of the theme. Only one `<HeadContent>` shows at a time. Your page would replace the theme of the full app while it is open.
{: .warning}

`<PageTitle>` has no such problem.

## Contribute to core pages

Solo draws your components in its own pages. To contribute, put an attribute on a component. Solo gives the component its parameters.

All attributes are in the namespace `Solo.Shared.Extensibility`.

| Attribute | Where Solo draws it | Parameters | Since |
|---|---|---|---|
| `[TaskDetailExtension]` | In the task drawer, below the assignees. | `Task`, `CanWrite`, `OnChanged` | 1.2 |
| `[ProjectNavSection("Title")]` | In the sidebar, as a section of each project. | `Project`, `CanWrite` | 1.3 |
| `[ExtensionSettings]` | In the Admin settings, in the entry of your extension. | `ExtensionId` | 1.3 |
| `[ProjectHomeSection("Title")]` | On the first tab of the project home, below the boards. | `Project`, `CanWrite` | 1.4 |
| `[ProjectCardSection("Title")]` | On the card of each project on the Projects page. | `Project`, `CanWrite` | 1.4 |
| `[TaskCardExtension]` | On each task card, next to the deadline ring. | `Task`, `ProjectId`, `CanWrite` | 1.8 |
| `[BoardColumnExtension]` | In the board header, next to the title of each column. | `Board`, `Status`, `Tasks`, `CanWrite` | 1.8 |
| `[ProjectSettingsSection("Title")]` | On the **Settings** tab of a project. | `Project`, `CanWrite` | 1.8 |
| `[BoardSettingsSection("Title")]` | In the **Board settings** dialog. | `Board`, `CanWrite` | 1.8 |
| `[AppBarExtension]` | In the app bar, on each page. | None | 1.8 |

The parameter types are:

| Parameter | Type |
|---|---|
| `Task` | `TaskDto` |
| `Project` | `ProjectSummaryDto` |
| `Board` | `BoardDto` |
| `Tasks` | `IReadOnlyList<TaskDto>` |
| `Status` | `TaskState` |
| `ProjectId` | `Guid` |
| `ExtensionId` | `string` |
| `CanWrite` | `bool` |
| `OnChanged` | `EventCallback` |

An example of a sidebar section:

```html
@attribute [ProjectNavSection("Plans", Order = 0)]
@inject ISoloClientApi Api

@foreach (var plan in _plans)
{
    <MudNavLink Href="@($"plans/{Project.Alias}/{plan.Id}")">@plan.Title</MudNavLink>
}

@code {
    [Parameter] public ProjectSummaryDto Project { get; set; } = null!;
    [Parameter] public bool CanWrite { get; set; }

    private List<PlanDto> _plans = [];

    protected override async Task OnInitializedAsync() =>
        _plans = await Api.GetAsync<List<PlanDto>>($"api/ext/plans/projects/{Project.Id}/plans");
}
```

### Rules for all contributions

- **Solo draws the title.** For an attribute with a title, your component draws only the content below the title.
- **Order.** Each attribute except `[TaskDetailExtension]` and `[ExtensionSettings]` has an `Order` property. Solo sorts by `Order`, then by extension ID.
- **Errors stay local.** Solo draws each contribution in its own error boundary. If your component throws an exception, Solo shows a short error in its place.
- **Contributions appear late.** Extension assemblies load after sign-in. A section appears a moment after the page.
- **`CanWrite` only helps the layout.** Use it to hide a control. Your endpoint must still check access.
- **No services.** To refresh a section after your own page changes data, use a static event in your assembly. Remove the handler in `Dispose`.

### Rules for specific contributions

**Task drawer.** Call `OnChanged` after you save. Solo then loads the task again and gets its new version.

**Task card.** A board can show hundreds of cards, and Solo draws your component one time for each card.

- Load the data one time for the board, not one time for each card. Keep it in a static cache in your assembly.
- Show a small item: a badge, a chip or a number. Show nothing when you have no data.
- A click on a card opens the task drawer. Add `@onclick:stopPropagation` to a control in your component.

**Board column.** `Tasks` holds all tasks in the column, in all lanes. This includes the finished cards that the board does not show. The board header is hidden on a phone, so a column contribution is not visible there.

**Project card.** Solo draws this section one time for each project on the Projects page. Keep its request small.

**Settings sections.** Your section saves through your own endpoint. Protect that endpoint with `SoloPolicies.ProjectOwner` or `SoloPolicies.ProjectWrite`. The **Save** button of the **Board settings** dialog saves only the fields of Solo.

**Admin settings panel.** Solo shows the panel only for an extension with the state **loaded**. Protect the endpoints of the panel with `SoloPolicies.SuperAdmin`.

**App bar.** Show one icon button, with a badge if necessary. The app bar is narrow on a phone, and all extensions share it. Use it for a state that changes while the person is on a different page, such as a count of unread items.

## Services from Solo

Inject these services into a page or a component. They are in `Solo.Shared.Extensibility`.

| Service | Purpose | Since |
|---|---|---|
| `ISoloClientApi` | Call the API. | 1.1 |
| `ISoloTaskDrawer` | Open the task drawer on your page. | 1.3 |
| `ISoloLiveTopics` | Get [live updates](hooks-and-events.md#live-updates). | 1.4 |
| `ISoloTaskPicker` | Let a person select a task. | 1.5 |
| `ISoloContextMenu` | Show a menu at the pointer. | 1.6 |

### Open the task drawer

`ISoloTaskDrawer.OpenAsync(taskId)` opens the same drawer that a board uses, on your page.

```html
@inject ISoloTaskDrawer Drawer
@implements IDisposable

@code {
    protected override void OnInitialized() => Drawer.Changed += OnDrawerChanged;

    private Task OpenAsync(Guid taskId) => Drawer.OpenAsync(taskId);

    private void OnDrawerChanged(TaskDrawerChange change) => InvokeAsync(() =>
    {
        // change.Kind is Saved, Moved or Deleted.
        // change.Task is the current TaskDto, or null after a deletion.
        StateHasChanged();
    });

    public void Dispose() => Drawer.Changed -= OnDrawerChanged;
}
```

- `Changed` reports each change to the open task. This includes changes that a different person makes.
- `Closed` reports that the drawer closed.
- The drawer applies the access of the person. A member with the Read only role cannot change the task.

### Let a person select a task

`ISoloTaskPicker.PickAsync` shows the task picker of Solo. The picker searches by name, serial number or epic.

```csharp
var task = await Picker.PickAsync(Project.Id, new TaskPickerOptions
{
    Title = "Link a task",
    Exclude = _linked.Select(l => l.TaskId).ToHashSet(),
    AllExcludedText = "Every task in this project is already linked.",
});

if (task is null)
{
    return;
}
```

- The method returns a `TaskDto`, or `null` when the person cancels.
- `Exclude` holds the IDs of tasks that the picker must not show.
- The picker shows only tasks that the person can see. Your endpoint must still check the task that it gets.

### Show a menu

`ISoloContextMenu.ShowAsync` shows a menu at a position on the page. The desktop app shows a native menu.

```csharp
private async Task OnRowMenuAsync(MouseEventArgs e, Milestone milestone)
{
    var choice = await Menu.ShowAsync(e.ClientX, e.ClientY,
    [
        new ContextMenuItem("open", "Open milestone"),
        new ContextMenuItem("delete", "Delete milestone") { Danger = true, SeparatorBefore = true },
    ]);

    if (choice == "delete")
    {
        await DeleteAsync(milestone);
    }
}
```

- The method returns the `Id` of the selected item, or `null`.
- Add `@oncontextmenu:preventDefault="true"` to the element. Without it, the browser also shows its own menu.
- A native menu shows only the label. `Icon` and `Danger` have no effect there. Make the label of a destructive command clear.
- Do not show a command that the person cannot use.
- A touchscreen has no right-click. Give the same menu a button, or open it after a long press.

### Add commands to the task menu

Solo shows a menu for a task when a person right-clicks a card. The **More actions** button in the task drawer shows the same menu. Contract 1.8 lets an extension add commands to it.

Write a class, not a component:

```csharp
[TaskMenuContributor(Order = 0)]
public sealed class SprintMenu(ISoloClientApi api) : ISoloTaskMenuContributor
{
    public ValueTask<IReadOnlyList<ContextMenuItem>> ItemsAsync(TaskMenuContext context, CancellationToken ct) =>
        ValueTask.FromResult<IReadOnlyList<ContextMenuItem>>(context.CanWrite
            ? [new ContextMenuItem("add", "Add to sprint")]
            : []);

    public Task OnChosenAsync(string itemId, TaskMenuContext context) =>
        api.SendAsync(HttpMethod.Post, $"api/ext/sprints/tasks/{context.Task.Id}", null);
}
```

- Solo creates the class for each menu. The constructor can take the services of Solo, such as `ISoloClientApi`, `ISoloTaskPicker` and `IDialogService`.
- Solo waits for `ItemsAsync` before it shows the menu. Answer from data that you have. If the method takes longer than 1 second or throws an exception, Solo omits your commands from that menu.
- The item IDs are your own. Two extensions can use the same ID.
