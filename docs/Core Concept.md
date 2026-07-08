Solo is a simple project management software with little configuration, sane defaults, and easy deployment. Geared towards my ideal game development planning workflow.
## Stack
- MudBlazor
	- Mudblazor.Markdown
- Blazor WASM + Signalr
- ASP.NET (Web API backend)
- Postgresql via EF Core
## Core Features
- Work Item management 
	- Kanban style boards with support for epics, and sub-tasks
- Google SSO authentication and user management
- Read or Read + Write user access
	- Since this is geared to small private teams, trust is simplified
- Live editing
	- Uses Signalr to receive data changes and viewing users in real time to reduce collisions
	- Events are not sent per character, but per "save event" (example: clicking outside of a text box, or selecting a dropdown item.)
## Hierarchy 
### Project -> Kanban Board -> Epic -> Task -> Sub-Task
Project has
- Name
- Icon
- User access configuration
- Type and label global configuration.
Kanban Board has
- Name
- Icon
- Open/Close status
Epic
- Name
- Description (Markdown)
- Comments
- User Assignment
Task
- Name
- Label
	- Configurable global for all boards, and local additions per board. 
	- Global defaults: Bug, Feature, Improvement, Feedback Session
- Finished Status (auto if sub-tasks are used)
- Description (Markdown)
- User Assignment
- Comments
Sub-Task (checklist)
- Name
- Label
	- Configurable global for all boards, and local additions per board.
	- Global defaults: Code, Asset, Design, Writing
- Finished Status
## Audit Logging
All "save event" changes to data will write to an audit log table that records:
- DateTime of change
- User (Id) who changed the data
- Nature of the data changed (Table, row, and column)
- Previous value
Configuration of logging is only retention date.