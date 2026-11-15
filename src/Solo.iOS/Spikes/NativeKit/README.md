# The test plugin

A client-only extension, `kit-spike`, that stands in for a third-party plugin: something the app has no native half for. It is how the app's support for such extensions is tried. Nothing here is released, built by CI or part of any solution.

It began as a spike — could a plugin's Razor component run in a web view nobody sees and be drawn by the app? — with three components of its own and a page to host them. The answer was yes; the components became the contract's kit (`Solo.Shared.Kit`, contract 1.17) and the page became the web client's kit surface, so the spike's own versions are gone. What it measured is kept at the end.

## What is in it

| File | What it is for |
|---|---|
| `PluginDemoPage` and the manifest's `nav` | A page built from the kit alone, with one of every component, which the app draws as a screen of its own. Its rows are the links such a page has to get right: a core screen by address and from a handler, the two pages below, and somewhere off the instance. |
| `PluginDemoMorePage` | A MudBlazor page marked `[WebPage]`: the app shows it from the web client without trying to draw it. Links to a core screen, back to the first page, and off the instance. |
| `AboutPage` | A MudBlazor page that is not marked, as every page written before the kit is: the app asks, is told it is not the kit, and shows it from the web. |
| `Contributions/DemoCardSection` | A project's card: its roadmaps, each a row with an `Href`. |
| `Contributions/DemoHomeSection` | A project's overview: buttons, a badge, progress and a switch over state the component holds. |
| `Contributions/DemoNavSection` | A project's sidebar group: two rows. |
| `Contributions/DemoDashboardSection` | Your Tasks. |
| `Contributions/DemoTaskDetail` | A task's detail: says which version of the task it was handed, and tells the host it saved something. |
| `Contributions/DemoCardBadge` | Every card, on a board and on Your Tasks: a badge. |
| `Contributions/DemoColumnBadge` | A board's column headers: how many sub-tasks the column's cards have. |
| `Contributions/DemoProjectSettings`, `DemoBoardSettings` | A section of a project's settings and of a board's: a switch, and a badge that follows it. |
| `Contributions/DemoExtensionSettings` | The extension's own panel in Admin → Extensions. |
| `Contributions/DemoAppBar` | The app bar: a row that counts its presses. |
| `Contributions/DemoTaskMenu` | A task's menu — a class, not a component. One item says something in a snackbar, one opens the host's task picker (a dialog), one is marked dangerous. |

Every contribution is built from the kit and nothing else, which is what lets the app draw it. The menu is the exception: its items are data already.

## Running it

```bash
build/pack-contract.sh
src/Solo.iOS/Spikes/NativeKit/pack.sh <the instance's extensions directory>
```

Restart the instance. The Roadmap extension has to be installed too, since the card section reads its endpoint. The instance has to be running this branch's web client.

## What the app's kit surface measured (iPad simulator, a Debug web client, the instance on the same Mac)

| Question | Answer |
|---|---|
| How long until the page is up? | 2.1–2.4 s from the app starting it. |
| How long until a place shows its contributions, once the page is up? | Under 0.1 s for a project's card or a section. |
| A board of 580 cards, a badge each? | 0.31–0.33 s from asking, as one message of 118 KB. |
| One of those cards changing? | One message of 291 bytes, for that card. |
| Memory with all of that mounted? | About 192 MB for the web view's content process. The app itself was at 150 MB with the board open. |

## What the spike measured before that (iPhone simulator)

| Question | Answer |
|---|---|
| Is a hidden web view held back? | Not if it is in a window: hidden, or one point square, the page counts itself visible and its timers run on time. Out of any window its timers drop to one a second and a live change took 3–4 s to show. |
| Does it survive the app going to the background? | For 30 s, yes: the page stopped, and carried on in the same process with its state. |
| On an iPhone Air over the local network? | It worked; the page took 12 s to come up, against 13 s on the simulator at the same address and 1.5 s at `localhost`. A plain-HTTP address on the network is not a secure context and the web client starts about eleven seconds slower without one. An instance behind HTTPS should not pay that, which was not measured. |

## Still not known

- Memory on a real iPhone or iPad, a fair start-up time there (over HTTPS), and how often the system ends the page's process when memory is short.
- The cost with a Release web client, over a real network.
- Scrolling a board while its cards' contributions are changing.
- What App Review makes of it.
