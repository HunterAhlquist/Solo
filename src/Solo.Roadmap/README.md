# Solo.Roadmap

Roadmaps, packaged as a Solo extension. A roadmap is a named timeline inside a
project that gathers tasks from any of its boards into one row each, drawn as
bars from start date to deadline over a day grid. People reorder the rows
without touching board order, drag a bar's edges to reschedule the task, and see
missed deadlines flash red and listed under the grid.

Everything the feature needs is in this project and its client half
([Solo.Roadmap.Client](../Solo.Roadmap.Client)): the schema and migrations, the
endpoints, the sidebar section, the timeline page, the task-view chips and the
Admin settings panel. Nothing about roadmaps exists in `Solo.Api`, `Solo.Data`
or `Solo.Client`. Delete the package and the instance loses roadmaps and nothing
else. The sidebar keeps its Boards section, task drawers lose their Roadmaps
chips, and Admin settings lists the extension as *data present, package missing*
until someone purges `ext_roadmap`.

## What it adds

- **A Roadmaps section in each project's sidebar**, under its own header,
  listing the project's roadmaps with **Add roadmap** for writers.
- **The timeline page** at `projects/{alias}/roadmaps/{id}`:
  - The day grid is virtualized and bounded, with striped bands on a
    configurable cadence and today's column highlighted.
  - It opens scrolled to the oldest start among the tasks running through today.
  - Writers can add a task from the picker, drag rows to rerank, and drag
    either edge of a bar (or use Shift/Alt+Shift + arrows) to move its start or
    deadline.
  - Clicking a bar opens the same task drawer the board uses.
- **A Roadmaps line in every task drawer** with a chip per roadmap the task is
  on. It shows nothing when the task is on none.
- **Timeline settings in Admin settings**:

| Setting | Default | Allowed |
|---|---|---|
| Days before today | 365 | 1–3650 |
| Days after today | 730 | 1–3650 |
| Stripes begin on day of year | 1 | 1–366 (366 only in a leap year) |
| Stripe interval (days) | 14 | 1–365 |

The day of year is resolved to a date in the year it is saved, and that
**date** is stored. Storing only the number would shift the pattern every New
Year; the stored date lets the bands run continuously, like a sprint cadence.
Changes apply the next time a roadmap page loads.

## Layout

```
extension.json      manifest: id "roadmap", schema "ext_roadmap", both
                    assemblies, no nav entries (the sidebar section is the
                    entry point)
Contracts/          DTOs and limits, linked into the client project too
Controllers/        roadmaps and entries, a task's roadmaps, the settings
Data/               RoadmapDbContext, the settings row, ranking, and the
                    handler that drops a deleted task's entries
Migrations/         in schema ext_roadmap
content/            roadmap.js (scroll, drag, resize) and roadmap.css, served
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
| GET | `projects/{projectId}/roadmaps/{id}` | read | The timeline: entries in roadmap order, each with its `TaskDto` and board, plus the settings. |
| GET | `projects/{projectId}/roadmaps/{id}/candidates` | read | Tasks in the project not yet on this roadmap, for the picker. |
| POST | `projects/{projectId}/roadmaps/{id}/entries` | write | `{ taskId, anchorDay }`. 400 for a task outside the project, 409 if it's already on. |
| PUT | `projects/{projectId}/roadmaps/{id}/entries/{entryId}/rank` | write | `{ precedingId, followingId }`. Roadmap order only. |
| DELETE | `projects/{projectId}/roadmaps/{id}/entries/{entryId}` | write | Takes the task off this roadmap; the task is untouched. |
| GET | `tasks/{taskId}/roadmaps` | read | The roadmaps a task is on. |
| GET | `settings` | signed in | The bounds, the interval, the day of year and the resolved anchor date. |
| PUT | `settings` | super admin | `{ pastDays, futureDays, stripeStartDayOfYear, stripeIntervalDays, version }`. 400 out of range, 409 stale. |

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
  a one-day bar.
- A task that already has dates keeps them, and the page says so.

**An entry outlives a board move but not a deletion.** Moving a task between
boards raises a delete event on the board it left. So the delete handler drops
entries only once `ISoloQuery` can no longer find the task. Event delivery is
best-effort, so the timeline read also prunes any entry whose task has gone.

**The stylesheet is added by the extension's own module**, as a `<link>`
appended when the page first loads it. `HeadContent` shows one owner's markup at
a time, and a second use would push out the theme's stylesheet.

## Its one dependency on core

Contract **1.3**, for two things:
- the task's optional `StartAt`, which turns a deadline into a range;
- the hookable `task.setSchedule` endpoint that sets a task's start and
  deadline together.

The client half also uses the 1.3 seams:
- `[ProjectNavSection]` for the sidebar;
- `ISoloTaskDrawer` to open the board's drawer over the timeline;
- `[ExtensionSettings]` for the Admin panel.

Hence `>=1.3 <2.0` on both sides of the manifest. See
[writing-extensions.md](../../docs/writing-extensions.md#contract-13).

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
