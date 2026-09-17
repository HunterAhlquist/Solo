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

When they do ask: **MCP has no tool for either.** `create_task` and `update_task`
cover name, description, label and epic only — assignees and `deadlineAt` exist
in the REST API (`PUT /api/tasks/{id}/assignees`, `deadlineAt` on the task PUT)
and are not exposed as tools. Report the serial and ask the user to set them in
the task drawer. Don't drop the request silently, and don't fake it with a label
or a line in the description.

## "Work for an LLM"

When the user expects their own agent to do the work, set the flag:

`set_task_llm_work(taskId, llmWork: true)`

It is metadata. It does not assign the task, start it, or launch anything, and it
grants no access. What it does is put the task in `list_llm_tasks`, which is how
an agent loop finds work meant for it — see the `solo-implement-task` guide.

Two conditions, both of which fail loudly rather than quietly:

- The task must **already be assigned** to a user with active MCP access, or the
  call is a 400 (*"No assignee has MCP access"*). Since MCP cannot assign, that
  means asking the user to assign it first — in practice to themselves, the
  account whose token you are calling with. Check before you write:
  `get_task_llm_work(taskId)` returns `eligible`, which is that same test run as
  a read. `eligible: false` means the enable call will be rejected, so ask for
  the assignment instead of firing it and reporting an error.
- The tool ships in MCP package **1.2.0**. On an older install it is simply
  absent from the catalog. Say so rather than substituting something else; a
  "for-LLM" label is not the same thing and `list_llm_tasks` will not see it.

## Report back

One short block: serial, board, label, and the sub-tasks you created. Serials
make it clickable and let the user spot a wrong board at a glance.
