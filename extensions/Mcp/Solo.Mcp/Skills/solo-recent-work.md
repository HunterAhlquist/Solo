---
name: solo-recent-work
description: Build a visual recap of the last two weeks of finished Solo work, per member, alongside each member's current In Progress tasks. Use when someone asks what was recently finished, what got done lately, what the team shipped, or wants a standup, retro or weekly-recap summary out of Solo.
---

# Recent work recap

A recap of the last two weeks, one panel per member, with what each of them has
in flight right now. Rendered as a visual, not a wall of bullets.

## Know what you can't know, before you build it

Two limits shape the whole report. State them in the output rather than papering
over them:

**Solo records no completion timestamp.** A task has `createdAt` and nothing
else — no finished-at, no updated-at, on the entity or in any MCP tool. The audit
log does record the status change, but it's project-owner-only REST
(`GET /api/projects/{id}/audit`) with no tool in front of it. So "finished in the
last two weeks" is *inferred*, and the report has to say so.

**Names come from a second call.** A task carries `assigneeIds` and nothing else,
so build the directory first: `list_members(projectId)` for the roster and
`get_me` for whose token you are holding. Match on **`userId`** — the `id` beside
it is the membership row, not the person, and matching it silently yields nobody.
An id that resolves to no member is someone who has since left the project; label
them "former member" rather than dropping their work.

## Gather

`list_projects` → `list_members(projectId)` → `list_boards(projectId)` →
`list_tasks(boardId)` for every open board. That's the whole dataset: status,
`createdAt`, `version`, `assigneeIds`, `deadlineAt` and sub-tasks all come back
on the task, and the roster puts names to the ids.

## Dating the window

Window = 14 days back from today. Sort finished tasks into two bands and keep
them visually distinct:

- **Confident** — `status: "Finished"` and `createdAt >= windowStart`. Created and
  closed inside the window. These carry a date.
- **Undated** — `status: "Finished"` and `createdAt < windowStart`, ordered by
  `version` descending. `version` is the row's Postgres `xmin`: a transaction id
  that only increases, so a higher `version` means a more recent write to that
  row. It **orders, it does not date** — any edit bumps it, and a long-untouched
  row may read as a tiny number after a vacuum freeze, which harmlessly sorts it
  to the bottom. Take the top handful, label the band plainly ("completion date
  not recorded"), and never print a date next to one.

Guessing a date from `version` and presenting it as fact is the one failure this
report can't recover from. Don't.

## Group by member

- Group finished and in-progress tasks by `assigneeId`.
- **`assigneeIds: []` is normal on a one-person instance.** Nobody assigns work to
  themselves when they're the only member. `list_members` tells you which case
  you're in: one member means attribute everything to them and drop the
  per-member layout entirely — a one-person report with an "Unassigned" column is
  noise pretending to be data.
- On a multi-member project, unassigned finished work is real. Give it its own
  card at the end in a neutral color. Don't drop it and don't spread it across
  people.
- A task with several assignees appears under each of them; count it once in the
  totals and say so in the footnote.

## In progress now

Per member, their assigned `status: "InProgress"` tasks. Include sub-task
progress (`3/7`) and `deadlineAt` when set — an in-flight task with a date is the
most actionable line in the whole report.

## Render it

Two shapes, deliberately different layouts. Don't render the grid with one card
in it.

**One member** — a single stream, no grid and no avatars. A header with the
project name and date range; three metric tiles (finished, in progress,
sub-tasks ticked); then flat lists under `Finished`, `Also closed recently` and
`In progress now`. A finished row is serial, label pill, name, date. An
in-progress row is serial, name, a 4px progress track with `n/m`, and the
deadline.

**Several members** — one card per person in a
`repeat(auto-fit, minmax(300px, 1fr))` grid: initials avatar, name, finished
count, then their finished tasks at two lines each (serial and date above, name
below, wrapping rather than truncating — a 300px card ellipses anything kept on
one line), then a divider and their in-progress work. Unassigned gets a
neutral-gray card spanning the full width at the end.

Both are widget fragments: no `<html>`/`<body>`, and host CSS variables aliased
once at the top of the `<style>` block with `var(--token, fallback)` so the page
also opens standalone in a browser. Ready-made templates for both ship in the
downloadable skill pack as `templates/one-member.html` and `templates/team.html`.

Render with `show_widget` (call `read_me` first). If that isn't available, publish
an Artifact; if neither, fall back to a Markdown table — the data matters more
than the frame. Keep the footnote wherever it lands.

## Don't touch the board

This is a read. No status changes, no new tasks.
