---
layout: home
title: Solo
lede: Documentation for Solo, a project management app for small game-dev teams.
---

{% include shot.html dark="board-dark.png" light="board-light.png" alt="A Solo board for the fictional game Regulatory Capture Quest. Epic groups and epics run as lanes across the Unstarted, In Progress and Finished columns. Each card shows its label, deadline ring, sub-task progress and assignees." caption="A board in the default theme. The screenshots in these docs show a demo instance with a fictional game and a fictional team." %}

## What Solo is

Solo is a Kanban board for a small team. A project holds boards. A board holds epics, tasks and sub-tasks. Each task moves through three columns: Unstarted, In Progress and Finished.

- **Live editing.** A change that one person makes shows on every open board immediately. You see who has a task open and who edits it.
- **An audit log.** Solo records each changed field, the person who changed it, and the value it replaced.
- **Single sign-on.** People sign in with your identity provider: Google, Microsoft Entra ID, or another OIDC issuer.
- **Themes.** A theme sets the colours, fonts and sign-in screen of the instance. Three themes come with Solo.
- **Extensions.** A package adds endpoints, data, hooks and pages without a change to Solo. Two extensions come with the server release: Roadmaps and an MCP server.

One container and a PostgreSQL database are the full deployment.

## Where to start

| You want to | Start here |
|---|---|
| Use an instance that someone gave you access to | [Get started](guide/get-started.md) |
| Run your own instance | [Install](admin/install.md) |
| Add a feature to Solo | [Extension development](extensions/index.md) |
| Change the look of your instance | [Theme development](themes/index.md) |

## Project status

One person maintains Solo and built it for their own team. The repository does not accept issues or public pull requests. The MIT licence lets you fork it and change it.

To report a security problem, use **Report a vulnerability** on the repository's Security tab. [SECURITY.md](https://github.com/HunterAhlquist/Solo/blob/main/SECURITY.md) has the details.
