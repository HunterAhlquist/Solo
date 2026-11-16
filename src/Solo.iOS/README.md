# Solo for iPhone and iPad — native client (experiment)

A SwiftUI client for a Solo instance. It talks to the same REST API and the same SignalR hub as the Blazor client in `src/Solo.Client`, so nothing on the server changes for it: point it at an instance and sign in.

It is an experiment and lives on its own branch. It is not part of `Solo.slnx`, of the release train, or of any workflow — it builds and runs locally, from Xcode or `xcodebuild`.

## Running it

Requires Xcode 26 and an iOS 26 simulator or device (the deployment target is iOS 26, for Liquid Glass).

```bash
open src/Solo.iOS/Solo.xcodeproj
```

Choose the `Solo` scheme and an iPhone or iPad destination, and run. The packages resolve on the first build.

On first launch the app asks for the instance's address, then signs in through the instance's own sign-in page in a system sheet. It keeps the session token in the Keychain and the instance in its settings, so later launches go straight in.

The tests are in the `SoloTests` target (`⌘U`, or `xcodebuild test`). They cover the rules the app has to share with the server and the web client — dates, serial numbers, rank neighbours, the roadmap's sprint arithmetic, note addresses and heading slugs, the Design Docs sync batch format, and the sketch rasterizer against values taken from the web one.

### Debug hooks

Debug builds read three environment variables, which is how the app was driven on simulators without typing a Google password into one:

| Variable | Effect |
|---|---|
| `SOLO_INSTANCE_URL` | The instance to use, skipping the address screen. |
| `SOLO_SESSION_TOKEN` | A `solo_…` session token to resume with, skipping sign-in. |
| `SOLO_OPEN` | Where to start: a tab (`work`, `projects`, `inbox`, `account`), a project (`DMD`), a board (`DMD-PW26`), a task (`DMD-PW26-2`), `roadmap:DMD:<id>`, `docs:DMD:<vault id>[:<note id>]`, or `route:<mcp\|admin\|devices\|notifications\|extensions\|themes\|users\|email\|updates\|about>`. |
| `SOLO_PERF` | Writes a line a second to `Documents/perf.log`: frames drawn, the longest wait between two, how much of the second was spent late, and how many zones and cards were built. |
| `SOLO_PERF_SCROLL` | With `SOLO_PERF`, a speed in points a second: once the screen has settled the app scrolls its longest page down at that speed and back up at three times it, and the log says where it is. The same swipe every run, and the only way to scroll a device from here. It also keeps the screen awake. |

With `simctl`, prefix each with `SIMCTL_CHILD_`. None of this is compiled into a release build — unless it is built with `PERF` among its compilation conditions (`SWIFT_ACTIVE_COMPILATION_CONDITIONS='$(inherited) PERF'`), which is how the app is measured optimised: a debug build's timings say little about a release build's.

## How it is put together

```
Solo/App          app entry, launch states, the signed-in Session, the Navigator
Solo/Core         API client and endpoints, models, auth, the live hub, theme, stores
Solo/UI           shared pieces: markdown, images and icons, cards, prompts
Solo/Features     one folder per area of core: board, dashboard, projects, inbox, account, admin
Solo/Extensions   the first-party extensions' clients, and the slots they fill
SoloWidgets       the Home Screen widgets, an extension of the app
SoloShared        the little the app and its widgets both compile: what they share, and its shape
SoloTests         unit tests
```

- **One navigation model, two shells.** `Navigator` holds a tab and a stack of routes per tab. An iPhone shows it as a tab bar; an iPad as a sidebar beside the selected stack, with a task's detail in a panel down the trailing edge — three panes in landscape. Routes are aliases, as the web app's URLs are, so a link means the same thing on every client.
- **The task panel is the shell's.** On an iPad it is not the system inspector, which sits inside the page's navigation stack — under the bar, with the page beside it snapping to its new width. `DetailPanelCenter` lets a page hand its panel to the shell, which draws it as a pane of glass over the bar's trailing end; the page makes room through its safe area, which animates.
- **A phone's board is one page.** The three columns are not three scroll views. Every row — an epic — holds its three zones side by side and shows the one the page's column names (`PagedColumns`), so a row is at the same height and under the same header whichever column is showing, and a sideways drag anywhere turns all of them together. A card that changes column is followed there and centred.
- **A big board is built a lane at a time.** The page is a lazy stack whose rows are lanes, and a group of epics is its title and then each of its epics as a row of its own (`BoardRow`), with the group's outline drawn in pieces that meet — a group used to be one row, so opening a board built every epic of its first group. Two layouts keep the page from being measured more than once: `ZoneRow` gives a lane's three zones a third of the width each without asking them how flexible they are, and `FillLayout` stops the bars around the page from asking the scroll view how tall all of its content is, which a lazy stack can only answer by building rows. The board's tasks are sorted out by lane and column once each time the board changes (`BoardIndex`), not once for every zone drawn. Your Tasks builds its cards as they scroll into view; on a phone a column that a card has just been moved to is built whole, so that the page can go to the card. On the test board of 580 tasks in an optimised simulator build, opening went from one frame of 940 ms and nine lanes built to 180 ms and two; Your Tasks at launch from 830 ms and every card to 180 ms and the ones in view. Within a lane, each zone builds its cards as they scroll into view too, and draws their glass in one `GlassEffectContainer`. That came from measuring on an iPhone Air, with the app scrolling itself (`SOLO_PERF_SCROLL`) down the same board at 1,000 points a second: with every card of an arriving lane built at once and a layer of glass each, the phone held 112 frames a second out of 120 while cool and about 60 once it reported its thermal state as fair, which a few minutes of use is enough for. Lazy zones alone gave 92 warm and the container alone 83; together 110 to 120, warm or cool. A container is only used for a zone of up to 30 cards: around a column as tall as one of Your Tasks' it halved the frame rate instead, so Your Tasks has none. What was not measured: an iPad, and what the render server does with the glass — the log only sees the app's own main thread, and the hitch recorder in Xcode could not reach the phone over Wi-Fi.
- **Finished is held to what is left to do.** As on the web (`KanbanZone.razor`, `js/finished-column.js`), and to the same numbers: a Finished column's cards may stand as tall as the lane's active columns, or 340 points where those are shorter, and no taller. Past that they scroll inside the column, and a column that has to scroll starts put away behind "Show finished (17)", so a lane is as tall as the work left in it; past 25 cards the rest fold behind "Show all". It is one component, `FinishedFold`, on a board's lanes and on Your Tasks. Three things are the app's own. Cards put away are not built, so whether they would fit is judged by the last time they were, or by a guess from the cards beside them, where the web keeps them laid out and hidden. A column that scrolls does not bounce, so that a drag past its end goes on to the page: on a phone the column is most of the screen. And a card dropped on a column that is put away still lands in it, where the web takes no drops there. The active columns' heights are kept in `LaneHeights`, which only the Finished column observes — as the lane's own state they had every column of the lane worked out again a hundred times a second while scrolling.
- **What is picked up is carried.** A card, a lane or a group of lanes that is pressed and dragged is in hand as itself — the same size, a little raised, where the finger took hold of it — and what it is carried over parts to leave it room, which is where it goes when it is let go (`CardDragWatch`, `CarriedCard`, `SlotGap`). The drag is the system's, so a card can still be let go on a column of the selector, held at the page's edge to turn the column, or taken to another app; the system is handed a preview of nothing, since what it would show is the thing shrunk. While a lane is in hand every lane is folded, and every group too while a group is, so that it is carried over a short list of names; the page opens again once it is put down, and goes to it. A lane is put in a group by being let go among its lanes, or level with its last one. Your Tasks carries its cards the same way, with the same pieces, though a card there is only ever put in another column and no room is made for it among the cards: where it lands is its board's to say. Four things are not as they would first be written. What is in hand is learned from the name its item provider carries when it first arrives over somewhere it could be let go, and not from the system asking for it: the system asks at a press that goes on to open a menu, and does not ask again for the next press on the same card. The places a card is let go are the zones, and for a lane the page — never the cards or headers it goes between, which are moving out of its way at that moment, so that a finger placed by one of them was a frame out and the thing in hand shook. And which gap the finger is over is judged by the middles of the rows as they stand with the room open, so that opening the room does not change the answer. And the card left behind while one is in hand — it has to stay, since the system carries the drag by it — is put far off to one side, because the system keeps a picture of what it lifted and draws it wherever the card is, whatever is said of the card's opacity. The system moves that picture at whatever pace is going when the card moves, so picking up and giving back are each two steps: the swap of the card for the one drawn in hand, with no animation at all, and a moment later the place closing or opening at its own pace. Done as one, the card was seen flying off the screen as it was picked up and flying back in as it was put down. Where a card stands is kept by the column it was built in, since a card that changes column is for a moment built in both, and the copy on its way out goes on saying where it is.
- **Live by default.** `HubClient` joins the board, user and extension topics the web client joins; stores apply what arrives. Saves send the row's `version`, and a 409 refreshes and says so. A page kept live this way — a board, Your Tasks, the inbox, a roadmap — has no pull to refresh, which would only fetch what it already has. The pages that are read once when they are opened still have it: the project lists, a project's page, the admin and account lists, and an extension's page.
- **Offline is read-only.** The last good response to every GET is kept on disk, so the app opens and reads without a connection and shows that it is offline. It does not queue writes; the one exception is a note or sketch that is open, which keeps unsent edits and sends them when the connection returns.
- **Theme.** The system's materials, backgrounds and type throughout, with Liquid Glass for panels and bars. The instance's theme contributes its primary and secondary colours — as the tint, and as a faint wash behind the page — and nothing else.

- **Widgets.** Three, one of each size, showing what the Capacitor app's show: how much is assigned to you, the same as a small board, and what is due this week. A widget runs in its own process, so the app and the extension share one keychain access group (it needs nothing registered, unlike an app group): the app leaves the instance, a copy of the session and the theme's accent there, and a widget keeps the last summary it drew for when the instance can't be reached. The summary is worked out from `GET api/users/me/tasks`, which every instance serves. A tap opens the app at Your Tasks or at a task, by the app's own URL scheme.
- **Local network.** The first connection to an instance on the LAN fails while iOS asks whether Solo may use the local network. The app waits for the answer and tries again rather than showing that failure; a refusal has its own screen, with the way to Settings.
- **Accent as a fill.** The instance's accent is whatever the theme says, and may be a bright yellow. Where the app draws on it, the label is black or white by contrast — set on the label, since a prominent button's style overrides one set on the button. Where the system draws white on it and gives no say (a switch's knob, a swipe action's icon), the fill is the nearest shade of the accent that white reads on.

### Libraries

| Package | For |
|---|---|
| [dotnet/signalr-client-swift](https://github.com/dotnet/signalr-client-swift) | The SignalR hub. Microsoft's own client; pinned to an exact preview version. |
| [gonzalezreal/swift-markdown-ui](https://github.com/gonzalezreal/swift-markdown-ui) | Rendering Markdown. |
| [swiftlang/swift-markdown](https://github.com/swiftlang/swift-markdown) | Parsing Markdown into a syntax tree with source positions, which the live note editor styles from. |
| [kean/Nuke](https://github.com/kean/Nuke) | Loading and caching avatars and icons with the session's credential. |
| [swhitty/SwiftDraw](https://github.com/swhitty/SwiftDraw) | Decoding the SVG avatars and icons the instance serves. |
| [y-crdt/yswift](https://github.com/y-crdt/yswift) | Yjs documents, for Design Docs' live notes and sketches, and their undo history. |

Three things are written here because nothing maintained covers them, each as small as it could be made: Yjs's awareness protocol (a varint-framed JSON state per client), a reader for the structure of a Yjs text (below), and the transition between tabs.

## Extensions

On the web an extension ships its client as an assembly the browser downloads. An app can't load code, so the three first-party extensions' clients are compiled in, under `Solo/Extensions`, and each is switched on only when the instance reports that extension (`GET /api/extensions`). An instance without Roadmaps shows no roadmap anywhere, exactly as its web app does; the catalog is read again whenever the live feed reconnects, since a restart is when it can change.

Core never asks about a particular extension. It names slots — sections of a task's detail, of a project's page and card, of the sidebar, of Your Tasks, of the Markdown editor, of admin settings — and `ExtensionSlots.swift` fills them.

| Extension | What is here |
|---|---|
| **MCP** | The MCP Access page (token, client snippets, tool list, usage) and a task's "Work for an LLM" switch. |
| **Roadmaps** | The timeline with press-and-hold to move and resize, milestones, sprints, the personal roadmap on Your Tasks, and the project, sidebar, card and task sections. |
| **Design Docs** | Design docs and their tree; notes that format as they are typed, edited live over the extension's Yjs protocol; wiki links, embeds, images and attachments; sketches, drawn with the web editor's rasterizer ported rule for rule so a stroke looks the same on every client; links to notes from task descriptions. |

Two parts of Design Docs follow the web editor closely enough to be worth knowing how:

- **Live formatting.** A note is one surface, as on the web: it reads as what its Markdown means, a tap in the text starts typing there, and putting the keyboard away ends it — there is no edit mode. Underneath it is the note's exact Markdown in a `UITextView`, which is what keeps it bound to the shared document character for character. `LiveMarkdown` parses the text with swift-markdown — the same CommonMark and GFM grammar the web editor's parser implements — and says what each run is; `NoteStyler` turns that into attributes and applies only the ones that changed. Syntax is folded away by styling it to nothing, never by removing it, and comes back when the caret touches its span (for a heading or a quote, its line; for a code block's fences, anywhere in the block). A custom layout manager draws what attributes can't: a quote's bar, a code block's background, and a bullet in place of a list marker's glyph. While the note isn't being typed in, a tap on a link follows it.
- **Pictures.** A line holding nothing but embeds is a row: one print, or several fanned into a stack with the web's own tilt for each path. Tapping opens them full screen with the system zoom transition, to swipe between, edit (a sketch), replace (an image), share, remove from the note or delete. The reader and the editor draw rows with the same view; in the editor it is hosted over a line made exactly as tall as the row.

- **Carets and pens.** Who is where travels over Yjs's awareness protocol, as on the web (`Awareness.swift`): a caret in a note, a pen and the stroke it is in the middle of on a sketch, each in the colour the instance gave that person. A caret is sent as the identity of the character it stands before, so that it holds its place while text is typed around it — and YSwift does not expose the document structure that turns that into an offset. `YjsStructure.swift` reads it out of the document's own update encoding and orders it with Yjs's rule for concurrent insertions. It is used for carets and nothing else, and it checks the text it reconstructs against the document's before placing one.
- **Moving a sketch's page** is the web editor's arithmetic (`SketchViewport.swift`, from `js/sketch/viewport.js`): a pan, a zoom and an angle, worked out from the raw touches so that what was under two fingers when they landed is under them still — they carry the page, zoom it and turn it, with no recognizer's threshold in between. A button puts it back, upright and fitted. Two fingers tapped together undo and three redo, as on the web; a finger draws unless Settings keeps drawing for the Pencil (or the Page menu says otherwise), and then one finger moves the page, as on the web. A trackpad scrolls, pinches and turns it too, and with Command or Option held its scroll zooms or turns.
- **Looking closer at a picture** is a scroll view's zoom (`ZoomablePicture.swift`), which keeps the place between the fingers where it is; a double tap zooms in on the place tapped.
- **Undo in a sketch** is Yjs's undo manager over the same parts of the document the web editor's watches, so it takes back a stroke, a change of paper or a cleared page, and never anybody else's work. Clearing plays the web's explosion, which the app fetches from the extension's content on the instance rather than carrying a copy.

### Extensions with no native half

Any other extension the instance runs still works here. Its manifest's `nav` entries are listed where the web's sidebar lists them — per project on the project's overview and in the sidebar, once on the Account tab for an instance-level page. Opening one draws the page natively when it is built from the kit, which is covered further down, and otherwise shows that page of the instance's web client inside the app's own bars (`Extensions/Web`).

The web client knows when it is hosted this way (`HostShell` in `Solo.Client`) and does three things differently:

- **It is only the page.** No app bar, no navigation, no footer, and no background of its own, so the app's shows through.
- **It signs in with the app's session.** The app hands it over for that page's lifetime; no provider is involved, whichever the instance uses. A session the API refuses is reported to the app, which checks for itself before signing anyone out.
- **Links to screens the app has go to the app.** The app tells the page which addresses it owns — a board, a project, a baked-in extension's page where that extension is installed — and a link to one opens the native screen. Anything else is the extension's and stays in the page; a link off the instance opens in the browser.

The page is given nothing of the device and can ask the app for nothing: it runs as web content, and what it may tell the app is that it is up, that its session was refused, and where it would like to go.

**What such an extension adds to core's own screens** — a section on a project, a badge on a card — is drawn natively when it is built from the kit (`Solo.Shared.Kit`, contract 1.17), and not shown otherwise. The app cannot run the extension's code, so the code runs where it always does: one page of the web client, kept in a web view nobody sees for the whole session (the client's "kit surface"; `Extensions/Web/KitSurface.swift` here). The app asks it to mount a place; the page runs that place's contributions with the parameters core gives them and describes the kit components they render as a tree of nodes; the app draws the nodes with its own controls. A press goes back as a click on the node it came from, so the extension's handler answers it, and whatever changes comes back as a new tree.

- **Places:** every one the web client has. A project's card, overview, sidebar group and settings; Your Tasks and its cards; a board's cards, column headers and settings; a task's detail and its menu; an extension's panel in Admin → Extensions; and the app bar, which here is a row above the Account tab's extension links.
- **A task's menu** is the one place that is not components: an extension answers with items, and the app shows them in a menu of its own beside Move and Delete in the task's detail. A card's press-and-hold menu does not have them — that would mean asking the page about every card.
- **Dialogs and snackbars.** A contribution may open one of the web client's dialogs — the task picker, a confirmation — which the app cannot draw. While one is up, the page itself is shown in a sheet, with nothing on it but the dialog; closing the sheet closes the dialog. What a contribution says in a snackbar is said as one of the app's own toasts.
- **Cards are for reading.** A card is one button, so what an extension puts on it is shown and cannot be pressed.
- **Writing and picking.** A kit text field is the app's own field. What is written goes to the extension when the writing pauses and when the field is left; while it is being written in the field is the person's, and once it is left it shows whatever the extension kept. A kit picker is a menu, and shows what the extension says is picked.
- **Only data crosses.** A node is a kind and some text; a press, something written and something picked are all that go back. `Href` on a row or a button opens the app's own screen where it has one, and the web client's page otherwise.
- **It runs only when it could matter.** The page starts the first time a place is shown on an instance that has such an extension, and closes for good if it reports that none of them contributes anything. The system ending its process restarts it, twice at most; a memory warning with nothing mounted closes it until it is next needed. It sits in the window at one point square, because out of a window the system holds its timers back.
- **A baked-in extension is never drawn this way.** The page is told which extensions the app has native halves for and leaves them out.

**An extension's own page** is asked of the same hidden page first (`PluginPageView`). The web client finds the page for the address, runs it, and says who draws it:

- **The app**, when everything the page renders is a kit component. It is drawn as a screen: text at a screen's size, a stack of nothing but rows as a glass panel, buttons as the app's own glass buttons, pull to refresh starting the page over.
- **The web client**, in a web view as before, when the page is marked `[WebPage]`, reads a query string, or is not an extension's page at all. The app is told at once and never tries to draw it.
- **The web client, after a moment**, when an unmarked page turns out to render something that is not the kit. The app switches to the web view when it is told, and does not switch back.

Nothing is waited for that could not answer: with no kit surface — it failed, the instance's web client is older than this, nothing on the instance has a page — the page is the web's straight away. A link from a page shown in a web view to a page the app could draw stays in the web view, drawn by the web client: only the app's own list of screens is handed to a hosted page.

About names the extensions this applies to. In a debug build the surface's timings and message sizes are written to `Documents/kit-surface.json`.

`Spikes/NativeKit` is a plugin for trying all of this, with pages and a kit contribution for each place, and `SOLO_OPEN=web:<path>` opens any address of the web client's this way. `SOLO_WEB_EXTENSIONS=mcp` lists a baked-in extension's manifest pages from the web as well.

## Where it differs from the web client

Better here:

- Opens and reads offline from the last loaded data.
- A task's detail sits beside its board on an iPad instead of over it.
- On a phone, a board's epics stay lined up across its columns, and a moved card is followed to where it lands.
- A card, a lane or a group that is dragged is picked up as itself, and the rows it is carried over part to make room for it; lanes fold while one is carried.
- Sketches take Apple Pencil pressure, and a double-tap on the Pencil swaps pen and eraser.
- Notes and folders are moved by dragging them about the tree, and a note dragged into a note being written becomes a link to it.
- Home Screen widgets, and haptics for picking a card up, putting it down and clearing a sketch.
- The theme is picked up on returning to the app, without a reload.

Not here — use the web client for these:

- **First-run setup** of a new instance. The wizard needs the identity provider's ID token, which the app never holds; it sends you to the instance's `/setup` page.
- **Design Docs:** importing and exporting an Obsidian vault as a zip; downloading a vault to edit offline; dragging pictures between rows; and, in sketches, text boxes — which are **not drawn at all**, so a sketch with text on it shows without it, though clearing the sketch removes them and Undo brings them back — and the selection, lasso and shape tools. Tables and inline pictures are shown as their Markdown rather than drawn.
- **Third-party extensions' screens**, for the reason above.
- **Push notifications.** There are none: nothing is registered with Apple and the instance has nothing to send through. The inbox is live while the app is running, and "Allow Notifications" only lets the app raise a banner itself, on the device, for a notification that arrives over the live connection in the moments after it is put in the background. Once the system suspends it, nothing arrives until it is opened again.

## Signing in

The app uses the same handshake as the desktop shell: it asks the instance to start a sign-in (`POST api/auth/desktop/start`), opens the instance's `/desktop-auth` page in an `ASWebAuthenticationSession`, and redeems the code that comes back for a session token, with PKCE throughout. The redirect lands on a loopback listener the app opens for the duration.

It first tries `POST api/auth/desktop/start-app`, the endpoint the Capacitor app's branch adds for returning to an app by its own URL scheme (`com.hunterahlquist.solo`), and falls back to the loopback flow when the instance doesn't have it — so it works against `main` today and will use the better path once that lands.
