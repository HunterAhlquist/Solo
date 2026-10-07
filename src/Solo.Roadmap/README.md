# Solo.Roadmap

Roadmaps, packaged as a Solo extension. A roadmap is a named timeline inside a
project that gathers tasks from any of its boards into one row each, drawn as
bars from start date to deadline over a day grid. People reorder the rows
without touching board order, drag a bar's edges to reschedule the task, and see
missed deadlines flash red and listed under the grid. Everyone with the roadmap
open sees each other's changes as they happen, and who else is there.

Everything the feature needs is in this project and its client half
([Solo.Roadmap.Client](../Solo.Roadmap.Client)): the schema and migrations, the
endpoints, the sidebar section, the project home and project card sections, the
timeline page, the task-view chips and the Admin settings panel. Nothing about
roadmaps exists in `Solo.Api`, `Solo.Data` or `Solo.Client`. Delete the package
and the instance loses roadmaps and nothing else:
- the sidebar keeps its Boards section;
- project homes go back to a plain Boards tab, and project cards to their boards;
- task drawers lose their Roadmaps chips;
- Admin settings lists the extension as *data present, package missing* until
  someone purges `ext_roadmap`.

## What it adds

- **A Roadmaps section in each project's sidebar**, under its own header,
  listing the project's roadmaps with **Add roadmap** for writers.
- **A Roadmaps section on each project's home**, under the boards in the
  "Boards & Roadmaps" tab. Each roadmap is a card that opens it; writers get
  **Rename** on each card and a tile to start a new one.
- **A Roadmaps surface on each project's card on All projects**, below its
  boards, with a link per roadmap.
- **The timeline page** at `projects/{alias}/roadmaps/{id}`:
  - The day grid is virtualized and bounded, with sprint stripes and today's
    column highlighted. It always shows at least nine rows, and two empty ones
    below the last task.
  - It opens scrolled to the oldest start among the tasks running through today.
  - Writers add a task by right-clicking any day no bar covers (or pressing
    and holding one on a touchscreen) and choosing **Add a task on …**, or with
    **Add task** in the header, which adds it on today. Either way they pick it
    in the host's task picker, which searches by name, serial or
    epic and groups by board and then epic. Tasks already on the roadmap aren't
    offered. Finished tasks and tasks on closed boards are hidden until
    switched on. The task gets a row of its own at the end, which the view
    scrolls to.
  - Dragging a bar up or down reranks it; dragging it sideways moves its start
    and deadline together. The way the pointer first goes decides which, so a
    reorder never shifts dates by accident.
  - Dragging either edge of a bar (or Shift/Alt+Shift + arrows) moves its start
    or deadline.
  - Hovering a bar whose label is cut off scrolls the label past like a ticker,
    except under reduced motion.
  - A row whose bar is scrolled out of view shows a hint pinned to that edge,
    naming the task. Finished tasks whose last day was before today don't.
  - Clicking a bar opens the same task drawer the board uses.
  - Writers select several bars at once: drag a box around them from an empty
    part of the grid, Cmd-click (Ctrl-click off a Mac) to add or drop one, or
    Shift-click to select every row from the last one clicked. Holding Cmd,
    Ctrl or Shift while dragging a box adds to the selection instead of
    replacing it. Clicking the grid or a bar, or pressing Escape, clears it.
  - Dragging a selected bar takes the rest of the selection with it. Sideways,
    every bar moves by the same number of days, and they all stop when the
    first one reaches the edge of the window. Up or down, the rows land
    together where they're dropped, in the order they had, in one request.
    Dragging a bar that isn't selected moves that bar alone.
  - **Delete** (**Cmd-Delete** on a Mac) takes the selection off the roadmap,
    as the menu's Remove does. Like Escape, it applies only while focus is on
    the grid and nothing is open over the page.
  - Right-clicking a bar (or a long press, or its **⋯** button) opens its menu:
    **Open task**, and for writers **Remove from roadmap**. On a selected bar
    the menu acts on the whole selection, as **Remove N tasks from roadmap**,
    in one request. On any other bar it selects that bar alone first. Removing
    takes tasks off this roadmap only; the tasks themselves stay on their
    boards.
  - The menus are the host's (`ISoloContextMenu`), so the desktop app shows
    native ones.
  - **Milestones** are named deadlines on the roadmap itself, independent of
    any task. Writers add one with **Add milestone** in the header (on today),
    or by right-clicking a day and choosing **Add a milestone on …**, and give
    it a name, a day and a colour (red unless they pick another). Each shows
    as a 4px dotted line at the end of its day, the height of the grid, with a
    flag carrying its name in the month band.
    - Clicking the flag (or the line, or Enter on the focused flag) opens its
      dialog to rename, recolour, re-date or delete it. Right-clicking it, or a
      long press, offers **Edit milestone** and **Delete milestone**.
    - Dragging the flag or the line moves it a day at a time; Shift+←/→ on the
      focused flag does the same.
    - A milestone flashes like a missed bar while it's due: from the start of
      the sprint its day falls in, through the day itself. A day in the first
      three days of a sprint counts as due from the sprint before, so it
      doesn't arrive without warning.
    - Scrolled out of view, a milestone shows as a chip pinned to that side of
      the month band. Clicking the chip brings its line to the middle of the view.
  - Changes anyone else makes to the roadmap or its tasks redraw it in place,
    without moving the view.
  - Whoever else has the roadmap open shows in the header. An outlined,
    pulsing avatar means they're dragging a bar or have a roadmap dialog open.
- **A Roadmaps line in every task drawer** with a chip per roadmap the task is
  on. It shows nothing when the task is on none.
- **Timeline settings in Admin settings**:

| Setting | Default | Allowed |
|---|---|---|
| Days before today | 365 | 1–3650 |
| Days after today | 730 | 1–3650 |
| Sprints start on | Monday | any day of the week |
| Sprint length (weeks) | 2 | 1–52 |

The last two are **Sprint Indication**: the grid's stripes alternate one sprint
light, one dark. Where they fall is worked out from the two settings alone,
counted from a fixed reference (the first chosen weekday of year 1), so the
pattern is the same on every instance and runs unchanged back into the past.
For sprints longer than a week, that rule decides which weeks start one; it
doesn't follow when the settings were saved. Changes apply the next time a
roadmap page loads.

## Layout

```
extension.json      manifest: id "roadmap", schema "ext_roadmap", both
                    assemblies, no nav entries (the sidebar section is the
                    entry point)
Contracts/          DTOs and limits, linked into the client project too
Controllers/        roadmaps and entries, a task's roadmaps, the settings
Data/               RoadmapDbContext, the settings row, ranking, and the
                    handler that drops a deleted task's entries and tells
                    open roadmaps when one of their tasks changes
Migrations/         in schema ext_roadmap
content/            roadmap.js (scroll, drag, resize, select) and roadmap.css, served
                    at /api/extensions/roadmap/content/
```

## Endpoints

All under `/api/ext/roadmap`. Every route that names a project goes through
core's project policies. A roadmap id is honoured only under its own project's
route; any other project answers 404.

| Method | Route | Access | |
|---|---|---|---|
| GET | `projects/{projectId}/roadmaps` | read | The project's roadmaps. |
| POST | `projects/{projectId}/roadmaps` | write | `{ title }`. |
| PUT | `projects/{projectId}/roadmaps/{id}` | write | Rename: `{ title, version }`. 409 on a stale version. |
| DELETE | `projects/{projectId}/roadmaps/{id}` | write | The roadmap and its entries; the tasks are untouched. |
| GET | `projects/{projectId}/roadmaps/{id}` | read | The timeline: entries in roadmap order, each with its `TaskDto` and board, its milestones by day, plus the settings. |
| POST | `projects/{projectId}/roadmaps/{id}/entries` | write | `{ taskId, anchorDay }`. 400 for a task outside the project, 409 if it's already on. |
| PUT | `projects/{projectId}/roadmaps/{id}/entries/{entryId}/rank` | write | `{ precedingId, followingId }`. Roadmap order only. |
| PUT | `projects/{projectId}/roadmaps/{id}/entries/{entryId}/anchor` | write | `{ anchorDay }`: moves a no-deadline pill. A task with dates moves through core's schedule endpoint instead. |
| DELETE | `projects/{projectId}/roadmaps/{id}/entries/{entryId}` | write | Takes the task off this roadmap; the task is untouched. |
| PUT | `projects/{projectId}/roadmaps/{id}/entries/rank` | write | `{ entryIds, precedingId, followingId }`, 1 to 500: moves them together between the neighbors, keeping their order. Ids not on this roadmap are skipped; 400 if a neighbor is one of the rows moving. |
| POST | `projects/{projectId}/roadmaps/{id}/entries/remove` | write | `{ entryIds }`, 1 to 500: takes them all off in one change. Ids not on this roadmap are skipped. |
| POST | `projects/{projectId}/roadmaps/{id}/milestones` | write | `{ name, day, color? }`. The colour is `#rrggbb` and defaults to `#e53935`. 400 without a name or day. |
| PUT | `projects/{projectId}/roadmaps/{id}/milestones/{milestoneId}` | write | `{ name, day, color, version }`. 409 on a stale version. |
| DELETE | `projects/{projectId}/roadmaps/{id}/milestones/{milestoneId}` | write | Deletes the milestone. |
| GET | `tasks/{taskId}/roadmaps` | read | The roadmaps a task is on. |
| GET | `settings` | signed in | The bounds, the sprint start day and the sprint length. |
| PUT | `settings` | super admin | `{ pastDays, futureDays, sprintStartDay, sprintLengthWeeks, version }`, the day as a name (`"Monday"`). 400 out of range, 409 stale. |

## Decisions worth knowing before changing anything

**A task's dates live on the task, not on the roadmap.** An entry stores only
which roadmap, which task, its rank and an `anchorDay`. The anchor is where a
task with no deadline is drawn: a one-day pill on the day it was added. Bars
read `StartAt` and `DeadlineAt` from core. Resizing saves through core's
`PUT api/tasks/{id}/schedule` from the client, as the person dragging.

This keeps a roadmap and the board from ever disagreeing about a date. It also
means a reschedule gets core's concurrency check, the `task.setSchedule` hook,
the audit trail and the live broadcast to board cards, all without this package
re-implementing any of it. The server half reads core through `ISoloQuery` and
never writes to it.

**Adding a task never moves someone's deadline behind their back.**
- A task with no deadline is scheduled for the day it was added on, and becomes
  a one-day bar. Added on a day already past, it runs from that day through
  today instead, so it doesn't arrive already missed.
- A task that already has dates keeps them, and the page says so.

**An entry outlives a board move but not a deletion.** Moving a task between
boards raises a delete event on the board it left. So the delete handler drops
entries only once `ISoloQuery` can no longer find the task. Event delivery is
best-effort, so the timeline read also prunes any entry whose task has gone.

**Live changes are nudges, and wait for the person editing.** Every write
publishes on the roadmap's topic (`roadmap:{id}`) or the project's list topic
(`roadmap:list`). Task edits made anywhere in Solo reach open roadmaps through
the task event handler. A page that hears one refetches the timeline. While
that person is mid-drag, or has a save of their own on its way, the refetch
waits, because the server's copy doesn't have their change yet and would snap
the bar back. Messages heard meanwhile fold into one read once both settle.
`RoadmapTopics` in `Contracts/` lists the topics and kinds.

**The stylesheet is added by the extension's own module**, as a `<link>`
appended when the page first loads it. `HeadContent` shows one owner's markup at
a time, and a second use would push out the theme's stylesheet.

**Selection lives in .NET, and a box in progress lives in the browser.** The
page holds which entries are selected and draws them, so a bar scrolled out of
view and redrawn comes back still selected. While a box is being dragged,
`roadmap.js` marks the rows it touches itself, working from each row's columns
rather than its bar, so bars outside the drawn range are caught too. It hands
the result to .NET when the box is let go. The page marks selected rows
(`data-selected`) so a drag can preview the whole group. On the drop, .NET
decides again from its own copy which rows go along.

**A milestone belongs to the roadmap, not to a task.** It's a row of this
package's own (`Milestones`, cascading with its roadmap), so moving one never
touches a task's dates, and the same date on two roadmaps is two milestones.
When it's due is worked out in the browser from the sprint settings
(`RoadmapSprints.MilestoneDueFrom`), not stored.

**Both content URLs carry the package version** (`roadmap.js?v=1.6.1`). The
page and its script change together, and a cache between the app and the
server, such as a proxy that adds its own headers or the desktop app's disk
cache, could otherwise pair a new page with an old script.

## Its dependency on core

Contract **1.4** on the server and **1.6** in the browser. From 1.3, two
things on the server:
- the task's optional `StartAt`, which turns a deadline into a range;
- the hookable `task.setSchedule` endpoint that sets a task's start and
  deadline together.

The client half also uses the 1.3 seams:
- `[ProjectNavSection]` for the sidebar;
- `ISoloTaskDrawer` to open the board's drawer over the timeline;
- `[ExtensionSettings]` for the Admin panel.

From 1.4:
- `[ProjectHomeSection]` and `[ProjectCardSection]` for the project home and
  the All projects cards;
- `ISoloLiveUpdates` on the server and `ISoloLiveTopics` on the client, for live
  changes and presence.

From 1.5, `ISoloTaskPicker` for choosing the task to add. From 1.6,
`ISoloContextMenu` for the bar and day menus. Both are client-only, hence
`>=1.4 <2.0` for `soloApiVersion` and `>=1.6 <2.0` for `soloClientVersion`.
See [writing-extensions.md](../../docs/writing-extensions.md#contract-16).

## Building the package

```bash
build/pack-roadmap-extension.sh
```

Produces `artifacts/solo-roadmap-<version>.zip`, containing `extension.json`,
`server/Solo.Roadmap.dll`, `client/Solo.Roadmap.Client.dll` and `content/`.
The script refuses to run if the manifest and either csproj disagree about the
version; bump all three together.

CI builds this package on every pull request. For a `server-v*` tag, it attaches
the package to the GitHub release only when the Roadmaps sources, shared
contracts, or packaging script changed since the previous server release.

## Migrations

Scaffolded here, applied by the host at startup (gated on `Database__AutoMigrate`
like core's):

```bash
dotnet ef migrations add <Name> --project src/Solo.Roadmap --startup-project src/Solo.Roadmap
```

A failed migration disables this extension only. Its endpoints then answer 503
naming the reason, and Admin settings shows it.

## Upgrading from 1.5

1.6 adds the `Milestones` migration, a new table in `ext_roadmap`, which the
host applies at startup. It needs nothing new from the host. The timeline read
now includes `milestones`, so update the server package and the page together.

## Upgrading from 1.4

1.5 adds no migration and needs nothing new from the host. Moving several rows
up or down uses a new endpoint, so update the server package and the page
together, as with any release.

## Upgrading from 1.3

1.4 adds no migration. Its client half needs contract 1.6, for the host's
context menu. On the desktop app, update the app as well as the server. Until
a client is on 1.6, it skips the package and shows no roadmaps, while the
server keeps them.

## Upgrading from 1.2

1.3 adds no migration. Its client half needs contract 1.5, for the host's task
picker, and its server half no longer has the `candidates` endpoint the old
picker read. On the desktop app, update the app as well as the server. Until a
client is on 1.5, it skips the package and shows no roadmaps, while the server
keeps them.

## Upgrading from 1.1

1.2 adds no migration, but it needs a host on contract 1.4. On the desktop app,
that means updating the app as well as the server. Until a client is on 1.4,
it skips the package and shows no roadmaps, while the server keeps them.

## Upgrading from 1.0

1.1's `SprintIndication` migration replaces 1.0's stripe settings, a start date
and an interval in days, with a weekday and a length in weeks:
- The weekday is the old start date's, except a start on 1 January, the old
  default, which becomes Monday.
- The interval becomes whole weeks, rounded, from 1 to 52.
