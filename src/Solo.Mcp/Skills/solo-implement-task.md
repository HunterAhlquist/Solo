---
name: solo-implement-task
description: Pick up a Solo task flagged "work for an LLM" and implement it, moving it through In Progress to Finished over MCP. Use when someone points you at a Solo task or serial number (e.g. LTN-FEAT-12) and expects the work done, or asks you to take the next LLM-flagged task off a board.
---

# Implementing a Solo task

The task is the brief. Read all of it, move the card before you start thinking,
do the work, and leave the board telling the truth about what happened.

## Find it

Given a serial, go straight there — every `taskId` parameter accepts
`PROJECT-BOARD-N`, so no discovery walk is needed:

`get_task("LTN-FEAT-12")` returns the description and the full sub-task list.

Given only "take the next one", walk `list_projects` → `list_boards` →
`list_llm_tasks(boardId)` and pick an `Unstarted` task whose flagged
`assigneeIds` include the user you are acting as. `list_llm_tasks` returns tasks
flagged for **any** eligible assignee, so filter — don't take someone else's
agent's work.

`get_task_llm_work(taskId)` confirms the flag. If it comes back false, don't
refuse the work the user just asked for — just don't claim the board said so.

## Move it to In Progress before you think

`set_task_status(taskId, "InProgress")`

This is the first call after reading the task and **before** you plan, explore
the codebase, or write anything. Not after you're confident it's doable. The
column exists so a teammate looking at the board right now knows the task is
taken; a status set at the end of the work never did that job.

## Read the whole brief

- The description is Markdown. `||LTN-FEAT-9||` links are real tasks — `get_task`
  each one before starting. They are usually the context the author assumed you'd
  follow.
- The sub-tasks are the checklist **and the definition of done**. If the task has
  sub-tasks, finishing means every box, not the ones that were convenient.
- Follow the repository's own conventions. The task says what; `CLAUDE.md`,
  `AGENTS.md` and the surrounding code say how.

## Work the checklist

Tick each sub-task as it actually lands, not in a batch at the end:

`set_subtask_finished(subTaskId, true)`

**Ticking the last box does not move the card.** The auto-move to Finished is
browser behavior — `SubTaskList.razor` fires a second status call after the tick.
Over MCP, `set_subtask_finished` persists the tick and nothing else, so a task
with every box ticked sits in whatever column it was in until you move it.

Don't rewrite the description to log progress. The ticks and the column are the
progress signal; the description is the brief, and editing it destroys what the
author wrote. `update_task` is a full replace under the hood — if you do need to
change a field, pass only that field and let the rest carry over.

A 409 means someone edited the task while you worked. Re-fetch with `get_task`,
merge, and retry — never overwrite.

## Finish

`set_task_status(taskId, "Finished")` — after the work is done **and verified**
by whatever the repo uses (`dotnet build Solo.slnx` / `dotnet test Solo.slnx`
here), not when the diff is written.

If part of it is blocked or turned out to be out of scope: leave the task
InProgress, leave that box unticked, and say what's left and why. A card in
Finished with unfinished work misreports it to everyone reading the board.

Work you discovered but didn't do belongs in its own task — see
the `solo-create-task` guide — linked back with `||SERIAL||`
so the trail survives.

## Report back

The serial, what you changed, how you verified it, and anything you left. If you
moved the card, say which column it's in now.
