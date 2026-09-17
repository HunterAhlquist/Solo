---
name: solo-create-task
description: File a reported bug or requested feature as a Solo task over MCP, in Unstarted, broken into labelled sub-tasks. Use when someone reports something broken, describes behavior they want changed, or asks to track work in Solo — "file this", "make a task for it", "put it on the board".
---

# Filing work in Solo

A bug reported in chat is a bug nobody can find tomorrow. Turn it into one Solo
task, in Unstarted, with enough detail that whoever picks it up doesn't have to
come back and ask what you already knew.

## Find the destination first

`list_projects` → `list_boards(projectId)` → `list_labels(projectId)` →
`list_epics(boardId)`.

Board choice is the expensive mistake. A project usually has a **Bugs** board and
a **Features** board, and a task on the wrong one is invisible to whoever triages
it. Match the board to the kind of work. Ask when two boards are plausible or the
project is ambiguous — never guess across projects.

Labels carry a `kind`. A `Task` label goes on the task, a `SubTask` label goes on
sub-tasks, and crossing them is a 400 (*"The label must belong to this board and
be a sub-task label"*), not a silent no-op. A label with a non-null `boardId`
only works on that one board.

## The task

`create_task(boardId, name, description, epicId?, labelId?)`

- **name** — the symptom in the words the reporter would search for. ≤300 chars.
  "Column headers scroll under the app bar", not "Fix scrolling".
- **labelId** — a `kind: "Task"` label; typically Bug, Feature or Improvement.
- **epicId** — only when an existing epic obviously fits. A bug report is not a
  reason to invent an epic.
- **description** — Markdown, ≤20k chars. Tables, code fences and task lists
  render; raw HTML is stripped. Link a related task by wrapping its serial in
  double pipes — `||LTN-BUGS-12||` — which renders as a link to that task.

For a bug, the description earns its place by carrying what the reporter knows
and the implementer doesn't:

```markdown
**What happens** — …
**Expected** — …
**Steps** — 1. … 2. …
**Where** — `src/Solo.Client/Components/TaskCard.razor:90`
```

Keep the reporter's own words for the symptom. Don't write down a cause you
haven't confirmed — a confident wrong diagnosis costs more than no diagnosis.

**New tasks are created Unstarted, at the end of the board.** That is already the
required state. Do not call `set_task_status` afterwards.

## Sub-tasks

Break it down only when the work has genuinely separable steps someone could
tick off one at a time. A bug with one obvious fix gets no sub-tasks; two
sub-tasks that are really one step are noise on the card.

`create_subtask(taskId, name, labelId)` — one call each, in the order they'd be
done. **Label every sub-task** with a `kind: "SubTask"` label (Code, Design,
Asset, Writing — whatever the project defines). `taskId` takes the serial, so the
`number` returned by `create_task` is all the reference you need.

Adding sub-tasks does not move the parent. It stays Unstarted.

## Assignees and deadlines

**Only when the user mentions them.** An assignee or due date nobody asked for is
a commitment you invented on someone's behalf.

When they do ask, both are one call:

- **Deadline** — `deadlineAt` on `create_task` and `update_task`, as `YYYY-MM-DD`.
  It is a date, not an instant: "due 30 July" means the same thing in every
  timezone.
- **Assignees** — `set_task_assignees(taskId, userIds)`. `list_members(projectId)`
  turns names into ids and `get_me` returns your own. Assign by **`userId`**, not
  the `id` beside it — that one is the membership row, and the API rejects it as
  not a member of the project. A member with `isPending: true` has been invited
  but hasn't joined and cannot be assigned.

`set_task_assignees` **replaces the whole set**, so anyone you leave out is
unassigned. To add someone, read the task's current `assigneeIds` first and send
them back along with the new one.

## "Work for an LLM"

When the user expects their own agent to do the work, set the flag:

`set_task_llm_work(taskId, llmWork: true)`

It is metadata. It does not assign the task, start it, or launch anything, and it
grants no access. What it does is put the task in `list_llm_tasks`, which is how
an agent loop finds work meant for it — see the `solo-implement-task` guide.

Two conditions, both of which fail loudly rather than quietly:

- The task must be **assigned** to a user with active MCP access, or the call is
  a 400 (*"No assignee has MCP access"*). When the user is asking for their own
  agent to do the work, that user is you: `get_me` for your id, then
  `set_task_assignees` before setting the flag. This is the one case where
  assigning without being asked is right — it is what "my agent will pick this
  up" means — but assign the caller, never someone else.
- `get_task_llm_work(taskId)` returns `eligible`, the same test run as a read.
  Check it rather than firing a write you can already tell will be refused.
- The tool ships in MCP package **1.2.0**. On an older install it is simply
  absent from the catalog. Say so rather than substituting something else; a
  "for-LLM" label is not the same thing and `list_llm_tasks` will not see it.

## Report back

One short block: serial, board, label, and the sub-tasks you created. Serials
make it clickable and let the user spot a wrong board at a glance.
