# Plugin kit spike

A question, not a feature: can a third-party plugin's Razor component run in a web view nobody sees, and be drawn by the native app with its own controls? Nothing here is released, built by CI or part of any solution.

## What is in it

| Piece | Where | What it does |
|---|---|---|
| A client-only extension, `kit-spike` | this folder | Three "kit" components (`KitRow`, `KitBadge`, `KitText`), the Roadmap's project-card section rewritten with them (`RoadmapKitCard.razor`), and a host page at `/kit-spike/host`. |
| The bridge | `content/kit.js` | Serializes the kit nodes on the host page as a tree and posts it to the app; turns a tap in the app back into a click on the element it came from. |
| The app's side | `Solo/Extensions/KitSpike/KitHost.swift` | A web view signed in with the app's session, the tree drawn with SwiftUI, and a screen of measurements under Account → Plugin Kit Spike. |

A kit component draws MudBlazor markup on the web and a `<solo-kit>` element on a kit surface, so `RoadmapKitCard` is one source for both: on the web it is an ordinary `[ProjectCardSection]` ("Roadmaps (kit)" on each project's card), and in the app the same running component is what the native rows come from. Its data, its live topic and its click handler are ordinary client code.

It also carries three plain pages (`PluginDemoPage`, `PluginDemoMorePage`, `AboutPage`) and the manifest entries that list them. They have nothing to do with the kit: they are an extension with no native half, for trying how the app shows one (see the app's README, "Extensions with no native half").

## Running it

```bash
build/pack-contract.sh
src/Solo.iOS/Spikes/NativeKit/pack.sh <the instance's extensions directory>
```

Restart the instance, then open Account → Plugin Kit Spike in the app. The Roadmap extension has to be installed too, since the card reads its endpoint.

## What it found (iPhone simulator, a Debug build of the web client, the instance on the same Mac)

| Question | Answer |
|---|---|
| Does it work at all? | Yes. Rows appear natively; a tap runs the component's C# handler, the component re-renders, and the new tree arrives with the change in it. A request to open a route comes through as a message. |
| How long until rows show? | About 1.7 s from asking the web view to load, with WebKit's cache warm (1.5 s to the host page being up). 1.8–2.1 s without the cache — but the instance was local, so that leaves out the download. |
| How much is downloaded cold? | About 14 MB compressed in 225 files for this Debug client. A Release client is trimmed and was not measured. |
| How much memory? | About 190–200 MB for the web view's content process, plus about 12 MB and 11 MB for WebKit's GPU and networking processes. The app itself was at 96 MB. |
| Is a hidden web view held back? | Not if it is in a window: hidden, or one point square, the page counts itself visible and its timers run on time (142 of 143 pulses). Out of any window its timers drop to one a second and a live change took 3–4 s to show, but it kept running for the 45 s it was watched. |
| Does it survive the app going to the background? | For 30 s, yes: the page stopped, and carried on in the same process with its state when the app came back. |
| Do live changes arrive? | Yes. A roadmap created through the API showed up in the tree within the 3 s that was waited, and left it again when deleted. |

## On an iPhone Air, over the local network

The same instance, reached at the Mac's address over plain HTTP, with a Debug build of the app handed a session at launch.

| Question | Answer |
|---|---|
| Does it work on the device? | Yes: rows drawn, the page counting itself visible in a one-point web view, 123 of 128 pulses heard. |
| How long until rows show? | 14.8 s (12.0 s to the host page being up). The simulator takes 13 s against the same address and 1.5 s against `localhost`, so nearly all of it belongs to the address and not to the phone: a plain-HTTP address on the network is not a secure context, and the web client starts about eleven seconds slower without one. Why exactly was not looked into. An instance behind HTTPS should not pay it, which was not measured. |

## What it did not find out

- Memory on a real iPhone or iPad, a fair boot time there (over HTTPS), and whether the system ends the web view's process when memory is short (the host notices if it does; it does not yet reload).
- The cost with a Release web client, over a real network.
- Many contributions at once — a board of cards with a badge each — which is the case a single hidden web view is meant to make affordable.
- Dialogs, the task picker and anything else a hidden component might want to put on screen.
- What App Review makes of it.

## Shortcuts taken

- The web client is told it is inside the desktop shell (`window.soloDesktop`) so that it takes a session token, which the app puts in `sessionStorage`. A real design needs its own way of handing the session over.
- The host page names its one component. The host would find contributions by their attributes.
- The kit lives in the plugin. It belongs in `Solo.Shared`.
