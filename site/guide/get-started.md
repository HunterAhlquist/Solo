---
title: Get started
lede: Sign in, learn the layout, and learn the words that Solo uses for your work.
---

## Before you start

You need two things:

- The address of a Solo instance.
- An invitation to a project on that instance.

A project owner invites you by email address. Solo does not send a message. The owner gives you the address of the instance.

Solo has no sign-up page. If nobody invited your email address, the instance refuses your sign-in.

## Sign in

1. Open the address of the instance in a browser.
2. Select the sign-in button. The button shows the name of the identity provider that the instance uses.
3. Complete the sign-in on the page of the identity provider.

Solo then shows the **Projects** page.

An instance that uses Google shows the option **Stay signed in on this device**. When you select it, the browser keeps your sign-in after you close it. When you clear it, the sign-in ends with the browser tab.

### If Solo refuses the sign-in

Solo shows this message when no project has an invitation for your account:

> Your account isn't authorized to use this instance. Ask an administrator to invite you to a project, then sign in again.
{: .warning}

Ask a project owner to invite the email address of the account that you sign in with. Then sign in again.

### If your session expires

Solo shows a **Session expired** dialog on the page where you work. Sign in again in that dialog. An open task stays open.

## The layout

{% include shot.html dark="board-dark.png" light="board-light.png" alt="A Solo board. The app bar runs along the top. The sidebar on the left lists projects and their boards. The board fills the remaining space." %}

Solo has three areas.

### App bar

The app bar is the black bar at the top.

- The **menu** button shows or hides the sidebar.
- The **Solo** title opens the Projects page.
- Your **account picture** opens the account menu. The menu holds **Appearance**, **Signed-in devices** and **Sign out**.

A cloud icon appears in the app bar when a board loses its live connection. Changes from other people can arrive late until the icon goes away.

### Sidebar

The sidebar lists each project that you are a member of. Select a project to show its content:

- **Boards** lists the open boards of the project. **Add board** creates a board.
- An extension can add a section below the boards. The Roadmaps extension adds **Roadmaps**.
- **Project settings** opens the project home.

The bottom of the sidebar holds instance-level entries:

- **Add project**, for a super admin.
- Pages that extensions add, such as **MCP access**.
- **Admin settings**, for a super admin.

To change the order of the projects, drag a project by its icon. Solo saves the order in your browser only. Other people keep their own order.

### Page

The remaining space shows the page that you selected: the Projects page, a project home, a board, or a page from an extension.

## How Solo organizes work

| Item | What it is |
|---|---|
| Project | A game or one body of work. A project has members and boards. |
| Board | A Kanban board with three columns: Unstarted, In Progress and Finished. |
| Epic group | An optional titled group of epics on a board. |
| Epic | A lane across the board. An epic holds tasks. |
| Task | A card on the board. A task belongs to one epic or to no epic. |
| Sub-task | One line of the checklist of a task. |

### Aliases and serial numbers

Each project and each board has an **alias**. An alias has 1 to 8 characters: capital letters and digits. Solo uses aliases in page addresses.

Each task has a **serial number**. The serial number joins the project alias, the board alias and a number:

```
RCQ-VS-6
```

This is task 6 on the board `VS` of the project `RCQ`. The task drawer shows the serial number at the top. You can use a serial number to [link to a task](tasks.md#link-to-a-task).

## Roles

Each member of a project has one role. A member can also be an owner.

| Role | What the member can do |
|---|---|
| Read only | See the boards and tasks of the project. Write comments. |
| Read/write | All of the above. Create and change boards, epics, tasks, sub-tasks and labels. |
| Owner | All of the above. Invite and remove members, change roles, read the audit log, change the project settings, delete the project, and delete the comments of other members. |

**Super admin** is a role on the instance, not on a project. A super admin creates projects and changes the [Admin settings](../admin/admin-settings.md).

## Light and dark

1. Select your account picture in the app bar.
2. Below **Appearance**, select **Light**, **Dark** or **Match system**.

**Match system** follows the setting of your device. Solo saves your selection in your browser.

A super admin selects the theme for the instance. The theme sets the colours and fonts of the two schemes. You select only which scheme you see.

## Next

- [Projects and members](projects.md): invite people and set their roles.
- [Boards](boards.md): read and arrange a board.
- [Tasks](tasks.md): the fields of a task, sub-tasks and comments.
