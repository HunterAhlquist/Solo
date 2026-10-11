---
title: Reference
lede: Contract versions, hookable operations, authorization policies, registration methods and limits.
---

## Contract versions

The contract version is the version of the two contract packages. Declare the lowest version that you need in the [manifest](manifest.md#version-ranges).

"Server" features need the version in `soloApiVersion`. "Client" features need it in `soloClientVersion`.

| Version | Half | What it added |
|---|---|---|
| 1.0 | Server, client | Endpoints, your own data, `IExtensionFieldStore`, `ISoloQuery`, hooks, events, pages and navigation entries. |
| 1.1 | Client | `ISoloClientApi`. |
| 1.2 | Server | `ISoloQuery.TaskAssigneeIdentitiesAsync`, `IExtensionFieldStore.SetVersionedAsync`, `ISoloAuditContributor`. |
| 1.2 | Client | `[TaskDetailExtension]`. |
| 1.3 | Server | `TaskDto.StartAt`, the operation `task.setSchedule`, `ISoloQuery.CallerAsync`. |
| 1.3 | Client | `[ProjectNavSection]`, `ISoloTaskDrawer`, `[ExtensionSettings]`. |
| 1.4 | Server | `ISoloLiveUpdates`. |
| 1.4 | Client | `[ProjectHomeSection]`, `[ProjectCardSection]`, `ISoloLiveTopics`. |
| 1.5 | Client | `ISoloTaskPicker`. |
| 1.6 | Client | `ISoloContextMenu`. |
| 1.7 | Server | `ISoloCredentialIssuer`. |
| 1.8 | Server | `ISoloBackground`, `ISoloQuery.MembersAsync`, `CommentAsync` and `TaskCommentsAsync`, `ISoloLiveUpdates.PublishToUserAsync`, and the operations `task.setBoard`, `task.setAssignees` and `epic.setAssignees`. |
| 1.8 | Client | `[TaskCardExtension]`, `[BoardColumnExtension]`, `[ProjectSettingsSection]`, `[BoardSettingsSection]`, `[AppBarExtension]`, `ISoloTaskMenuContributor`, `ISoloLiveTopics.SubscribeUserAsync`. |

`ContractVersion.Current` in `Solo.Shared.Extensibility` holds the contract version of an instance.

## Operations

A [hook](hooks-and-events.md#hooks) runs on these operations. The constants are in `Solo.Extensibility.Operations`.

| Operation | Constant | Endpoint | Since |
|---|---|---|---|
| `task.create` | `TaskCreate` | `POST api/boards/{boardId}/tasks` | 1.0 |
| `task.update` | `TaskUpdate` | `PUT api/tasks/{taskId}` | 1.0 |
| `task.delete` | `TaskDelete` | `DELETE api/tasks/{taskId}` | 1.0 |
| `task.setStatus` | `TaskSetStatus` | `PUT api/tasks/{taskId}/status` | 1.0 |
| `task.setEpic` | `TaskSetEpic` | `PUT api/tasks/{taskId}/epic` | 1.0 |
| `task.move` | `TaskMove` | `PUT api/tasks/{taskId}/rank` | 1.0 |
| `task.setSchedule` | `TaskSetSchedule` | `PUT api/tasks/{taskId}/schedule` | 1.3 |
| `task.setBoard` | `TaskSetBoard` | `PUT api/tasks/{taskId}/board` | 1.8 |
| `task.setAssignees` | `TaskSetAssignees` | `PUT api/tasks/{taskId}/assignees` | 1.8 |
| `epic.create` | `EpicCreate` | `POST api/boards/{boardId}/epics` | 1.0 |
| `epic.update` | `EpicUpdate` | `PUT api/epics/{epicId}` | 1.0 |
| `epic.delete` | `EpicDelete` | `DELETE api/epics/{epicId}` | 1.0 |
| `epic.setAssignees` | `EpicSetAssignees` | `PUT api/epics/{epicId}/assignees` | 1.8 |
| `subtask.create` | `SubTaskCreate` | `POST api/tasks/{taskId}/subtasks` | 1.0 |
| `subtask.update` | `SubTaskUpdate` | `PUT api/subtasks/{subTaskId}` | 1.0 |
| `subtask.delete` | `SubTaskDelete` | `DELETE api/subtasks/{subTaskId}` | 1.0 |
| `subtask.setFinished` | `SubTaskSetFinished` | `PUT api/subtasks/{subTaskId}/finished` | 1.0 |

Notes:

- `task.move` changes the order of a task on one board. `task.setBoard` moves a task to a different board.
- For `task.setBoard`, the new board is the `BoardId` of the request. `Target.BoardId` is `null`, because the route names only the task.
- For `task.setAssignees` and `epic.setAssignees`, the request holds the full new list of assignees.

### Task dates

A task has an optional start date, `TaskDto.StartAt`, and an optional deadline, `TaskDto.DeadlineAt`.

- A start date needs a deadline, and cannot be after the deadline.
- `PUT api/tasks/{taskId}/schedule` with `{ startAt, deadlineAt, version }` sets the two dates. It answers `400` for a rule violation and `409` for an old version.
- `PUT api/tasks/{taskId}` does not change the start date. If that request removes the deadline, or moves it before the start date, Solo removes the start date.
- The effective start of a task is `StartAt`, or the creation date when `StartAt` is `null`.

To change the dates of a task from an extension, call the schedule endpoint from the client half.

## Policies

The constants are in `Solo.Extensibility.SoloPolicies`. See [Authorization](server.md#authorization).

| Constant | Value | Who passes |
|---|---|---|
| `ProjectRead` | `Project:Read` | Each member of the project. |
| `ProjectWrite` | `Project:Write` | A member with the Read/write role, or an owner. |
| `ProjectOwner` | `Project:Owner` | An owner of the project, or a super admin. |
| `SuperAdmin` | `SuperAdmin` | A super admin. |

## Registration methods

Call these in `ISoloExtension.ConfigureServices`.

| Method | Registers |
|---|---|
| `services.AddExtensionDbContext<T>(context)` | Your `DbContext`, with its migrations. |
| `services.AddOperationHook<T>()` | An `ISoloOperationHook`. |
| `services.AddEventHandler<T>()` | An `ISoloEventHandler`. |
| `services.AddCredentialIssuer<T>()` | An `ISoloCredentialIssuer`. |

## Services from Solo

### Server half

| Service | How to inject | Purpose |
|---|---|---|
| `ISoloQuery` | Directly | [Read core data](server.md#read-core-data). |
| `ISoloLiveUpdates` | Directly | [Publish live updates](hooks-and-events.md#live-updates). |
| `IExtensionFieldStore` | `[FromKeyedServices("<id>")]` | [Fields on core records](server.md#fields-on-core-records). |
| `ISoloBackground` | `[FromKeyedServices("<id>")]` | [Background work](hooks-and-events.md#background-work). |

### Client half

| Service | Purpose |
|---|---|
| `ISoloClientApi` | [Call the API](client.md#call-the-api). |
| `ISoloTaskDrawer` | [Open the task drawer](client.md#open-the-task-drawer). |
| `ISoloLiveTopics` | [Get live updates](hooks-and-events.md#subscribe-in-the-client-half). |
| `ISoloTaskPicker` | [Let a person select a task](client.md#let-a-person-select-a-task). |
| `ISoloContextMenu` | [Show a menu](client.md#show-a-menu). |

## Addresses

| Address | Content |
|---|---|
| `/api/ext/<id>/…` | The endpoints of your extension. |
| `/api/extensions/<id>/content/…` | The files in the `content/` folder of your package. No sign-in is necessary. |

## Limits

### Package

| Limit | Value |
|---|---|
| Size of the `.zip` file | 64 MB |
| Size of one file in the package | 64 MB |
| Total size after extraction | 192 MB |
| Number of files | 500 |
| Length of the ID | 64 characters |

### Run time

| Limit | Value |
|---|---|
| Time for `OnBeforeAsync` of a hook | 2 seconds |
| Time for `OnAfterAsync` of a hook, and for an event handler | 10 seconds |
| Time for `ItemsAsync` of a task menu contributor | 1 second |
| Length of a live topic | 200 characters |
| Live topics on one connection | 64 |

## Instance settings for extensions

| Variable | Purpose |
|---|---|
| `Extensions__Directory` | The directory with the `.zip` packages. |
| `Extensions__DevelopmentDirectories__0` | A package folder to load without a `.zip` file. |
| `Extensions__ApiBaseUrl` | The address at which background work reaches the Solo API. |
| `Database__AutoMigrate` | `false` stops the migrations of Solo and of all extensions. |
