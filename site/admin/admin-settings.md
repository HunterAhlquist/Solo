---
title: Admin settings
lede: The page where a super admin sets the branding, the audit retention and the theme, and manages the extensions and users of the instance.
---

## Open the Admin settings

Select **Admin settings** at the bottom of the sidebar. Only a super admin sees this entry.

The page has five parts: **Branding**, **Audit retention**, **Theme**, **Extensions** and **Users**.

## Branding

The branding is the display name and the tagline of your instance.

- **Name.** Solo shows the display name in the app bar, on the sign-in page and in the title of each page. The [desktop app](../guide/desktop.md) shows it in its list of instances.
- **Tagline.** Solo shows the tagline below the name on the sign-in page.

1. Enter a name of 40 characters or fewer in **Name**.
2. Enter a tagline of 120 characters or fewer in **Tagline**.
3. Select **Save changes**. If you changed the name, Solo loads the page again.

Solo does not save these two fields when you leave them. You must select **Save changes**.

To remove the name or the tagline, clear the field and save. An instance with no display name shows the name Solo. An instance with no tagline shows the tagline of Solo.

The footer of each page always tells that the instance runs Solo.

## Audit retention

Solo deletes audit entries that are older than this number of days.

1. Enter a number from 1 to 3650 in **Days**.
2. Leave the field. Solo saves the value.

A new instance starts with the value from the setup wizard.

## Theme

The theme applies to each person on the instance. Each person still selects light or dark for their own browser.

1. Select a theme in the list. Solo saves the selection.
2. Load the page again to see the theme.

Built-in themes have the word **built-in** next to their name. Solo has three: Ahlquist Interactive, Arcade and Blueprint.

### Rescan

The rescan button, at the top right of the card, reads the themes directory again. Use it after you [copy a theme package](packages.md#install-a-theme) into the directory. No restart is necessary.

If a package does not load, the card lists the file and the reason.

### A missing theme

If the package of the selected theme is gone, Solo uses the default theme and shows a warning. Solo keeps your selection. You have two options:

- Put the package back in the themes directory, and rescan.
- Select a different theme.

## Extensions

The card lists each extension that the instance knows. Each entry shows the name, the ID, the version and the database schema of the extension.

| State | Meaning |
|---|---|
| loaded | The extension runs. |
| disabled | The extension did not load, or its database migration failed. The entry gives the reason. The endpoints of the extension answer with status `503`. |
| data present, package missing | The package is gone from the extensions directory. The data of the extension is still in the database. |

An entry can also show:

- **Intercepts**, with a list of operations. The extension runs code before these operations and can refuse them. Read this line when a change to a task fails and you do not know why.
- The settings of the extension itself. The Roadmaps extension shows its timeline settings here.

A package that Solo could not read shows as a warning below the list, with the file and the reason.

Solo reads extension packages only at startup. See [Install an extension](packages.md#install-an-extension).

### Delete the data of an extension

An entry with the state **data present, package missing** has a **Delete data** button.

1. Optional: make a backup of the schema with `pg_dump -n <schema>`.
2. Select **Delete data**.
3. Type the ID of the extension to confirm.

> Solo deletes the schema of the extension and each row in it. Solo also deletes the values that the extension added to tasks, epics and boards. You cannot get this data back.
{: .danger}

## Users

The card lists each person who signed in to the instance.

### Make a person a super admin

Set the switch in the **Super admin** column. A super admin can change the Admin settings and create projects.

The instance must keep one super admin or more. Solo disables the switch of the last super admin.

If you clear your own switch, you lose access to the Admin settings. A different super admin can give it back.

### Sign a person out everywhere

**Sign out everywhere** revokes what one person holds on the instance:

- Each desktop session of the person. The **Devices** column shows the number of sessions.
- Each token that an extension issued to the person, such as an MCP token.
- The permission that the person gave an extension to do background work as them.

A browser holds no Solo session. A browser with a sign-in from the identity provider loses access in one hour or less.

This action does not remove the person. The person can sign in again. To end the access of a person to a project, remove the person from that project. See [Change a role](../guide/projects.md#change-a-role).
