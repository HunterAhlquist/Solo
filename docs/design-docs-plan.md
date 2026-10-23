# Design Docs: build plan

> **Status (2026-10-22):** A1–A5 and B0–B9 implemented on `codex/design-docs-prerelease`; physical device acceptance is pending. Original plan grounded in `d6357e1`; lifecycle behavior checked against merged PR #29 (`843a19c`).
>
> **Verified correction:** B2's original acceptance sentence denied vault lifecycle to non-member super admins, contradicting its policy table and settled PR #29 behavior. `AccessPolicy.Allows(..., Owner)` admits super admins; `Read` still requires membership. The B2 acceptance sentence now matches those rules. Physical iPad/Pencil checks remain required; no result is inferred from a desktop browser test. Safari selection retention and actual Electron ZIP downloads passed the final checks recorded below.
>
> **Where the decisions come from:** the feasibility review, [Design Docs extension: feasibility review](https://claude.ai/artifact/RxZ5kdcCkJZN7LZunHY1CL), and Hunter's answers in its comment threads (2026-10-22). Treat the decisions in §1 as settled. Anything this plan had to decide on its own is listed in §5, so Hunter can overrule it before or during the build.

Design Docs is an **extension**: Obsidian-style markdown vaults per project, live co-editing, task links, images, and pixel-style sketches. It needs four general-purpose additions to core first, shipped together as **contract 1.10**. Core still won't know Design Docs exists after those additions: each one is a seam any extension could use.

## How to use this plan

1. Read `CLAUDE.md`, then [writing-extensions.md](writing-extensions.md). Read `extensions/Roadmap` alongside it. It is the closest existing extension: own schema, project nav and home sections, live topics, a JS module in `content/`, a CI-built package.
2. Build **Part A (core, contract 1.10)** first, as one PR. Part B can't compile against a contract that doesn't exist yet.
3. Build **Part B (the extension)** in the PR order in §3. Start with the prototype in B0. It proves the risky parts before the rest is built on them.
4. Each step ends with **Done when**. Don't move on until it holds. Where this plan says *verify*, it is a claim not yet checked against the running code or the library. Check it, and if it's wrong, change the design here rather than working around it in silence.

The rule from `CLAUDE.md` still applies to Part B: **the extension modifies no core project.** Part A is the only place core changes. If Part B seems to need anything else from core, that's a contract gap. Stop and raise it.

---

## 1. Settled decisions

From the review and its comment threads. Don't reopen these.

| Area | Decision |
|---|---|
| Shape | One project can have several **vaults** (the user-facing name is "design doc"). A vault holds folders and markdown notes, like an Obsidian vault. |
| Editor | CodeMirror 6 with live preview: one view for reading and editing, with no Write/Preview switch. Markdown symbols (a heading's `#`, `**`) show only on the lines under **your own** cursor. Each editor works this out from its own cursor, so another person's cursor never reveals symbols on your screen. |
| Sync | **Relay.** The server stores and relays Yjs updates without interpreting them, and clients send a plain-markdown copy. No YDotNet, no native libraries. |
| Contract | Approved for 1.10: a markdown link seam plus an insert-at-cursor seam in core's description editor, and client-sent live messages on core's hub. Also the core CSP change that allows `blob:` images. |
| Access | Permissions come from the project. Non-members get nothing, raw files included. Images are fetched with the sign-in credential and shown through `blob:` URLs. |
| Read members | Read-only editor. They send no edits and have no cursor, but they see writers' cursors and show as "viewing". |
| Vault lifecycle | Only **super admins** create, close, reopen and delete vaults. A closed vault is read-only and can be reopened. A vault can be deleted only after it's closed. |
| Editing | Online only. A read-only offline cache is a nice-to-have, not part of this plan's scope. |
| History | No version history, just "last edited by" with a time. |
| Comments | None in Design Docs for now. |
| Cursors | Text cursors and selections in markdown. Mouse pointers in sketches only. |
| Colors | Per project, assigned by join order (golden-angle steps, see B5). |
| Task → doc links | Each note has a GUID. A picker in core's description editor inserts a link at the cursor. |
| Search | Stretch goal, not in the first version. |
| Obsidian import v1 | `.md` notes and the images they embed only. Dataview, Canvas and Excalidraw content is dropped. Dataview queries live inside notes as code blocks, so they come through as plain code. |
| Sketch ownership | A sketch belongs to one note and can move within it. Moving it to another note is a nice-to-have. |
| Sketch look | WigglyPaint style: a pixel texture stamped along each vector stroke. Starts at **72 paint pixels per inch** (A4 = 595 × 842), to be tuned when Hunter tests it. |
| Boil | Animated but subtle. Each stamp shifts by about one paint pixel at most. |
| Thickness | Five levels. With a stylus the selected level is the minimum and pressure ramps up toward the thickest. A mouse ignores pressure. |
| Tools | Pen, eraser, rectangle and ellipse (a modifier constrains them to squares and circles), select/move, text with a pixel font. Shapes are drawn as ordinary strokes. |
| Eraser | Visually cuts strokes rather than deleting whole ones. Stored as eraser strokes, so two people erasing at once merge cleanly. |
| Select/move | Picks up a group of strokes that touch each other, or a text box. Moving one piece of a cut stroke splits that stroke at that moment. |
| Shrinking a page | Warns that line work will be clipped. Clipped strokes are hidden, not deleted, so enlarging the page brings them back. |
| Page | A size from common paper sizes and an orientation. A background pattern (empty, grid, notebook, dots) is chosen with the size, sits on the pixel grid, and exports in light gray. In the app the page contrasts with the ink. |
| Inks | Black/white (follows the color scheme), red, blue, green. |
| Sketch export | Rasterized PNG, black lines on white, one still frame. |

**Dependency, built elsewhere:** boards and roadmaps are moving to the same super-admin-only lifecycle in a separate thread, [PR #29](https://github.com/HunterAhlquist/Solo/pull/29). Don't re-plan or redo it here. That PR puts create, close/reopen and delete behind `SuperAdmin` with `ProjectOwner` stacked on top, so the project still resolves from the route. B2 copies that pattern, and the way the client hides those actions, so boards, roadmaps and vaults all behave the same way. If #29 changes before it merges, follow what it merges with.

---

## 2. Architecture at a glance

```
Browser (Blazor WASM)                                   Server (one container)
┌───────────────────────────────────────────┐          ┌───────────────────────────────────────┐
│ Solo.DesignDocs.Client (Razor, MudBlazor) │          │ Solo.Api                               │
│  vault tree, dialogs, presence bar,       │  REST    │  BoardHub ── SendTopicMessage (A2) ──┐ │
│  nav/home sections, link picker (A3)      │─────────▶│  /api/ext/design-docs/... ──────┐    │ │
│        │ JS interop (DotNetObjectReference)│          │                                │    │ │
│        ▼                                  │          │ Solo.DesignDocs (extension)    ▼    ▼ │
│ content/design-docs.js (esbuild bundle)   │  hub     │  controllers   live handler (relay)  │
│  CodeMirror 6 + live preview              │◀────────▶│        │              │              │
│  Yjs doc + awareness + provider           │ (core's  │        ▼              ▼              │
│  sketch renderer / input / tools          │  hub,A2) │   ext_design_docs schema (Postgres)  │
│  images (blob: URLs, A1+A4), zip I/O      │          │   vaults, notes, update log, images  │
└───────────────────────────────────────────┘          └───────────────────────────────────────┘
```

- **Yjs** ([docs](https://docs.yjs.dev/)) holds each note's text (`Y.Text`) and each sketch's strokes. It runs only in the browser. The server stores Yjs updates as opaque bytes and relays them.
- **Core's hub carries the sync traffic** through the new client-sent messages (A2). The extension opens no WebSocket of its own, so it needs no ticket scheme or startup filter. Core already handles reconnects and the desktop proxy.
- **.NET sits in the middle of every message** (JS → .NET → hub → .NET → JS), because core's hub connection lives in .NET (`src/Solo.Client/Realtime/BoardHubClient.cs`). That's fine at typing rates if the JS side batches (B3). Measure it in B0.
- **The server never parses markdown or Yjs.** What it knows about a note's content (headings, text for export or later search) comes from the markdown copy that writer clients send.

---

## Part A: core changes (contract 1.10)

One PR. It changes `Solo.Api`, `Solo.Client`, `Solo.Shared`, `Solo.Extensibility`, `Solo.Extensions`, `src/Solo.Desktop`, tests and docs. Nothing in it mentions Design Docs.

### A1. Allow `blob:` images in the content security policy

`img-src` today is `'self' data: https:`. It's set in **two** places that must stay in step:

- `src/Solo.Api/SecurityHeaders.cs` (`ContentSecurityPolicy`)
- `src/Solo.Desktop/main/server.js` (`CONTENT_SECURITY_POLICY`). The desktop shell serves the client from its own origin with its own copy of the policy. Change only one and images break in the desktop app alone.

Change both to `img-src 'self' data: https: blob:`. Add a sentence to the doc comment in `SecurityHeaders.cs` on why: a page fetches a members-only image with the sign-in credential and shows it from an object URL, and only script already running on the page can mint a `blob:` URL. Don't touch `IsolationPolicy`.

**Done when:** `tests/Solo.Api.Tests/Integration/SecurityHeaderTests.cs` asserts that `img-src` contains `blob:` (nothing checks `img-src` today), and the desktop file matches.

### A2. Client-sent live messages on core's hub

Today a topic message only goes from server to clients (`ISoloLiveUpdates.PublishAsync`, `src/Solo.Api/Realtime/LiveUpdates.cs`). A2 lets a client send one on a topic it holds. Core passes it to **the extension that owns the topic**, and that extension's handler decides what happens: drop it, reply to the sender, or relay it to everyone else on the topic.

#### Server contract (`src/Solo.Extensibility/`)

New file `ISoloLiveMessageHandler.cs`, in the same voice as `ISoloLiveUpdates.cs`:

```csharp
public interface ISoloLiveMessageHandler
{
    /// Runs once per client-sent message on one of this extension's topics.
    /// Not calling a method on reply drops the message.
    ValueTask HandleAsync(LiveMessage message, ILiveMessageReply reply, CancellationToken ct);
}

public sealed record LiveMessage(Guid ProjectId, string Topic, string Kind, string? Data, LiveSender Sender);

/// Who sent it. Core resolves this when the topic is subscribed and refreshes it when
/// the member's role changes, so a handler never has to look it up per message.
public sealed record LiveSender(Guid UserId, string? DisplayName, ProjectRole Role, bool IsProjectOwner)
{
    public string ConnectionId { get; init; } = string.Empty;
}

public interface ILiveMessageReply
{
    /// Only to the connection that sent the message.
    Task ToSenderAsync(string kind, string? data = null, CancellationToken ct = default);

    /// To every other connection on the topic, the sender's own other tabs included.
    Task ToOthersAsync(string kind, string? data = null, CancellationToken ct = default);
}
```

Registration in `ExtensionServiceCollectionExtensions.cs`: `services.AddLiveMessageHandler<THandler>()`, scoped like hooks and event handlers. One handler per extension. Registering a second one is a load failure.

Messages reach the client as the existing `TopicMessage` (`src/Solo.Shared/Hubs/BoardHubContract.cs`), with `ActorUserId` set to the sender. That way the client's dispatch in `LiveTopics.cs` doesn't change.

**Implementation finding (2026-10-22):** the original sender shape had no connection key, but B3 requires per-connection multipart buffers and seed leases. An invocation-scoped handler cannot distinguish a user's two tabs from `UserId` alone, and must not depend on HTTP request state. `LiveSender.ConnectionId` is the opaque hub connection id supplied by core. It is not a credential and changes on reconnect. This is a general-purpose addition within A2, before the extension is built.

#### Routing and ownership

- **A topic belongs to the extension whose id comes before its first `:`.** Roadmap already names its topics this way (`roadmap:list`, `roadmap:{id}` in `RoadmapContracts.cs`). Find the handler's owner from its assembly, as `HookCatalog.OwnerOf` does (`src/Solo.Extensions/HookCatalog.cs`). Put the lookup in a small `LiveHandlerCatalog` in `Solo.Extensions`.
- A message on a topic with no handler, or whose extension is disabled (`ExtensionState.IsDisabled`), is refused. Client-sent messages are opt-in per extension.
- User topics (`TopicRegistry.UserScope`) don't accept client messages in 1.10. Nobody has needed it.

#### The hub method

Add to `IBoardHubServer` and `BoardHub`:

```csharp
Task<LiveSendResult> SendTopicMessage(Guid projectId, string topic, string kind, string? data);
```

`LiveSendResult` is a new enum in `Solo.Shared.Hubs`: `Accepted`, `NotSubscribed`, `NoHandler`, `TooLarge`, `RateLimited`, `Failed`. Return a result rather than throwing a `HubException`. A Yjs client has to know when an update didn't land so it can resend (B3), and an exception per rejected message would be noisy.

Checks, in this order:

1. `RequireTopic(topic)`. `kind` is 1–64 characters. `data` is at most `BoardHubConstants.ClientMessageMaxLength` characters. Add that constant at **48,000**.
2. The connection holds `(projectId, topic)` in `TopicRegistry`. Subscribing is where read access was checked, so holding the topic is the access check. Otherwise `NotSubscribed`.
3. A per-connection token bucket (`System.Threading.RateLimiting.TokenBucketRateLimiter`). Default burst 200, 50 per second, read from `RateLimiting:Live:Burst` / `RateLimiting:Live:TokensPerSecond` next to the existing `/api` settings in `Program.cs`. Dispose the connection's limiter in `OnDisconnectedAsync`. This bucket is separate from the `/api` one, so live traffic can't starve a board's REST calls.
4. Find the handler (see Routing). Run it in the invocation's scope, with a timeout of about 5 seconds linked to `Context.ConnectionAborted`. Exceptions and timeouts are logged naming the extension, and return `Failed`.

`ILiveMessageReply` is implemented in `Solo.Api` over `IHubContext<BoardHub, IBoardClient>`: `Clients.Client(connectionId)` for the sender, and `Clients.GroupExcept(TopicGroup(...), [connectionId])` for the others.

#### Message size

SignalR's hub default `MaximumReceiveMessageSize` is 32 KB ([Microsoft: SignalR configuration](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0)). Raise it for `BoardHub` only, to 64 KB (`AddSignalR().AddHubOptions<BoardHub>(...)`), so a 48,000-character `data` plus its JSON envelope fits. Anything bigger is the extension's job to split (B3). Core uses SignalR's JSON protocol, so binary payloads travel as base64 strings, about a third larger.

#### Role freshness

`TopicRegistry.Subscription` gains `ProjectRole Role` and `bool IsOwner`, filled in by `SubscribeTopic` from the membership row it already reads through `ProjectAccessHandler`. Then `LiveSender` costs nothing per message. To keep it current:

- Extend `LiveAccess.RevalidateAsync` (`src/Solo.Api/Realtime/LiveAccess.cs`) so it also refreshes the cached role on the topics a user still holds.
- Call it from the member update endpoint (`PUT api/projects/{projectId}/members/{memberId}`, `ProjectsController.cs` around line 204), not only from member removal (line 257). Otherwise someone demoted to Read keeps sending edits until they reconnect.

#### Client contract (`src/Solo.Shared/Extensibility/ISoloLiveTopics.cs`)

```csharp
/// Sends a message on a subscribed project topic, for its extension's server
/// handler to drop, answer or relay (contract 1.10).
Task<LiveSendResult> SendAsync(Guid projectId, string topic, string kind, string? data = null,
    CancellationToken ct = default);

/// True while the live connection is up.
bool IsConnected { get; }

/// The connection went down, or came back with held topics already resubscribed.
/// After a gap, refetch or resync: anything sent in it was missed.
event Func<Task>? ConnectionChanged;
```

Implement them in `src/Solo.Client/Realtime/LiveTopics.cs` over `IBoardHubClient`, which already raises `ConnectionStateChanged` after it resubscribes topics on reconnect (`BoardHubClient.cs`, the `Reconnected` handler). When not connected, `SendAsync` returns a result without throwing. Add `NotConnected` to the enum for that case.

These are new members on a published interface. `extensions/Roadmap/Solo.Roadmap.Tests/FakeLiveTopics.cs` and the core test fakes have to implement them, as they did for `SubscribeUserAsync` in 1.8.

#### Tests

- `tests/Solo.Api.Tests/Integration/SignalRTests.cs`: not subscribed → `NotSubscribed`. A topic with no handler, or another extension's prefix → `NoHandler`. Oversized → `TooLarge`. Burst exceeded → `RateLimited`. A relayed message reaches the others and not the sender. `LiveSender.Role` is `Read` for a Read member, and turns `Read` after a demotion without reconnecting.
- Give the fixture extension (`tests/Solo.Extension.Fixture`) an echo handler. Nothing else in core registers one, so it is the only way to test routing end to end.
- `tests/Solo.Client.Tests/LiveTopicsTests.cs`: `SendAsync` forwards to the hub, `NotConnected` while down, `ConnectionChanged` fires after a reconnect.

**Done when:** an extension can send on its own topic and nobody else's, a Read member's messages arrive at the handler labeled Read, and the tests above pass.

### A3. Markdown seams in core's editor and renderer

Core renders descriptions and comments with `SoloMarkdown.razor` (Markdig through `SoloMarkdownPipeline`), and edits them with `MarkdownEditor.razor`, a MudBlazor text field with Write and Preview tabs. Neither has an extension point today. Discover both new seams the way core finds task menu contributors: `ContributedSections.Implementations<TAttribute, TContract>` (`src/Solo.Client/Extensions/ContributedSections.cs`), and instantiate them the way `src/Solo.Client/State/TaskMenuService.cs` does.

#### (a) Editor actions: insert at the cursor

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownEditorActionAttribute : Attribute { public int Order { get; set; } }

public interface ISoloMarkdownEditorAction
{
    /// Buttons for this editor, from what the action already holds. Awaited before the
    /// toolbar renders: one that throws or takes over a second is left out.
    ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct);

    /// Text to insert at the cursor, replacing any selection, or null to insert nothing.
    Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct);
}

public sealed record MarkdownEditorButton(string Id, string Label) { public string? Icon { get; init; } }
public sealed record MarkdownEditorContext(Guid ProjectId, MarkdownSurface Surface, Guid EntityId, string? SelectedText);
public enum MarkdownSurface { TaskDescription, EpicDescription, Comment }
```

Wiring:

- `MarkdownEditor.razor` gains `ProjectId`, `Surface` and `EntityId` parameters. `DetailDrawer.razor` (line 114) and `CommentThread.razor` (lines 46 and 67) pass them. Render the buttons in a compact row at the top of the Write tab, and above the comment composer, which is always in edit mode.
- Insert with a new `solo.markdown.insertAtCursor(root, text)` in `src/Solo.Client/wwwroot/js/solo-interop.js`. Find the `textarea` inside the editor's root element, call `setRangeText(text, start, end, 'end')`, then dispatch an `input` event so the `Immediate` binding updates `_draft`. A textarea keeps `selectionStart`/`selectionEnd` after losing focus to a dialog, so no extra bookkeeping is needed. *Verify on Safari.*
- If the insert would go past `MaxLength`, refuse it with a snackbar.
- Button ids are prefixed per extension, as `TaskMenuService` prefixes menu ids.

#### (b) Link rendering: live labels for claimed links

```csharp
[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownLinkProviderAttribute : Attribute { public int Order { get; set; } }

public interface ISoloMarkdownLinkProvider
{
    /// Cheap and synchronous: does this provider own the link? No I/O.
    bool Claims(string url);

    /// Labels for the claimed links, keyed by URL. A URL left out keeps its label as written.
    ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct);
}
```

Behavior in `SoloMarkdown.razor`:

1. Render the markdown as written, straight away, as today.
2. Parse it with the same pipeline plus `UsePreciseSourceLocation()`. Collect the URLs of `LinkInline`s (not images) that pass `SoloMarkdownPipeline.IsSafeUrl` and that some provider `Claims`.
3. Call each provider once per render with its URLs, with a one-second timeout. A failure or timeout is logged, and those links stay as written.
4. Replace each claimed link's label text by its `LabelSpan`, working from the end of the string backwards so earlier offsets stay valid. Escape markdown characters in the new label. Then render again. *Verify that `LinkInline.LabelSpan` is populated with precise source locations in the Markdig version the client ships.*
5. Leave caching to providers. Core calls them on every render, and the contract doc says so.

`||SERIAL||` linkification (`LinkifySerials`) runs first and is unaffected. If the extension is uninstalled, its links render with the label they were written with. That keeps the extension removable.

Out of scope: `[[...]]` syntax in task descriptions, hover previews, `[[` autocomplete. The GUID-link decision made them unnecessary for now.

**Tests (`tests/Solo.Client.Tests`, bUnit):** a contributed action's text lands at the cursor and replaces a selection. Over-length inserts are refused. A provider's label replaces a claimed link's label. Unclaimed links, images and unsafe URLs are untouched. A throwing or slow provider leaves the text as written.

**Done when:** a test extension can add a button to the task description editor that inserts text at the cursor, and can relabel its own links in a rendered description or comment.

### A4. Binary calls on `ISoloClientApi`

**This one wasn't in the review's list.** Grounding the plan turned it up: `ISoloClientApi` (`src/Solo.Shared/Extensibility/ISoloClientApi.cs`) only sends and receives JSON. Design Docs has to download image bytes with the credential (to make `blob:` URLs) and upload images and zipped vaults. The only binary call today is core's own `GetExtensionAssemblyAsync` in `ApiClient.cs`.

```csharp
/// GET, as bytes. Throws SoloApiException on a non-success status.
Task<SoloBinary> GetBytesAsync(string path, CancellationToken ct = default);

/// A call whose body is raw bytes, such as an image upload, with a JSON response.
Task<T> SendBytesAsync<T>(HttpMethod method, string path, byte[] content, string contentType,
    CancellationToken ct = default);

public sealed record SoloBinary(byte[] Content, string? ContentType);
```

Implement them explicitly in `ApiClient`, beside the other extension-seam members, so they share its credential and ProblemDetails handling.

*Fallback if Hunter declines A4:* send images as base64 inside JSON through the existing `GetAsync`/`SendAsync`. It works with no contract change, but it's about a third bigger and costs more memory in WASM.

### A5. Version bump and documentation

Per `CLAUDE.md`:

- `build/contract-package.props` `<Version>` → `1.10.0`, with `ContractVersion.Current` in `src/Solo.Shared/Extensibility/ContractVersion.cs` to match (`ContractPackageTests` enforces it). Add a `<para>1.10.0 ...</para>` to its remarks: both halves changed.
- Every `HunterAhlquist.Solo.*` reference → `1.10.0`: `samples/HelloExtension`, `extensions/Mcp`, `extensions/Roadmap` and `templates/content/`. `build/pack-contract.sh` enforces it.
- `templates/Solo.Templates.csproj` `<Version>` → `1.10.0`, because the template's major.minor has to match the contract's. Run `build/pack-templates.sh`.
- `docs/writing-extensions.md`: a "Contract 1.10" section in the style of the 1.8 one, covering client-sent messages, editor actions, link providers, binary calls and `blob:` images.
- `site/extensions/reference.md` (the version table), `site/extensions/index.md` ("describes contract 1.10"), and the package versions in `site/extensions/client.md`, `server.md` and `first-extension.md`.
- `docs/extension-plan.md`: a short dated "Update: contract 1.10" note recording why client-sent messages go through a per-extension handler, rather than letting extensions map their own WebSocket or hub. That alternative is in the review's contract table: every live extension would then solve sign-in and reconnects again.
- **Don't push an `sdk-v1.10.0` or `templates-v1.10.0` tag.** Publishing to nuget.org is permanent. Only Hunter does that.

**Done when:** `dotnet test Solo.slnx` is green, `build/pack-contract.sh` and `build/pack-templates.sh` succeed, and `dotnet test` passes for `extensions/Mcp/Solo.Mcp.slnx` and `extensions/Roadmap/Solo.Roadmap.slnx` on 1.10.

---

## Part B: the extension, `extensions/DesignDocs`

Extension id **`design-docs`**. That gives schema `ext_design_docs`, API base `/api/ext/design-docs/`, content base `/api/extensions/design-docs/content/`, and live topics prefixed `design-docs:`.

### B0. Scaffold, build pipeline and a prototype

Layout, mirroring Roadmap:

```
extensions/DesignDocs/
  Solo.DesignDocs.slnx
  Solo.DesignDocs/              server half: controllers, DbContext, migrations, live handler, extension.json, content/
  Solo.DesignDocs.Client/       client half: Razor pages and components, JS interop wrappers
  Solo.DesignDocs.Client/js/    JS sources, package.json, package-lock.json, esbuild config, node --test tests
  Solo.DesignDocs.Data.Tests/   Testcontainers tests against the schema, no host
  Solo.DesignDocs.Tests/        host tests (references src/ to load the package, like Solo.Roadmap.Tests)
build/pack-design-docs-extension.sh
```

`extension.json`:

```json
{
  "id": "design-docs",
  "name": "Design Docs",
  "version": "1.0.0",
  "soloApiVersion": ">=1.10 <2.0",
  "soloClientVersion": ">=1.10 <2.0",
  "server": "server/Solo.DesignDocs.dll",
  "client": "client/Solo.DesignDocs.Client.dll",
  "schema": "ext_design_docs",
  "nav": [],
  "hooks": [],
  "failOpen": false
}
```

Copy from Roadmap: the csproj comments and package references (contract `1.10.0`, `PrivateAssets="all"`), the schema-equals-manifest check in the entry point (`RoadmapExtension.cs`), the design-time factory, and the content URL with `?v=` plus the package version (`RoadmapApi.Content`).

**JS build.** This is the first extension that bundles JS. Roadmap's `content/roadmap.js` is hand-written. Use esbuild to produce one ES module, `content/design-docs.js`, plus `content/design-docs.css`, loaded with `import()` the way `RoadmapAssets.cs` loads Roadmap's module.

- Dependencies, all MIT (re-check each license when pinning): `@codemirror/*` and `@lezer/markdown` ([CodeMirror](https://codemirror.net/)), `yjs`, `y-protocols`, `y-codemirror.next` ([Yjs docs](https://docs.yjs.dev/)), `fflate` for zip files.
- Pin exact versions in `package-lock.json`. Pick the current stable release of each when starting, rather than versions named here.
- The bundle is a build output: gitignore it. `npm ci && npm run build` runs in the pack script and in a dev script that writes into the development package directory (`Extensions__DevelopmentDirectories__0`, writing-extensions.md §11).
- CI: add a Design Docs block to `.github/workflows/ci.yml` copied from the Roadmap one (change filter, test, package, upload), with `actions/setup-node` before the pack step.

**CSP check.** The bundle must run under core's policy as it is, with no script relaxation. CodeMirror injects styles at runtime, which `style-src 'unsafe-inline'` already allows. No `eval`, no inline scripts, no workers. Fonts come from `content/` (`font-src 'self'`).

**Prototype before building anything else.** Throwaway code on a branch, not merged. Prove:

1. Yjs over A2: three browser tabs typing in one `Y.Text` through a stub handler that only relays. Measure the delay between tabs and the hub's load. If .NET in the middle is too slow, batch harder in JS before changing the design.
2. CodeMirror inside a Razor component. The JS editor owns its DOM element and Blazor never re-renders inside it. Check navigation away and back, hot reload, and disposal.
3. The stamp renderer on an iPad with an Apple Pencil, at 72 paint pixels per inch: pressure, latency, and whether `getCoalescedEvents()` helps. An [Apple forum report](https://developer.apple.com/forums/thread/689375) found plain `pointermove` already delivers 240 Hz from the Pencil on an iPad Pro.
4. iPadOS backgrounding: lock the screen mid-edit, come back, and check the resync (B3).

**Done when:** the package builds and loads with an empty page, CI packages it, and the prototype's findings are written into this plan (change any section they contradict).

#### B0 findings (2026-10-22)

The throwaway implementation is on `codex/design-docs-prototype`; it is not part
of the extension implementation branch. Built package and browser evidence are
under `artifacts/` (ignored). A disposable Postgres database and synthetic local
sessions were used, with no access to a secrets store.

- The package loaded through the real extension host under the existing CSP.
- Three writer tabs each inserted 200 changes at 10 ms intervals, batched at
  50 ms. All three and a Read member converged on exactly 3,276 characters.
  The observed p95 acknowledgment was 11–12 ms and delivery was 9–12 ms on
  localhost; writers sent 40–45 messages including startup traffic. There were
  zero failures and zero outstanding acknowledgments. This is a local latency
  measurement, not a remote-network or server capacity benchmark.
- A simulated gap made one editor read-only. Another writer added 1,092
  characters; reconnect restored identical 4,368-character text in all tabs.
  The test drops traffic at the prototype bridge, not at the network layer.
- Blazor navigation away and back disposed/recreated CodeMirror and resynced
  its text without browser warnings or errors. Extension assemblies and static assets are cached at startup; the development package workflow requires a restart. In-place extension hot reload is not supported by the existing loader.
- Four JS tests cover reordered/duplicated updates and resync, byte encoding,
  pressure mapping, and deterministic stamp jitter. Hardware pressure, Scribble,
  coalesced events, palm rejection and iPadOS lock/resume remain unverified.
- **User-approved gate exception:** continue B1–B9 with the physical device
  checks explicitly pending. These results do not waive final device acceptance.
  Safari selection retention and desktop blob downloads also remain pending.

### B1. Data model (`ext_design_docs`)

Own `DbContext`. Bare `Guid`s for core ids, no foreign keys into core (writing-extensions.md §4). `Version` is `xmin` for optimistic concurrency, as in core. All timestamps are `DateTimeOffset` in UTC.

| Table | Columns | Notes |
|---|---|---|
| `Vaults` | `Id`, `ProjectId`, `Name`, `IsClosed`, `ClosedAt?`, `CreatedAt`, `CreatedBy`, `Version` | Index `(ProjectId)`. Unique `(ProjectId, Name)`. |
| `Folders` | `Id`, `VaultId`, `ParentId?`, `Name`, `Version` | Unique `(VaultId, ParentId, Name)`. Sorted by name, like Obsidian. |
| `Notes` | `Id` (the GUID in links), `VaultId`, `FolderId?`, `Name`, `Markdown`, `MarkdownSeq`, `Headings` (jsonb), `SeedMarkdown?`, `LastEditedBy?`, `LastEditedAt?`, `CreatedAt`, `Version` | Unique `(VaultId, FolderId, Name)`. `Name` is the file name without `.md`. |
| `Sketches` | `Id`, `NoteId`, `Name`, `PageSize`, `Orientation`, `Pattern`, `LastEditedBy?`, `LastEditedAt?`, `CreatedAt`, `Version` | `Name` is unique per vault (embeds refer to it). |
| `DocUpdates` | `Seq` (bigint identity), `DocumentId`, `IsSnapshot`, `Data` (bytea), `UserId`, `At` | One log for notes and sketches. Index `(DocumentId, Seq)`. |
| `Images` | `Id`, `VaultId`, `Path`, `ContentType`, `Data` (bytea), `Width`, `Height`, `Bytes`, `UploadedBy`, `UploadedAt`, `Version` | Unique `(VaultId, Path)`. `Path` is vault-relative, as in Obsidian. |
| `MemberColors` | `ProjectId`, `UserId`, `Slot` | Primary key `(ProjectId, UserId)`. Unique `(ProjectId, Slot)`. |

- Deleting a vault cascades inside the schema: folders, notes, sketches, update log, images.
- Deleting a note deletes its sketches and their logs.
- Images are kept until their vault is deleted, even when no note embeds them any more. That keeps undo working and makes "replace" safe. A cleanup of unreferenced images can come later.

**Done when:** the migration applies through the host at startup, and `Solo.DesignDocs.Data.Tests` covers the unique constraints and the cascades.

### B2. REST API: vaults, folders, notes

Every route carries the project id, so the project policies resolve it: `/api/ext/design-docs/projects/{projectId}/...` (writing-extensions.md §6). **Every handler also checks that the vault, note or image it loads belongs to that `projectId`.** Without that check, a member of project A could pass project B's ids under A's route.

| Action | Policy | Notes |
|---|---|---|
| List vaults, read the tree, read a note's metadata, markdown copy and headings | `ProjectRead` | The vault list includes `canManage`, from `ISoloQuery.CallerAsync().IsSuperAdmin`, so the client knows whether to show lifecycle actions. |
| Create, rename, close, reopen, delete a vault | `SuperAdmin` **and** `ProjectOwner` (two `[Authorize]` attributes, both required), as PR #29 does for boards | `ProjectOwner` is there so the project resolves from the route. `AccessPolicy.Allows` lets any super admin pass it, member or not (`src/Solo.Data/Domain/AccessPolicy.cs`), so the effective rule is "super admins only", as decided. Delete only a closed vault (409 otherwise). |
| Create, rename, move, delete folders and notes | `ProjectWrite`, and the vault is open (409 if closed) | Moving or renaming a note keeps its `Id`, so GUID links survive. |
| Store the markdown copy: `PUT notes/{id}/copy` `{ markdown, headings, seq }` | `ProjectWrite`, vault open | Kept only if `seq >= MarkdownSeq`. Sets `LastEditedBy` (the caller) and `LastEditedAt`. Cap the markdown at 1 MB. |
| Resolve a note for a link: `GET notes/{id}/locate` | plain `[Authorize]` | The one route without a project id, because links carry only the note GUID. Check membership through `ISoloQuery.ProjectsAsync`. Return 404, not 403, for a note the caller can't read, so its existence isn't revealed. |
| Resolve link labels: `POST links/labels` `{ urls }` | plain `[Authorize]` | For the link provider (B6). The same membership rule. Unreadable notes are left out of the answer. |
| Read and claim per-project colors: `GET`/`POST projects/{projectId}/colors` | `ProjectRead` | `POST` gives the caller the next free slot, idempotently (retry on a unique violation). |

After each change, publish on live topics with `ISoloLiveUpdates`, as Roadmap does after its saves:

- `design-docs:vaults` (the project's vault list): `changed`
- `design-docs:vault:{vaultId:N}` (a vault's tree): `tree`, `closed`, `reopened`, `deleted`
- `design-docs:doc:{documentId:N}` (one note or sketch): `deleted`, `renamed`

**Done when:** host tests (`Solo.DesignDocs.Tests`) cover each policy: a Read member can't write, a ReadWrite member can't create or close a vault, a super admin who isn't a member can manage vault lifecycle but cannot read vault contents or subscribe to their topics, writes to a closed vault get 409, and cross-project ids get 404.

### B3. Live sync: the relay protocol

One topic per open document: `design-docs:doc:{documentId:N}`, subscribed with the vault's project id. One `ISoloLiveMessageHandler` serves all of them. The server never decodes Yjs. It only checks who sent what kind of message.

**Every message is `kind` + `data`.** Binary Yjs payloads are base64 strings. Sequence numbers are decimal strings or a small JSON envelope. Keep the envelope minimal and documented in one C# file and one JS file that mirror each other, with a test that pins the shared examples.

| Kind (client → server) | Who | Server does |
|---|---|---|
| `sync` `{ afterSeq }` | anyone | Replies to the sender, in order: `state` chunks (the latest snapshot plus every update after `afterSeq`, each chunk ≤ 48,000 characters), then `synced { seq }`. If the note has `SeedMarkdown` and an empty log, see seeding below. |
| `update` `{ id, data }` | writers, vault open | Appends to `DocUpdates`. Replies `ack { id, seq }` to the sender, and relays `update { seq, data }` to the others. Refused (`refused { id, reason }`) for a Read member or a closed vault. |
| `update-part` `{ id, index, count, data }` | writers | For an update over the size cap. Reassemble per connection in memory, with a short expiry, then treat it as `update`. |
| `awareness` `{ data }` | writers | Relayed to the others, not stored. Dropped silently from a Read member. |
| `compact` `{ uptoSeq, data }` | writers | Replaces the snapshot and every update with `Seq <= uptoSeq` with one snapshot row, in one transaction. Ignored if `uptoSeq` is older than the current snapshot. |

**Client provider** (`js/sync/provider.js`):

- Wraps a `Y.Doc` and a y-protocols `Awareness`. Talks to .NET through a `DotNetObjectReference`: `send(kind, data)` out, `receive(kind, data)` in. The Razor component forwards to `ISoloLiveTopics.SendAsync` and from its topic subscription.
- **Batches local updates** for about 50 ms with `Y.mergeUpdates` before sending. Throttles awareness to about 15–20 messages a second, the review's number for pointers.
- **Keeps every unacked update** and resends it after a reconnect, on `ConnectionChanged` when `IsConnected` turns true. Yjs ignores updates it has already applied, so a duplicate is harmless.
- **Resyncs after any gap:** sends `sync { afterSeq: lastSeenSeq }` on reconnect. Anything relayed while disconnected was missed.
- **Online only:** while disconnected, the editor turns read-only and shows a banner. Unacked updates stay in memory. If there are any, warn on `beforeunload`.
- **Compaction:** after `synced`, a writer whose log has grown past a threshold (say 500 updates since the snapshot) sends `compact` with `Y.encodeStateAsUpdate(doc)` and the seq it has seen through. Don't elect a client centrally. If two writers compact at once, the server keeps whichever arrives with the higher `uptoSeq`, and both snapshots are valid.
- **Awareness cleanup:** when core's topic presence (`onPresence`) shows a user has gone, remove their awareness states, rather than waiting for y-protocols' own timeout.

**Seeding imported notes.** Imported notes arrive as markdown (B9), and the server can't turn markdown into Yjs. So:

- `SeedMarkdown` holds the imported text until a client converts it.
- The first **writer** to `sync` a note with `SeedMarkdown` and an empty log gets `seed { markdown }` and a lease of about 30 seconds, held per document in server memory. It builds the `Y.Text` and sends it as a normal `update`.
- The first stored update from the lease holder clears `SeedMarkdown`.
- Anyone else who syncs during the lease gets `seeding`, and retries `sync` after a moment.
- A Read member who opens an unseeded note sees the stored markdown, read-only, until a writer has seeded it.

**Last edited.** Each client sends its markdown copy (B2) about 2 seconds after its own last local edit, never for remote ones. That updates "last edited by", the headings and the text, with no extra write per keystroke.

**Handler notes.**

- Cache document → (project, vault, closed) in memory for a short time, and refresh it on the lifecycle events B2 publishes.
- A message whose document isn't in the given project is dropped. Same reason as B2's project check.
- Don't use `ISoloQuery` or anything request-scoped inside the handler. Everything it needs about the sender is in `LiveSender`. *Verify what `IHttpContextAccessor` returns inside a hub invocation before relying on anything request-shaped.*

**Done when:** two writers and a Read member converge on the same text through the real hub, in a host test with a JS-free C# fake client that replays recorded Yjs updates as opaque bytes. Also: a dropped connection resyncs without loss, a Read member's `update` is refused, a closed vault refuses writes, and seeding happens exactly once with two writers racing for it.

### B4. Pages, navigation and the vault tree

- `[ProjectNavSection("Design Docs")]`: the project's open vaults as links, with closed ones behind a toggle. "New design doc" only when `canManage`. Refreshes from the `design-docs:vaults` topic. `RoadmapNavSection.razor` is the model.
- `[ProjectHomeSection("Design Docs")]`: the same list as cards. `RoadmapHomeSection.razor` is the model.
- Routes:
  - `/docs/{ProjectAlias}`: the project's vaults.
  - `/docs/{ProjectAlias}/{VaultId:guid}`: a vault, with the tree on the left and the open note on the right. The note is `?note={noteId}`.
  - `/docs/note/{NoteId:guid}`: resolves through `locate`, then replaces the URL with the vault route, keeping the `#heading` fragment. This is the route task links use (B6).
- The tree is a `MudTreeView` of folders and notes. Create, rename, move (drag, plus a "Move to…" dialog for touch) and delete, through `ISoloContextMenu` on right-click and long-press. On a phone the tree is a drawer.
- Vault lifecycle actions (rename, close, reopen, delete) sit in the vault page's ⋯ menu, only for super admins. Delete asks for confirmation and is only offered when the vault is closed.
- Renaming a note that `[[wiki links]]` in other notes point at: v1 warns with a count, and leaves the links unresolved, shown in Obsidian's unresolved-link style. The count comes from searching the stored markdown copies. Rewriting the links automatically is a later phase (B10), because with the relay design it's a client-side edit of every referencing note.

**Done when:** a super admin can create a vault, a writer can build a tree in it, a Read member sees it read-only, and all of it updates live in a second tab.

### B5. The editor: CodeMirror live preview and presence

`js/editor/` mounts CodeMirror 6 with:

- **`@codemirror/lang-markdown`** and a **live-preview plugin**: a `ViewPlugin` that builds decorations from the syntax tree, hiding markup (`#`, `**`, `_`, link brackets and URLs, embed syntax) **except on lines that hold the local selection**. Decorations are per view, so other people's cursors never reveal anything, which is the decided behavior ([CodeMirror guide: decorating the document](https://codemirror.net/docs/guide/#decorating-the-document); [Obsidian: views and editing mode](https://obsidian.md/help/edit-and-read)).
- **Widgets** replace embeds when the cursor isn't on them: `![[image.ext]]` and `![](path)` become images (B7), `![[Name.sketch]]` becomes a sketch (B8), and task links become chips (B6).
- **`yCollab`** from y-codemirror.next, for the shared `Y.Text`, remote cursors and selections, and per-user undo.
- **Read-only** through a compartment holding `EditorState.readOnly` and `EditorView.editable`. On for Read members, closed vaults and while disconnected.
- **No raw HTML, ever.** Inline HTML in a note (common in imported Obsidian notes) shows as text. Never set `innerHTML` from note content. Links are allowed only when they pass the same rule as core's `SoloMarkdownPipeline.IsSafeUrl` (relative, `http`, `https`, `mailto`). Port it to JS and pin both with a shared table of cases. Core's reason holds here too: the session credential is readable by any script on the page.
- **Clicks:** an in-app link (`/...`) goes through Blazor's navigation, so it stays in the app and in the desktop shell. An external link opens a new tab, which the desktop shell sends to the system browser. A task chip opens `ISoloTaskDrawer.OpenAsync(taskId)` without leaving the page.
- **Headings** carry slugs (lowercase; spaces to `-`; drop everything but letters, digits, `-` and `_`; add `-2`, `-3` for repeats). The slug function lives only in JS. The client sends headings with each markdown copy (B2), so the server never needs its own copy of the rule. A `#fragment` that matches no heading opens the note at the top with a "heading not found" note.

**Presence bar** (Blazor, above the note): everyone on the document's topic, from `ISoloLiveTopics` presence. Writers report `Editing` while the editor is focused and `Viewing` otherwise. Read members always report `Viewing`.

**Colors:**

- Slot `n` (join order per project, from `MemberColors`) maps to hue `(n × 137.508°) mod 360` in OKLCH, with lightness fixed per color scheme and a moderate chroma. Golden-angle steps keep existing colors still when someone joins, and OKLCH spaces them more evenly than HSL (see the review's "Editor colors").
- Draw text cursors and selection highlights with a contrasting outline, so they stay visible over colored text and inks.
- Past about seven people, colors stop being easy to tell apart ([Healey 1996](https://www.cs.ubc.ca/tr/1996/tr-96-10)), so cursor labels always show the name.
- CSS `oklch()` needs Safari 15.4 or Chrome 111 ([MDN: oklch()](https://developer.mozilla.org/en-US/docs/Web/CSS/color_value/oklch)). Convert to `rgb()` in JS if an older target matters.

**Last edited by** shows under the note title, from the note's metadata, refreshed when the topic says it changed.

**Done when:** two writers see each other's cursors in their colors, markup appears only under each person's own cursor, a Read member sees both cursors and can't type, and a note with `<img onerror=...>` in it shows that as text.

### B6. Links between tasks and docs

1. **Picker in core's editor (A3a).**
   - A `[MarkdownEditorAction]` class in the client half offers "Link a doc" for `TaskDescription`, `EpicDescription` and `Comment`.
   - It opens the extension's own MudBlazor dialog: choose a vault (open ones) → search or browse notes → optionally a heading (from the stored `Headings`).
   - It returns `[Note › Heading](/docs/note/{noteId:N}#{slug})`, or `[Note](/docs/note/{noteId:N})`.
   - The URL has no project alias on purpose. A note's GUID is unique on its own, and a link without the alias survives a project alias change, as well as note renames and moves.
2. **Live labels (A3b).**
   - A `[MarkdownLinkProvider]` claims URLs starting with `/docs/note/`. It batches them to `POST links/labels` and caches answers for about 30 seconds.
   - The label it returns is the note's current title and heading text, so a renamed note shows its new name.
   - A note the reader can't open, or that was deleted, keeps the label as written. Don't reveal whether it exists.
   - The provider always replaces the label. Custom link text is not kept (see §5).
3. **Task chips inside docs.**
   - A toolbar button calls `ISoloTaskPicker.PickAsync` and inserts `[{SERIAL}](/docs/task/{taskId:N})`. The editor renders it as a chip with the task's current serial and title, fetched from core's task API (`api/tasks/{id}`).
   - Use the task's id rather than `||SERIAL||`. A serial changes when a task moves boards, and core only re-points serials inside its own descriptions and comments (`TaskReferences.Retarget`), never inside an extension's notes.
   - Add a small page at `/docs/task/{TaskId:guid}` that opens the task drawer, for when such a link is followed from outside the editor.
4. **"Linked docs" on a task.**
   - A `[TaskDetailExtension]` component parses `/docs/note/` links out of `Task.Description` on the client, and lists them with their resolved labels.
   - No server index is needed for this direction.
   - The reverse direction ("which tasks link to this note") is B10.

**Done when:** a writer inserts a doc link into a task description at the cursor, the rendered task shows the note's current title, renaming the note updates that label, and following the link opens the note at the heading.

### B7. Images

- **Upload** by paste, drop, or a file button.
  - In the browser, decode the image and re-encode it with a canvas to WebP (JPEG where WebP encoding isn't available), with the long edge at most 2,560 px. That avoids Safari's inconsistent HEIC handling ([WebKit bug 292350](https://bugs.webkit.org/show_bug.cgi?id=292350)) and strips location data.
  - Reject SVG in v1. An SVG opened directly can run script.
  - Upload with `SendBytesAsync` (A4) to `POST projects/{projectId}/vaults/{vaultId}/images?path=...`. The server caps the body at 10 MB with `[RequestSizeLimit]`, sniffs the bytes, and stores them.
  - Insert the embed at the cursor: Obsidian form `![[attachments/name.webp]]`, so export stays Obsidian-compatible.
- **Download** with `GetBytesAsync`, then `URL.createObjectURL(new Blob(...))`. Needs A1 for `blob:`.
  - Keep a per-page cache of object URLs keyed by image id and version.
  - Revoke them when evicted and when the page is disposed.
  - Serve image responses with core's isolation headers: `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; sandbox`, the same string as `SecurityHeaders.IsolationPolicy`, written out in the extension because it can't reference `Solo.Api`. Also `X-Content-Type-Options: nosniff`.
- **Look:** a small paper card, slightly rotated with a seeded angle per image, so it doesn't change between renders. Clicking it expands the image full screen over a darkened page.
- **The "same line" rule:**
  - Two or more image embeds in one paragraph, with nothing but whitespace between them, render as a fanned stack. Clicking the stack opens a full-screen carousel.
  - An image in a paragraph that also has text renders as its own small card, inline. No stacking.
- **Replace** (writers, from the image's context menu) uploads new bytes into the same row. Every embed updates, and a live `image` message on the vault topic tells open pages to refetch.
- **Resolving embeds** follows Obsidian's rules ([Obsidian: links](https://obsidian.md/help/links)): an exact vault-relative path first, then a unique file name anywhere in the vault. Decode `%20` in markdown-style paths.

**Done when:** a pasted iPad photo uploads as WebP, a non-member gets 404 for the image URL, and the stack, full-screen view and carousel work by mouse and touch.

### B8. Sketches

Each sketch is its own Yjs document on its own topic (`design-docs:doc:{sketchId:N}`), with its own row in `Sketches`. A note embeds it as `![[Name.sketch]]`. Moving that line moves the sketch within the note. Deleting the line keeps the sketch until the note is deleted, so undo brings it back.

**Model (`Y.Doc`):**

- `ops`: a `Y.Array` of finished operations in drawing order. Each is a stroke `{ id, tool: pen|eraser, ink, level, seed, points: [x, y, pressure, ...] }`, with points in paint pixels and pressure 0–1. A move is `{ id, kind: 'move', strokeIds, dx, dy }`, or rewrite the moved strokes' points in one transaction. Choose whichever keeps the eraser semantics below simpler, and note the choice here.
- `texts`: a `Y.Map` of text boxes `{ x, y, ink, scale, text: Y.Text }`.
- `meta`: page size, orientation and pattern. These mirror the `Sketches` row, which is what the REST API serves to lists.

**Rendering (`js/sketch/render.js`):**

- An offscreen canvas at the page's paint-pixel size: 72 per inch, so A4 is 595 × 842, A5 420 × 595, A3 842 × 1191 and Letter 612 × 792, swapped for landscape. It is shown scaled up with `image-rendering: pixelated`.
- Zoom in whole-number steps of screen pixels per paint pixel, so pixels stay even.
- Draw each pen stroke by stamping a small pixel brush at even spacing along the path, at whole-pixel positions, with `imageSmoothingEnabled = false`. No rotated stamps: use a few pre-drawn angles.
- Draw eraser strokes the same way with `globalCompositeOperation = 'destination-out'`, so they clear pixels from everything drawn before them. Draw the background pattern on its own layer underneath, so erasing never removes the grid.
- **Thickness:** five levels, each a brush width in paint pixels, starting at 1, 2, 3, 5 and 8 (to tune). With a stylus, width = the selected level's width rising toward level 5's as pressure rises, rounded to whole paint pixels. With `pointerType === 'mouse'`, ignore pressure: a pressed mouse reports 0.5 ([MDN: PointerEvent.pressure](https://developer.mozilla.org/en-US/docs/Web/API/PointerEvent/pressure)).
- **Boil:** each stamp's offset is a function of `(stroke.seed, stampIndex, frame)` in {−1, 0, 1} paint pixels. Cycle three frames a few times a second. Stop the loop when the sketch is off screen or the tab is hidden. Hold still on frame 0 when `prefers-reduced-motion` is set. That last point isn't from Hunter, but it's cheap and kind.
- **Inks:** theme ink (black in light schemes, white in dark), and red, blue and green, each with a light-scheme and a dark-scheme shade. The page background contrasts with the ink in each scheme.
- **Text:** a bitmap pixel-font atlas in `content/`, drawn glyph by glyph at whole pixels. Pick a font licensed OFL or CC0 and record its license in the package. While a box is being edited, an overlaid `<textarea>` bound to its `Y.Text` takes the input.

**Input (`js/sketch/input.js`):**

- Pointer events with `touch-action: none` on the canvas.
- Read `getCoalescedEvents()` where present.
- Palm rejection: ignore `touch` pointers while a `pen` is down.
- Suppress the long-press menu and selection on the canvas.
- Two-finger pan, zoom and rotate of the **view**, plus 90° rotate buttons. The rotation is a display transform, and pointer positions are rotated back into page coordinates.
- An Apple forum report says Scribble can swallow `pointerdown` events (same link as B0), so test with Scribble on.

**Tools:**

- **Pen.**
- **Eraser.**
- **Rectangle and ellipse:** generate a stroke. Shift constrains to a square or circle. iPads often have no keyboard, so add a "constrain" toggle on the toolbar too.
- **Select/move:**
  - Tap a stroke: flood-fill the visible pixels from that point, collect the strokes whose stamps landed in that region, and move them as a group.
  - When a stroke is only partly in the region because an eraser cut it, split it at the cut into separate strokes in the same transaction, then move the piece. This is the hardest tool. Build it last.
- **Text tool.**
- **Undo/redo:** `Y.UndoManager` scoped to the local user.
- **Clear:** with a confirmation.

**Live drawing:**

- Finished operations go into the Yjs doc (stored, via B3).
- A stroke in progress travels as awareness state (relayed, not stored), so others watch it being drawn.
- Pointer cursors in sketches travel as awareness too, throttled to 15–20 updates a second, drawn in the person's color, and only for writers.
- An Apple Pencil hovering on models that support it can show a pointer. A finger can't.

**Page changes:** changing the size or orientation to one that would clip line work shows a warning first. Clipped strokes stay stored and are hidden by the page bounds, so enlarging the page brings them back.

**Export PNG:** render frame 0 at 1× paint pixels, with the theme ink as black on white, colored inks in their light-scheme shade, and the pattern in light gray. Download as a `blob:` URL.

**Done when:** two people draw on one sketch at once and see each other's strokes and pointers. An eraser stroke cuts a line visibly, and two simultaneous erasers merge. Pressure ramps from the selected level on a Pencil and does nothing with a mouse. Export matches the screen pixel for pixel at 1×.

### B9. Obsidian import and export

**Import** (super admins and writers into an open vault; a new vault can be created from a zip by a super admin):

- Read the zip **in the browser** with fflate. Skip `.obsidian/` and anything that is neither a `.md` note nor an image embedded by one. Show a summary of what was skipped, and why, before importing.
- Create folders and notes in batches (`POST projects/{projectId}/vaults/{vaultId}/import/notes`, up to about 100 notes or 5 MB per call). Each note's markdown goes into `SeedMarkdown` unchanged, including front matter. B3 seeds it on first open.
- Upload images one by one with A4. The `/api` bucket allows a burst of 300 and then 100 requests per 10 seconds per user (`Program.cs`), so pace uploads and honor `Retry-After` on a 429. A vault with hundreds of images takes a minute or two, so show progress.
- Keep links as Obsidian wrote them (`[[Note]]`, `[[Folder/Note]]`, `[[Note#Heading]]`, `[[Note|text]]`, `![[...]]`, markdown links with `%20`). The editor resolves them with Obsidian's rules.
- Large bodies are the reason for batching rather than one upload of the whole zip. ASP.NET Core rejects bodies over 30,000,000 bytes by default ([Microsoft: Kestrel limits](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-5.0)).

**Export** (anyone who can read the vault):

- Build the zip **in the browser**.
- For each note, fetch its update log (a `GET` that returns the snapshot and updates), apply it to a fresh `Y.Doc`, and take the text. That's exact, unlike the stored markdown copy, which can lag.
- Add the image files at their paths.
- Render each sketch to PNG (B8) as `Name.sketch.png`, and rewrite its embeds to `![[Name.sketch.png]]` in the exported copy only. Obsidian can show a PNG but can't open a `.sketch`.
- Notes nobody edited since import come out byte for byte as they went in, because the seed is stored unchanged.
- Download as a `blob:` URL. *Verify the desktop shell saves a `blob:` download (Electron's `will-download`), and fix it in the shell if not. That would be a core change, so raise it first.*

**Done when:** importing a sample vault and exporting it again gives identical `.md` files for the notes nobody touched, and images resolve both ways.

### B10. Later, not in the first release

Listed so they aren't built by accident, and so the data model leaves room for them:

- **Search** across a project's notes: Postgres full-text over `Notes.Markdown`. The stretch goal.
- **Backlinks** ("tasks that link to this note"): an `ISoloEventHandler` on task changes parses descriptions into a table in the schema. Event delivery is best-effort, so add a reconcile endpoint that rescans a project's tasks through `ISoloQuery` as the caller.
- **Move a sketch to another note:** the nice-to-have. Re-parent the row, and move the embed line in the same user action.
- **Rewrite `[[links]]` on rename,** as Obsidian does ([Obsidian: links](https://obsidian.md/help/links)). The renaming client loads each referencing note's Yjs doc and edits it.
- **Read-only offline cache:** `y-indexeddb` ([Yjs: offline editing](https://docs.yjs.dev/getting-started/allowing-offline-editing)). Easy in the desktop app. In a browser it needs a service worker in core first, which Solo doesn't have. Wipe the cache on sign-out.

---

## 3. PR order

| # | PR | Contains | Depends on |
|---|---|---|---|
| 1 | Core: contract 1.10 | A1–A5 | — |
| 2 | Design Docs: scaffold, data, vaults and tree | B0 (minus the throwaway prototype), B1, B2, B4 | 1, and the board/roadmap lifecycle PR for its pattern |
| 3 | Design Docs: live editing | B3, B5 | 2 |
| 4 | Design Docs: task links | B6 | 3 |
| 5 | Design Docs: images | B7 | 3 |
| 6 | Design Docs: sketches | B8 (split into rendering and tools if it grows past review size) | 3 |
| 7 | Design Docs: Obsidian import and export | B9 | 5, 6 |

Bump the extension's version in `extension.json` and both csproj files together whenever its package changes. The pack script refuses a mismatch, as Roadmap's does.

## 4. Testing

- **Core (Part A):** the tests listed in each step. `dotnet test Solo.slnx` (integration tests need Docker for Testcontainers Postgres).
- **Extension, C#:**
  - `Solo.DesignDocs.Data.Tests`: the update log (append, compact, seed lease), cascades and constraints.
  - `Solo.DesignDocs.Tests` (host loaded): every policy in B2, cross-project ids, the live handler through the real hub (B3), and bUnit tests for the Razor components, as `Solo.Roadmap.Tests` does with its `FakeLiveTopics`.
- **Extension, JS:** `node --test` in `Solo.DesignDocs.Client/js`, run by the pack script and CI.
  - Two `Y.Doc`s converging through a fake relay that drops, duplicates and reorders messages.
  - Slug cases and `IsSafeUrl` parity cases (the shared tables).
  - The stamp renderer, for deterministic output given a seed.
  - The pressure-to-width mapping.
  - Obsidian link resolution cases.
- **Manual:** the `verify` skill (`.claude/skills/verify`) runs the app locally.
  - Check with two browsers and a Read member.
  - Check on an iPad home-screen web app with an Apple Pencil: pressure, Scribble, palm rejection, backgrounding and reconnect.
  - Check in the desktop app: `blob:` images and export downloads.

## 5. Assumptions this plan made

These weren't decided by Hunter. Each is the most likely reading, and each is cheap to change before its PR.

1. **A4 (binary calls on `ISoloClientApi`) joins contract 1.10.** It wasn't in the review's list. The fallback is base64 in JSON (A4).
2. **Doc links always show the note's current title.** Custom link text in task descriptions isn't preserved. That matches how `||SERIAL||` renders, and keeps the rule simple.
3. **Writers create, rename, move and delete notes and folders.** Super admins only control the vault's own lifecycle, which is what the decision named.
4. **A vault can be deleted only once it's closed.** This is from the original idea ("when closed they can be deleted").
5. **Doc links use `/docs/note/{guid}` with no project alias,** so they survive alias changes as well as renames.
6. **Renaming a note warns about `[[links]]` instead of rewriting them** in v1 (rewrite is B10).
7. **SVG uploads are rejected** in v1.
8. **Unreferenced images are kept** until the vault is deleted.
9. **Sketch boil holds still under `prefers-reduced-motion`.**
10. **Starting values to tune:** the hub size cap (48,000 characters), the live rate limit (burst 200, 50 per second), the five brush widths (1, 2, 3, 5, 8 paint pixels) and the image size cap (2,560 px, 10 MB).

## 6. Sources

Solo repository at `d6357e1`:
[CLAUDE.md](../CLAUDE.md),
[writing-extensions.md](writing-extensions.md),
[extension-plan.md](extension-plan.md),
`src/Solo.Api/Realtime/BoardHub.cs`, `LiveAccess.cs`, `TopicRegistry.cs`, `LiveUpdates.cs`,
`src/Solo.Shared/Hubs/BoardHubContract.cs`,
`src/Solo.Shared/Extensibility/ISoloLiveTopics.cs`, `ISoloClientApi.cs`, `ContractVersion.cs`,
`src/Solo.Extensibility/ISoloLiveUpdates.cs`, `ExtensionServiceCollectionExtensions.cs`, `SoloPolicies.cs`,
`src/Solo.Extensions/HookCatalog.cs`,
`src/Solo.Client/Realtime/LiveTopics.cs`, `BoardHubClient.cs`,
`src/Solo.Client/Components/SoloMarkdown.razor`, `SoloMarkdownPipeline.cs`, `MarkdownEditor.razor`,
`src/Solo.Client/Extensions/ContributedSections.cs`,
`src/Solo.Api/SecurityHeaders.cs`, `src/Solo.Api/Program.cs`,
`src/Solo.Desktop/main/server.js`,
`build/contract-package.props`, `build/pack-contract.sh`, `build/pack-roadmap-extension.sh`,
`extensions/Roadmap/` (as the model extension).

Design record: [Design Docs extension: feasibility review](https://claude.ai/artifact/RxZ5kdcCkJZN7LZunHY1CL) and its comment threads (2026-10-22).

Official documentation:
- Microsoft: [SignalR configuration](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration?view=aspnetcore-10.0) (32 KB default receive size), [Kestrel limits](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-5.0)
- MDN: [PointerEvent.pressure](https://developer.mozilla.org/en-US/docs/Web/API/PointerEvent/pressure), [oklch()](https://developer.mozilla.org/en-US/docs/Web/CSS/color_value/oklch)
- CodeMirror: [guide, decorating the document](https://codemirror.net/docs/guide/#decorating-the-document)
- Obsidian: [links](https://obsidian.md/help/links), [views and editing mode](https://obsidian.md/help/edit-and-read), [editor extensions](https://docs.obsidian.md/Plugins/Editor/Editor+extensions)
- Yjs: [docs](https://docs.yjs.dev/), [offline editing](https://docs.yjs.dev/getting-started/allowing-offline-editing)

Research: Healey, C. G. (1996). [Choosing Effective Colours for Data Visualization](https://www.cs.ubc.ca/tr/1996/tr-96-10). Proceedings IEEE Visualization '96, 263–270.

Bug reports and forums, for browser behavior with no official page: [Apple Developer Forums: 240 Hz Apple Pencil input in JavaScript](https://developer.apple.com/forums/thread/689375), [WebKit bug 292350: HEIC transcoding on upload](https://bugs.webkit.org/show_bug.cgi?id=292350).

## Implementation findings: durable relay

- The database enforces cross-vault folder/note parents, unique root names
  (`NULLS NOT DISTINCT`), and cascade ownership of both note and sketch logs.
  `Sketches.VaultId` enforces its vault-wide name namespace. Exclusive nullable
  `DocUpdates.NoteId` / `SketchId` foreign keys enforce log cleanup without a
  foreign key into core.
- Vault-row locks serialize closure/deletion with content writes and tree moves.
  Lifecycle state is read under that lock instead of caching closed status;
  this avoids an authorization window after closing a vault.
- Snapshots retain `ThroughSeq` separately from their new database identity.
  Replay sends the snapshot first and then its retained tail. Sync currently
  replays that full compacted state, making recovery robust to reordered live
  delivery; `afterSeq` is a future bandwidth optimization.
- `compact-part` extends the multipart envelope for snapshots that exceed one
  hub message. It uses the same bounded, expiring per-connection buffer as updates.
- Seeding uses a lease token and persists `Notes.SeededByToken`. A delayed seed
  from an expired lease is refused; retrying an accepted seed gets its ack
  without appending it twice. This closes a race the original envelope did not
  distinguish from an ordinary edit.
- B1 constraints/cascades: 10 database tests passed. B2 and durable relay: 10
  host tests passed, including recorded Yjs bytes through the real hub. B4: five
  component tests passed; browser creation of a vault, folder and nested note
  appeared immediately in a Read member's open page, with no mutation controls.
- Browser verification exposed an A2 implementation bug: the existing hub's
  state notification also fires for presence/subscription changes. Forwarding
  every notification as `ConnectionChanged` made focusing CodeMirror initiate a
  resync and remove focus. A regression test reproduced two false connection
  events; LiveTopics now filters by actual transport-state transitions. All
  eight LiveTopics tests pass, and sustained browser typing works with normal
  presence reporting. Temporary focus/status experiments were removed.
- B6: 19 extension host/client tests passed. In the browser, the core markdown
  action opened the extension picker, inserted a stable note GUID link into a
  comment editor, and returned focus to the selection. The linked-task route
  opened core's own drawer. A literal Razor chip URL found during this check was
  corrected to a bound URL.
- B7: four host tests passed for membership, isolated image responses, replacement
  concurrency, reader restrictions, SVG rejection and invalid paths. Browser
  upload converted a synthetic PNG to WebP and inserted its embed. Authenticated
  bytes displayed through a blob URL; a two-image paragraph formed a stack and
  opened the carousel. iPad photo decoding and touch gestures remain pending.
  Header checks follow [PNG IHDR](https://www.w3.org/TR/png-3/) and
  [WebP RIFF](https://developers.google.com/speed/webp/docs/riff_container).
- B8's move representation rewrites the selected stroke into remaining and moved
  segments in one local Yjs transaction; eraser operations keep their original
  order, and moved visible pieces are appended after them. A pixel-level test
  caught residual jitter fragments at the original location and now verifies
  they are cleared. Eight-neighbor selection and connected stamp interpolation
  keep thin diagonals selectable.
- B8 verification: 13 JS tests passed, including pressure, seeded raster frames,
  erasing, cut-piece movement and malformed-coordinate bounds; the sketch
  lifecycle/ownership host test passed. Browser drawing, rectangle, text, eraser,
  undo, cut-piece move and PNG download were exercised. Two open clients exported
  byte-identical still PNGs after edits from both. The PNG is 595×842 for A4.
  Hardware pressure, Scribble, palm rejection and multi-touch remain pending.
- The bitmap atlas is the original Solo Tiny 5 font, dedicated under CC0; its
  editable glyph rows and build script ship in source, and the PNG and license
  ship in the package. Basic ASCII glyphs are supported; other characters stay
  in the text data and render as a question-mark glyph.

- A4 exposes optional `SoloApiException.RetryAfter` so B9 can honor server
  pacing without bypassing the authenticated API. JSON and binary calls preserve
  both delta-seconds and HTTP-date headers; 14 API client tests passed.


## Implementation findings: import/export and final validation

- B9 imports UTF-8 notes without changing their text, including a BOM, CRLF,
  front matter, and dataview blocks. The browser preview lists skipped files
  and conflicts before any writes. Notes are sent in batches of at most 100
  and 5 MB; image uploads are paced and honor `Retry-After` on 429 responses.
- The browser-to-.NET transfer bridge uses JSON text and base64 image data;
  the authenticated HTTP requests still send UTF-8 JSON or raw image bytes.
  Browser verification caught and removed a failing optimized byte-array bridge.
- Import has explicit browser memory limits: 100 MB ZIP, 256 MB expanded,
  1 MB per note, and 25 MB per image before conversion. Export stops at
  512 MB uncompressed. Non-UTF-8 notes and unsupported paths are reported as
  skipped. Images already within upload limits preserve their original format
  and bytes; oversized raster images are resized in that format. Oversized
  animated GIFs are skipped with an explanation.
- Super admins with Read membership can import through dedicated import
  endpoints, matching B9's explicit exception. Non-member admins still cannot
  read or import vault content. Ordinary image uploads remain ProjectWrite.
- The browser round trip imported two notes and one embedded PNG, skipping
  settings, a canvas file, and an unused image. Both exported `.md` files and
  the PNG matched the input byte for byte. A wiki link with an alias and heading
  navigated to the correct note and fragment; Unicode content remained intact.
- The actual Electron shell, using an isolated synthetic profile, rendered the
  authenticated blob image and saved the blob ZIP through the macOS Save panel.
  Its exported note and image bytes also matched after first opening the notes.
  No desktop download change was required. Sketch PNG downloads were verified
  in the browser; physical device input remains pending.
- Final local automated results: core 778 tests (777 in the full run plus the
  shared URL-parity fixture), Design Docs 38 C# tests and 24 JavaScript tests,
  MCP 86 tests, Roadmap 202 tests. No skipped tests in those runs. Contract
  packaging, sample builds, and template option builds passed. The existing
  SignalR.Common 10.0.12 NU1903 advisory warning remains in restore output.
- Inline sketch cards show a still preview, refreshed when their editor closes.
  The sketch editor provides live collaboration and animated boil. The tiny
  bitmap font currently renders basic ASCII; other characters remain in the
  collaborative data and use a placeholder glyph.
- Pending device acceptance: Apple Pencil pressure/latency/coalesced events,
  Scribble, palm rejection, multi-touch view gestures, iPad photo decoding,
  and iPadOS lock/background/resume. These pending checks are not claimed as passed by desktop results.

- Final task-link checks: replacing a nonempty selection through the picker
  preserved the surrounding text and restored focus in both the in-app browser
  and native Safari. A 32-digit task GUID rendered the current serial/title and
  opened the core drawer without leaving the note.

- Final review found that repeated image basenames made note-relative embeds
  ambiguous in the editor. Image resolution now uses the note directory after
  exact vault paths and before a unique basename; its regression test also
  rejects external targets. Package 1.0.7 is included in preview build 2.

## First feedback round — Codex preview

The feedback build moves to client contract 1.11 for a general-purpose workspace
lease and initial task-picker filters. Design Docs remains removable: the host
layout names no extension. The editor uses the available page width and height,
with line-number gutters and its own scroll area; entering a vault collapses
main navigation, and the normal navigation button can reopen it.

Vault lists refresh for local mutations and navigation as well as remote live
messages. Creation uses the same sidebar link treatment as boards/roadmaps.
Obsidian ZIP selection, review, and import happen only inside the new design doc
dialog; existing vaults offer export only.

Media remains rendered on the selected line. Media-only lines form photo/sketch
stacks, with a fullscreen viewer, previous/next buttons at opposite edges,
Left/Right keys, swipe navigation, and removal of the selected embed. Removal
keeps the stored attachment so undo restores it. Relative Yjs positions keep
viewer actions attached to the correct occurrence during concurrent editing.
Cards can be dragged to move their embeds; notes and folders have drag handles
with native mouse dragging and touch dragging, including a root drop target.

Sketch editing is fullscreen with only the top-right close control. The view
supports continuous zoom and rotation, superseding B8's original integer zoom
rule: scroll pans, Ctrl/Cmd + scroll zooms, Alt + scroll rotates, Shift + scroll
pans horizontally, and Space + drag pans. Two-finger transforms and Safari's
trackpad gesture events use the same anchored transform. Physical iPad/Pencil,
Scribble, palm rejection, and backgrounding checks remain pending under the
previously approved hardware exception.

Browser automation confirmed native hierarchy moves and photo moves. A separate
sketch drag out of a mixed stack still needs manual confirmation: the automation
started the native drag but did not deliver a drop event at the target.

### Second feedback round

The user supplied an Obsidian reference for reading margins and requested a floating
insertion palette, MudBlazor controls, 64-pixel attachment previews, wider image fans,
viewer-only removal, and export in the header menu. The note tree keeps disclosure
carets only for folders; the redundant Move to menu entry is removed.

The sketch view uses tool/color/brush palettes at the upper left, history at the
upper right, and zoom/fit icons at the lower right. Page settings use a dialog.
Fit reserves room for these overlays; the separate Rotate view button is removed,
while continuous touch/trackpad rotation remains available. Theme-font text is
painted live under its transparent input, so committing text does not switch its
font or layout. Box and lasso selection show bounds and preview moved content
before the drag is committed. Paper and floating surfaces use 50% backgrounds.

Clear plays the user-requested transparent Deltarune explosion and sound, clears
ink/text halfway through its 1.19-second animation, and remains undoable. Reduced
motion skips the animation. Source revision and attribution are packaged in
`NOTICE.txt`; these game assets are separate from Solo's code license.

Validation: 38 JavaScript tests cover media deletion guards, box/lasso selection,
and moving selected cut pieces without changing unrelated objects. Browser checks
confirmed the reading margins, 64×64 contain sizing, pointer/typing palette behavior,
folder-only disclosure carets, menu actions, theme-font live text, multi-object move
bounds, page settings, and the transparent Clear animation. API readback confirmed
Clear changed two strokes and one text object to zero, and Undo restored all three.
Physical iPad/Pencil and iPadOS backgrounding checks remain pending.

The final extension suite passed all 40 C# tests. A screenshot comparison confirmed
the theme text's glyph geometry stays unchanged after leaving its input. Clear uses
a same-origin audio URL to preserve the host's existing media CSP.


### Third feedback round

The insertion palette follows the Markdown caret below its current line, clamped
to the visible editor and hidden when the caret scrolls away. Line numbers are
removed. Rendered ATX headings hide their delimiter's separator as well as the
hashes, correcting the extra title indent without changing stored Markdown.

The sketch previously rasterized the entire committed scene twice per draw and
issued one rectangle call per ink pixel. It now caches the three deterministic
boil frames, reuses the hit mask until strokes change, batches horizontal pixel
spans, and composites cached selection layers during a drag. Active local/remote
strokes apply to a copy of the cached raster, preserving eraser ordering. Selection
layers retain clipped brush edges. Theme changes invalidate the color layers;
shared stroke/page changes invalidate the scene. Blank or reduced-motion sketches
no longer request periodic boil repaints. Pan/zoom retain CSS transforms, which let
the browser use its compositor; actual GPU acceleration is browser/device dependent.

A native Chrome 155 canvas benchmark on this Mac used 200 seeded strokes with 30
points each, three warm-up draws, and 18 measured draws. Before/after median drawing
submission time was 170.1/2.9 ms (p95 191.6/3.5 ms); cached idle redraws fell below the
timer's resolution versus 170 ms before. These are synthetic warm-cache desktop
measurements, excluding document loading and GPU presentation; they do not establish
iPad frame rates. The approach follows [MDN's canvas optimization guidance](https://developer.mozilla.org/en-US/docs/Web/API/Canvas_API/Tutorial/Optimizing_canvas).

Touch drawing is off when opening a sketch: one finger pans and Pencil draws; the
finger toolbar toggle enables touch drawing. Pencil pointer identity survives an
unrelated palm's pointerup/cancel. The whole drawing dialog prevents native header
selection via user-select (including Safari's prefixed property), disables header
touch gestures/callouts, and guards selectstart outside editable fields. A separate
capture guard rejects palm interactions during a pen stroke. Opaque paper and
viewport backgrounds replace the previous transparent paper; only grid/ruled/dot
patterns render at half opacity.

Validation: 48 JavaScript tests and 40 extension C# tests pass. Pixel comparisons
cover cached frames, active erasers, scene/theme invalidation, cut-piece movement,
page-edge movement, and paper/pattern opacity. Browser checks confirmed zero line
number gutters, heading alignment, caret palette placement, the touch toggle, and
continued text selection inside sketch text inputs. Header drag/triple-click attempts
produced no text selection in desktop Chrome and Safari. Physical iPad/Pencil palm
behavior and iPadOS backgrounding still require device retesting; the touch-drawing
option is retained independently of those checks.


### Fourth feedback round

The notes tree sizes to its visible titles between 260 and 390 CSS pixels (the
user revised the original request to a 1.5× maximum). Nesting uses 12-pixel indents;
titles align left, and a title itself is the drag source. There are no separate
grab handles. Overflowing titles use Roadmaps' continuous hover/focus ticker,
with the same delay and speed, no touch animation, and reduced-motion support.
Its gesture follows core's board: 320 ms stationary touch hold,
10-pixel touch slop to preserve scrolling, and a 6-pixel fine-pointer threshold.
Dragging suppresses the following click so it does not open the moved note.

Folders start collapsed. Per-vault cookies remember expanded folder IDs, the current
note, and the editor font scale. Explicit note links take precedence over the saved
note, and saved notes are resolved only from the current authorized vault tree.
Large folder lists split into bounded cookie chunks. Font sizes from 75% to 200%
are available in the vault header. Preferences do not change shared Markdown.

Tab/Shift-Tab indent/dedent text and lists. Return continues bullet or numbered
markup, renumbers following ordered items, and exits an empty list item. Bullet
markers remain rendered while editing their text; source markers appear only when
a selection touches the marker itself. The vertical insertion palette aligns its
first action to the caret's current line and stays beside the left text margin.
The reading gutter reserves space for it at small viewport widths too.

The touch-drawing toggle only renders when the browser reports touch contacts.
Safari Force Touch pressure events can feed the same variable-width renderer as
Pencil, using the browser's click/force-click constants rather than hardcoded
thresholds. A constant click value from an ordinary mouse does not enable variable
width. Other browsers retain their existing input behavior. This is a feature-gated
WebKit integration, not a claim of cross-browser trackpad-pressure support; physical
Force Touch hardware verification remains pending. Sources: [Apple Force Touch API](https://developer.apple.com/library/archive/documentation/AppleApplications/Conceptual/SafariJSProgTopics/RespondingtoForceTouchEventsfromJavaScript.html),
[CodeMirror Tab handling](https://codemirror.net/examples/tab/), and [touch capability detection](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/maxTouchPoints).

Validation: 66 JavaScript tests and 44 C# extension tests pass. Tests cover cookie
round trips, large-tree chunking, corrupt preference recovery, list continuation and
nesting, marker-adjacent editing, touch hold/cancel behavior, browser timer receiver
requirements, and Force Touch normalization/brush widths. Browser checks cover
restoring folders/note/font without a note query, title dragging without changing
the selected note, adaptive sidebar bounds, left alignment, the moving overflow ticker, and the margin palette.
The touch toggle is absent on the desktop test platform. Physical iPad touch-drag,
palm rejection, iPadOS backgrounding, and Force Touch pressure still need hardware
retesting under the existing approved exception.

### Sketch follow-up fixes

Opening a sketch now suspends the underlying Markdown view with both CodeMirror's
read-only/editable facets and DOM `inert`. Permission updates cannot re-enable it
while the dialog is open; closing the dialog restores current permissions rather
than assuming write access. An asynchronous insertion can finish without stealing
focus from sketch text. Desktop typing did not reproduce the reported leakage,
but the baseline check confirmed the underlying view remained editable while the
dialog was open; the fixed browser check requires it to be noneditable and inert.
Physical iPad keyboard behavior still needs retesting.

The sketch provider buffers sync history until `synced`, then merges it into one
Yjs update. A failing replay test previously exposed an old stroke, its deletion,
and the current stroke as three visible states. Initial loading now exposes only
the current state, while subsequent live updates still apply immediately.
Interrupted loads resume from the last applied sequence, without skipping buffered
history.

A moving selection has a separate transparent canvas in the viewport. Its full
cut raster and bounding box travel beyond the paper edge until release; only the
committed drawing is clipped to the paper. The original raster already survived a
held off-page-and-back round trip in the unit harness; the missing behavior was a
preview independent of the paper clip. Tests now require that independent layer
and preservation of its pixels through movement. Real iPad pointer behavior remains
a device check.

The eraser uses Google's outlined `ink_eraser` Material Symbol, scaled from its
960-unit view box into MudIcon's 24-unit view box. Its Apache 2.0 license and source
attribution ship with the package. Source: [Google Material Symbols SVG](https://github.com/google/material-design-icons/blob/master/symbols/web/ink_eraser/materialsymbolsoutlined/ink_eraser_24px.svg).

Validation: 70 JavaScript tests and 44 C# extension tests pass. Browser checks
confirm sketch typing, a noneditable/inert background view, and restored editing
after closing. Reconstructing the saved Yjs document confirms its Markdown stayed
byte-for-byte unchanged after sketch typing and close. Preview 6's actions
were canceled before deployment; its immutable tag is retained. Preview 7 carries
these changes and Design Docs 1.0.12.


### Preference, URL, and import follow-up

A deterministic component test reproduced preference loss: rerender the vault while
`loadVaultPreferences` is pending, and the old implementation writes empty folders
and 100% font scale before hydration completes. This can overwrite valid cookies
on theme/auth rerenders. The page now gates saves and controls until preferences
are loaded; asynchronous responses from an old vault cannot replace a newer view.
Cookies still hold folder IDs, the selected note, and font size, with the new margin
width added. Old cookies default margins to Auto. Explicit widths of 0%, 5%, 10%,
15%, and 20% are available beside font scale; Auto preserves the existing reading
width behavior. The current note is marked with `aria-current=page` and the theme's
primary color. H4/H5/H6 have explicit heading styles.

Note URLs encode the complete 128-bit GUID as 22 unpadded base64url characters.
The GUID and all database keys remain unchanged. Canonical `/docs/note/<key>` URLs
render the vault directly instead of redirecting to a long vault/query URL. Old
GUID note links and old vault query links still open; newly generated note links
use compact keys. Label resolution, linked-doc extraction, wiki links, heading
fragments, and membership checks support both encodings. This is an encoding, not
an authorization token, and deliberately avoids a truncated 11-character ID.

Video files are filtered before decompression/import and generate a video warning.
Their Markdown references are preserved byte-for-byte. The editor recognizes video
embeds, including ones imported by older previews, without sending them to the
image endpoint; it displays a skipped-video label away from the editing cursor.
Unsupported attachments also avoid the image loader. Common metadata is ignored
silently: AppleDouble files, `__MACOSX`, `.DS_Store`, macOS indexing/trash metadata,
Obsidian settings, source-control metadata, `Thumbs.db`, and `desktop.ini`. Genuine
unsupported content such as canvas/Excalidraw files still appears in the report.

#### Storage and a future video option

The hierarchy is represented by `Vaults`, `Folders`, and `Notes` rows in the
extension's `ext_design_docs` PostgreSQL schema. Notes have a text Markdown copy;
collaborative Yjs updates and compacted snapshots are binary `DocUpdates` rows.
Export reconstructs current text from that log. Images are `bytea` values in the
extension's `Images` table. No Markdown files or image files are edited on disk by
the current design.

Videos are not inherently incompatible with PostgreSQL binary storage, but the
current image endpoint materializes a whole blob and is not a video-streaming
implementation. For larger or numerous videos, a future implementation could use
a separate media dataset mounted outside PostgreSQL's data directory, or object
storage. Keep vault/project ownership, MIME type, byte length, checksum, and a
server-generated storage key in PostgreSQL. Serve content through the existing
project authorization model with HTTP Range support for seeking. Coordinate database
and media backups and handle orphan-file cleanup. This is a design note only; this
preview neither adds a media mount nor imports/stores video bytes. The database
storage mechanics are described in [PostgreSQL's TOAST documentation](https://www.postgresql.org/docs/17/storage-toast.html),
and [S3's GetObject API](https://docs.aws.amazon.com/AmazonS3/latest/API/API_GetObject.html)
is one example of object storage supporting byte-range reads. The suggested split
is an architectural recommendation, not a measured size threshold for this setup.


Validation for this follow-up: 56 C# extension tests and 75 JavaScript tests pass.
The preference lifecycle test was run red before the hydration guard and green
afterward. Import/video tests were also run red before the fix. Browser verification
used an isolated fake identity and PostgreSQL fixture: changed the instance theme
from Ahlquist Interactive to Blueprint, signed out and returned with the fixture
identity, restarted the server against the same database, and reloaded the vault
without a note query. Expanded folders, the remembered note, 150% font scale, and
10% margins persisted. A legacy GUID link became a 22-character note route while
retaining its heading fragment. The current note color matched the active primary
color. At a 21 px body size, H4 measured 24.15 px and H5 22.575 px. Video embeds
rendered skipped-video labels; fenced examples stayed source text. Physical iPad,
Pencil, keyboard, backgrounding, and Force Touch checks remain pending under the
existing approved exception.


## Desktop materials and vault navigation feedback (Codex preview 11)

The workspace owns both vault routes and compact note routes. Moving between notes
keeps its workspace lease, so an explicitly reopened main sidebar stays open. The
sidebar vault and the selected hierarchy row use the same primary color, hover
background, and end border. All Projects receives a project-card contribution;
closed vaults use an archive expansion panel with Reopen and View actions.

Origin-local persistent storage now owns the vault preferences, with migration
from the earlier cookie chunks and a cookie fallback when storage is unavailable.
This separates desktop instances by their stable localhost ports and avoids cookie
eviction. The font scale, margin width, current note, and expanded folder IDs
survive leaving the page and restarting the desktop app. Bulk collapse keeps the
current note's ancestor path open. Folder carets have centered primary-color guides;
folder and note icons are removed. Dragging a note title into the editor inserts a
compact document link at the drop position. Document links stay underlined in the
primary color until the caret touches their source.

Attachment previews use 128×128 contain sizing at 100% and scale with the font
setting. The viewer supports image replacement, image download, sketch PNG export,
and backdrop dismissal. Attachment deletion by keyboard remains blocked; its
explanation is a temporary snackbar. The drawing editor does not close on backdrop
clicks. Its title follows the core Mac traffic-light inset and Windows overlay
control insets. Native glass surfaces stay transparent around opaque Markdown
content and drawing paper. A content filter blurs the underlying layout while a
viewer or drawing dialog is open; this was needed because the native glass window
did not visibly apply the otherwise computed backdrop filter.

Validation: 56 extension C# tests pass, including compact-route workspace lifetime
and preference hydration; 78 JavaScript tests pass. Browser checks cover matching
sidebar highlights across file switches, note drag insertion, image replacement,
download/PNG export, backdrop dismissal, temporary warnings, and closed-vault
reopening. A native Mac desktop fixture verifies glass, title spacing, and
restoration of a nested note, 125% font scale, 10% margins, and folder states after
quitting and reopening. Physical Windows titlebar and iPad/Pencil/Force Touch
checks remain pending under the existing hardware exception.


## Layout and loading feedback (Codex preview 12)

The displayed vault identity is separate from incoming route parameters. Compact
note resolution no longer falls through to the vault list while awaiting the API.
The previous note stays mounted beneath a darkened blur and circular progress
indicator confined to the Markdown pane. A hidden incoming editor becomes visible
only after its sync completes; keyed surfaces avoid remounting it at that point.
Late sync completion from a superseded navigation cannot select an older note.

Hierarchy caret buttons have square hit areas and round hover backgrounds. Row
highlights have a small left inset from the folder guide, include that inset in
their total width, and retain their selected edge inside the sidebar. Action
buttons align with the root's expand/collapse control. Add Note/Folder is centered.
Native modal titles and right controls share the desktop's 70-pixel titlebar axis.

The attachment viewer expands the clicked photo or sketch from its thumbnail,
then returns to the current thumbnail on dismissal. The background wash and native
content blur fade with the movement. The selected thumbnail moves to the front
before the wash appears; the full-size image stays hidden until the first animation
frame, avoiding an intervening flash. Closing during opening reverses from its
current transform. Reduced-motion preferences disable the transitions.

Validation: 57 C# extension tests and 78 JavaScript tests pass. The compact-route
loading regression failed before the fix and passes afterward, including retaining
the editor until sync and ignoring a late superseded sync. Browser checks use an
artificially delayed note lookup to verify that the tree and vault header stay
visible while only the content pane is covered. Measured caret buttons are 24×24;
all hierarchy action centers align with the root control, with the selected edge
inside the sidebar. Browser and native Mac checks cover thumbnail expansion,
return dismissal, carousel keys, and opening the sketch editor after dismissal.
Native screenshots verify the sketch title and close button in the header row.
Physical Windows titlebar and iPad/Pencil checks remain pending.


## Final appearance and desktop rotation feedback (Codex preview 13)

Folder guides use a dotted 1px primary-color border at 50% opacity. The vault
header background uses `max(50%, --solo-glass-background-alpha)` so it stays at
least half opaque and follows higher desktop settings. Purple is available in
the sketch palette and both raster/text rendering, including cached frames,
previews, and exported PNGs.

The desktop rotation failure was a missing transport: the sketch listened for
Safari DOM gestures, while Electron reports AppKit rotation on BrowserWindow.
The shell now sends a generic native rotation event through preload as plain
numeric DOM event data. It retains incremental degree values and the zero end
marker, converts screen coordinates to renderer CSS coordinates at the current
page zoom, and leaves all Electron objects in the isolated context. The sketch
only consumes the event over its drawing viewport, ignores text inputs, keeps
its anchor fixed during a gesture, and detaches on disposal. AppKit's angle sign
is converted to CSS's clockwise convention. This requires the updated desktop
installer; the extension remains independent of core's projects and schema.

Validation: 57 C# and 81 JavaScript tests pass. A regression driving the real
instance-window registration and preload failed with zero renderer events before
the bridge and passes afterward. Viewport tests cover fractional deltas in both
directions, anchor preservation, gesture end, invalid data, input/viewport
exclusions, and disposal. The purple render test compares live cached output
with exported pixels for strokes and text. Browser computed styles confirm
header alpha values 0.5/0.5/0.5/0.75/1 at settings 0/25/50/75/100%, and a dotted
1px guide with alpha 0.5. The native Mac fixture displays the palette and updated
vault surfaces. The user reported rotation working during verification.


### Pencil selection interruption follow-up (Codex preview 14)

The user's iPad recording shows the system Copy/Translate/Copy Link with Highlight
menu appearing repeatedly during rapid handwriting, including strokes away from
the header and without a palm resting on the screen. The existing pointer-default,
CSS and selectstart guards were therefore insufficient on the device.

A mountSketch regression fixture exercises the production DOM listeners, raster
renderer, Yjs document, and outgoing sync updates. Before the change, native
touchstart remained uncanceled and selectionchange left a native range intact.
The regression reports both failures. This reproduces missing safeguards, not the
iPadOS native recognizer itself; the recording remains the hardware evidence.

The viewport now cancels touchstart/move/end through non-passive capture listeners,
without adding a second touch drawing path. Pointer Events still own drawing,
one-finger panning and two-finger navigation. Selectionchange removes stray ranges
while the modal is mounted, except when a text field has focus. Context menus are
blocked across the modal except inside editable fields. Compatibility mouse
selection defaults are blocked on the viewport; explicit WebKit selection and
callout properties cover the paper and header descendants. Every new listener is
removed on disposal. Toolbar touch defaults remain available for normal clicks.

Five regression checks cover 40 rapid strokes committed once each, native selection
cleanup with text-field exceptions, cancellation recovery and palm priority,
Pointer Event pan/zoom, toolbar taps and disposal. The cancellation/recovery check
already passed with the earlier input state machine; it did not need another
state-machine change. Actual iPad Pencil/Scribble behavior requires retesting.

Apple's [canvas input guidance](https://developer.apple.com/library/archive/documentation/AudioVideo/Conceptual/HTML-canvas-guide/AddingMouseandTouchControlstoCanvas/AddingMouseandTouchControlstoCanvas.html)
describes canceling native touch defaults. [WebKit issue 249223](https://bugs.webkit.org/show_bug.cgi?id=249223)
reports native iOS selections despite event cancellation. These sources support
the extra safeguards; they do not establish the exact internal cause on the
user's current iPadOS version.

### Presence follow-up — local design review, not published

Presence updates were discarded because initialization and disconnect set the
Yjs Awareness state to null, and setLocalStateField cannot recreate a null state.
Writable sync now creates a local state with the assigned user profile. The
awareness sender also throttles rather than debounces: continuous Pencil motion
or typing must not postpone every packet. Topic presence changes reannounce the
current state to newly joined collaborators without waiting for another movement.
Regression checks isolate initialization/reconnect, continuous movement, and
stationary late joins; both timing checks still failed after initialization alone
was repaired.

The user requested profile pictures joined to pointer arrows and I-beam carets,
with the assigned project color outlining both shapes. The implemented local
review uses those cursors, a brush-footprint ring, and profile-picture rosters in
both Markdown and the sketch header. Pictures use the receiving instance's avatar
proxy, never another desktop client's localhost address. Missing pictures fall
back to initials. Persistent color slots drive cursor, selection and avatar
borders; a shared CSS lightness variable adapts them to the local background.

A local two-client relay fixture runs the production editor, sketch, Yjs provider
and cursor components. It verifies both text and brush cursors arrive, all demo
profile pictures load, and pointer tips align with brush footprints. The review
shows enlarged examples alongside the actual-size editor rendering. The user
explicitly requested reviewing this design after the presence repair and before
publishing a preview; this follow-up remains unpublished pending that review.

The pointer's two edges are true tangents to the avatar's border circle; that
circle's near arc is the concave notch, producing one continuous silhouette.
Geometry tests verify circle contact and perpendicular tangent/radius vectors.
Local validation passes 58 C# and 92 JavaScript checks. No tag or preview was
published for this review.
