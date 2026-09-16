# Solo.Mcp.Client

The MCP extension's client half: the pages a user sees. Packaged into
`solo-mcp-<version>.zip` under `client/`, downloaded by the browser at
bootstrap, and handed to the host's router — so `@page "/mcp"` resolves exactly
as a page compiled into the app would.

One page, [McpAccess.razor](McpAccess.razor): generate a token, copy the config
your client wants, and — for a super admin — audit every token on the instance
with a 24-hour usage graph and an emergency revoke-all.

[LlmWork.razor](LlmWork.razor) contributes the task drawer's **Work for an LLM**
checkbox through the shared `[TaskDetailExtension]` slot. It appears for tasks
with an assignee who has active MCP access, and saves through the extension's
versioned endpoint.

## What an extension page gets, and what it brings

Everything ambient comes from the host's service provider: MudBlazor and its
dialog and snackbar services, `NavigationManager`, `IJSRuntime`, the
authentication state the router authorizes against. The one thing the host had
to grow for this is **`ISoloClientApi`** (in `Solo.Shared.Extensibility`) — an
authenticated call into the instance, implemented by the host's own `ApiClient`,
so a package shares the app's credential rather than inventing one.

Note its two resolvers. `Resolve` is for something this browser fetches — the
stylesheet below. `ResolveExternal` is for an address a person copies out of the
app, which is what the connection snippets are: in the desktop shell the page's
origin is a local proxy that only exists while the app is running, so an MCP
client configured with it would break the moment Solo was closed.

What it brings itself:

- **Its API shapes.** [McpApi.cs](McpApi.cs) mirrors what `Solo.Mcp` serves. The
  two halves ship in one archive and version as one, so they agree by
  construction rather than through a third shared project.
- **Its dialogs.** The host's live in `Solo.Client`, which an extension cannot
  reference.
- **Its stylesheet**, from the package's `content/` folder, linked by the page
  through `HeadContent`. In the package rather than the host's `app.css` so it
  arrives and leaves with the extension.

It deliberately does **not** call any `window.solo.*` interop: that is the
host's private JS surface, and depending on it is how a package breaks on an app
update. Clipboard access goes straight to `navigator.clipboard`.

## Constraints worth knowing

**A `PackageReference` to `Microsoft.AspNetCore.Components.Web`, never a
`FrameworkReference` to `Microsoft.AspNetCore.App`** — the framework reference
does not resolve for `browser-wasm`, the same constraint that keeps
`Solo.Extensibility` out of reach here and puts the shared contract types in
`Solo.Shared` instead.

**The host's `PublishTrimmed=false` is load-bearing.** This assembly calls
MudBlazor and framework members the host app may not itself use; a trimmed
Release build would remove exactly those and fail this page with
`MissingMethodException` in production only.

**MudBlazor is not packaged.** It resolves to the copy the host already loaded,
which is why the version here has to track `Solo.Client`'s.
