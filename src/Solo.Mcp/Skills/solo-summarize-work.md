---
name: solo-summarize-work
description: Work out what to do next on a Solo project — walk its boards over MCP, infer the blockers Solo doesn't model, and recommend one concrete next piece of human work. Use when someone asks "what should I work on", "what's next on X", "where do I start", or wants a push in the right direction on a project.
---

# What to do next

Someone asking what to do next is stuck, not uninformed. They can already see the
board. What they want is a decision with a reason behind it — **one** next thing,
why it's that one, and what to do instead if they disagree.

An inventory of the board is the working material, not the answer. Never hand
back a list of everything.

## Read the whole project

`list_projects` → `list_boards(projectId)` → for each board with `isOpen: true`:
`list_epic_groups`, `list_epics`, `list_tasks`. `list_labels(projectId)` once, to
turn `labelId`s into names.

Every board. A project's real next action is often on the board the user wasn't
looking at.

## What the fields are telling you

| Signal | Reading |
|---|---|
| `rank` | Priority within its column or epic. Lower rank = higher on the board = what the author meant to do next. |
| epic groups | Phases. Unfinished work in an earlier group usually gates a later one. |
| `deadlineAt` | A date, not an instant. Overdue or near-due outranks rank order. |
| `subTasks` partially ticked | Work already in flight. Cheaper to land than anything unstarted. |
| many `InProgress` | The answer is probably "finish one of these", not "start something". |
| `createdAt` old + `Unstarted` | Either it doesn't matter or it's stuck. Worth naming either way. |

## Blockers — Solo doesn't model them, so derive them

There is no blocker field, no dependency edge, no "blocked" status. Infer them,
and say which signal each one came from so the user can overrule you:

- **`||SERIAL||` links.** The strongest signal, because someone wrote it on
  purpose. Resolve each with `get_task`; a task pointing at something that isn't
  Finished is a candidate blocked task.
- **Sequencing words** in descriptions: *after, once, depends on, needs, blocked
  by, requires, follow-up to*.
- **Position.** A task in an epic that sits in a later group than unfinished work
  in an earlier one.
- **Missing inputs.** A description naming an asset, a decision, a credential, an
  account or a third party that doesn't exist yet is blocked on a person, not on
  code. These are the ones worth surfacing loudest — nobody unblocks them by
  accident.
- **Undefined work.** A vague name with an empty description is blocked on
  *definition*. The next action is a five-minute conversation, and saying so is
  often the most useful answer you can give.

## Exclude the LLM work

The question is what the **user** should do. Call `list_llm_tasks(boardId)` per
board and drop those tasks from the recommendation — they're already earmarked
for an agent.

The tool ships in MCP package 1.2.0. If it isn't in the catalog, make the
recommendation anyway and say the filter couldn't be applied.

## Don't touch the board

No status changes, no new tasks, no re-ranking. The user asked where to go, not
to be moved. Offer to file a follow-up if the walk turned one up; don't file it.

## Answer

1. **Do this next** — one task, by serial and name, and the sentence of reasoning
   that made it win. Deadline pressure, unblocks the most, or already half done.
2. **If not that** — two or three runners-up, one line each.
3. **Blocked, and on what** — the blockers you inferred, with the signal behind
   each, most-costly first.
4. **Nobody can start this** — the definition-blocked items, if any.

Serial numbers throughout, so every line is clickable.
