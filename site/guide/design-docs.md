---
title: Design Docs
lede: Keep Markdown notes, task links, images, and sketches together in a project.
---

Design Docs is an optional extension. An administrator must install `solo-design-docs` before its pages appear. See [Themes and extensions](../admin/packages.md#install-an-extension).

{% include shot.html width="1275" height="988" src="design-docs-notes.png" alt="A design doc for the fictional game Regulatory Capture Quest. The hierarchy contains design notes. The editor shows Markdown, task links, and stacked attachments." %}

## Open or create a design doc

A design doc is a vault of notes and folders. Each vault belongs to one project.
The project sidebar, project home, and Projects page list its design docs.

1. Select **New design doc** in the project sidebar.
2. Enter a name.
3. Optional: select **Import an Obsidian vault (.zip)…**. See [Import and export](design-docs-import-export.md).
4. Select **Create**.

A super admin can create, rename, close, reopen, or delete a vault.
Content access still requires project membership.
Members with Read/write access and project owners can edit notes.
Members with Read only access can read notes and export the vault.

## Organize notes

Use **Note** or **Folder** above the hierarchy to create an item at the vault root.
The **⋯** menu beside an item contains its rename and delete actions.

- Select a folder arrow to expand or collapse that folder.
- Use the button beside **Vault root** to expand or collapse all folders.
- Drag an item title onto a folder to move the item there.
- Drag an item onto **Vault root** to move it to the root.
- On a touchscreen, hold the title briefly before dragging it.

Collapsing all folders keeps the path to the current note open.
Solo remembers expanded folders and the current note in this browser or desktop instance.

## Write a note

Select a note in the hierarchy. The editor saves changes automatically while connected.
Markdown formatting appears as you write. The cursor reveals the syntax that you edit.

| To | Do this |
|---|---|
| Create a heading | Type one to six `#` characters, then a space. |
| Create a bullet list | Type `- ` at the start of a line. |
| Create a numbered list | Type `1. ` at the start of a line. |
| Continue a list | Press **Return**. |
| Indent or outdent | Press **Tab** or **Shift+Tab**. |
| Change text size | Use **Editor font size** in the vault header. |
| Change text margins | Use **Editor margin width** in the vault header. |

Text size and margins persist in this browser or desktop instance.
They do not change the stored Markdown.

The insertion palette follows the current line beside the left text margin.
Move the pointer to show it. Typing hides it.
**Alt+Insert** moves keyboard focus to the palette.

## Link notes and tasks

Drag a note title from the hierarchy into the editor to insert a link at the drop position.
Obsidian links such as `[[Movement]]` and `[[Movement#Dash|Dash timing]]` also work within a vault.

Select **Link a task** in the insertion palette to search the project's tasks.
The task picker can include finished tasks and closed boards.
Select a task link to open the task drawer.

The core task and comment editors also have a **Link a doc** action.
It inserts a link to a note or one of its headings.
Note links remain valid after a rename or move.

## Add images and sketches

Select **Add image** in the palette, or paste or drop an image into the editor.
Select **New sketch** to create a [sketch](design-docs-sketches.md).
Attachments remain rendered while you edit the surrounding text.

Place attachments on the same line to form a stack.
Drag a rendered attachment to move its embed within the note.
The preview size follows the editor's font scale.

{% include shot.html width="1275" height="988" src="design-docs-viewer.png" alt="The attachment viewer shows an expanded concept image with navigation arrows, a counter, and attachment actions." %}

Select a preview to open the attachment viewer.
Use its arrows or the **Left** and **Right** arrow keys to change attachments on that line.
Select the background or **Close viewer** to close it.

The viewer can download images, export sketches as PNG, and remove an attachment embed from the note.
Writers can also replace an image or edit a sketch.
Use the viewer to remove an embed. **Backspace** and **Delete** do not remove rendered attachments.

## Work with other people

Profile pictures identify people who view or edit the same note or sketch.
Each person has a project color. The same color outlines their picture and cursor.
Changes appear without a page reload.

The editor becomes read-only while disconnected.
It synchronizes again when the connection returns.
Keep the page open while edits are pending.

## Close a design doc

Select **Close** in the vault header's **Design doc actions** menu.
A closed vault remains readable and exportable. Its content cannot change.
The project's **Closed design docs** section has **Reopen** and **View** actions.
Delete is available only after a super admin closes the vault.
