import Observation
import SwiftUI
import WebKit

// A spike, not a feature: whether a plugin's Razor component can run in a web
// view nobody sees and be drawn by the app instead. The instance needs the
// spike extension (Spikes/NativeKit) installed; its host page runs a card
// contribution written with three "kit" components, a script hands the app
// what they would show as a tree of nodes, and a tap here goes back as a
// click there. Everything on this screen is for measuring that.

/// One kit node, as the page describes it.
nonisolated struct KitNode: Decodable, Identifiable, Sendable, Equatable {
    var id: Int
    var kind: String
    var props: [String: String]
    var children: [KitNode]
}

/// The kit nodes of one contribution: here, one project's card section.
nonisolated struct KitRoot: Decodable, Identifiable, Sendable, Equatable {
    var seam: String
    var project: String
    var alias: String?
    var name: String?
    var nodes: [KitNode]

    var id: String { seam + ":" + project }
}

@Observable
final class KitHost: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
    /// Where the web view is kept, which is what decides how the system treats its page.
    enum Placement: String, CaseIterable, Identifiable {
        case detached = "Not in a window"
        case hidden = "In the window, hidden"
        case tiny = "In the window, one point"
        case shown = "Shown"

        var id: String { rawValue }
    }

    private(set) var webView: WKWebView?
    private(set) var roots: [KitRoot] = []
    private(set) var status = "Not started"
    private(set) var lastOpen: String?
    private(set) var lines: [String] = []

    // Timings, in seconds from asking the web view to load.
    private(set) var readyAfter: TimeInterval?
    private(set) var firstRowsAfter: TimeInterval?
    /// The page's own count of the time from navigation to the host page being up, in milliseconds.
    private(set) var pageReadyAfter: Int?
    private(set) var trees = 0

    // The page's pulse: four a second when nothing holds it back.
    private(set) var beats = 0
    private(set) var beatsExpected = 0
    private(set) var longestGap: TimeInterval = 0
    private(set) var visibility = "—"
    @ObservationIgnored private var lastBeat: Date?
    @ObservationIgnored private var firstBeat: Date?
    @ObservationIgnored private var started: Date?

    /// Starts over with a new web view. `cached` keeps WebKit's cache between runs, as a second launch would find it.
    func start(instance: URL, token: String, cached: Bool) async {
        stop()
        status = "Asking who is signed in…"
        // The web client keeps the profile it was signed in with beside its token.
        var request = URLRequest(url: instance.appending(path: "api/users/me"))
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        guard let (profile, _) = try? await URLSession.shared.data(for: request),
              let user = String(data: profile, encoding: .utf8),
              let literals = try? JSONSerialization.data(withJSONObject: [token, user]),
              let values = String(data: literals, encoding: .utf8) else {
            status = "Couldn't reach the instance."
            return
        }

        let configuration = WKWebViewConfiguration()
        configuration.websiteDataStore = cached ? .default() : .nonPersistent()
        // The web client signs in the way it does inside the desktop shell: with a session token it
        // is handed, here for this page's lifetime only (sessionStorage), never written to disk.
        let script = """
        (() => {
            const [token, user] = \(values);
            window.soloDesktop = {};
            sessionStorage.setItem('solo.session.token', token);
            sessionStorage.setItem('solo.session.user', user);
            const say = text => window.webkit.messageHandlers.soloKit.postMessage(JSON.stringify({ type: 'log', text }));
            window.addEventListener('error', event => say('error: ' + event.message));
            window.addEventListener('unhandledrejection', event => say('rejected: ' + event.reason));
        })();
        """
        configuration.userContentController.addUserScript(
            WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController.add(self, name: "soloKit")

        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 390, height: 700), configuration: configuration)
        view.navigationDelegate = self
        #if DEBUG
        view.isInspectable = true
        #endif
        webView = view
        origin = instance
        started = Date()
        status = "Loading the web client…"
        view.load(URLRequest(url: instance.appending(path: "kit-spike/host")))
    }

    func stop() {
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: "soloKit")
        webView?.stopLoading()
        webView?.removeFromSuperview()
        webView = nil
        roots = []
        lines = []
        lastOpen = nil
        readyAfter = nil
        firstRowsAfter = nil
        pageReadyAfter = nil
        trees = 0
        resetPulse()
    }

    /// Starts counting the pulse afresh, for comparing one placement with another.
    func resetPulse() {
        beats = 0
        beatsExpected = 0
        longestGap = 0
        lastBeat = nil
        firstBeat = nil
    }

    /// A tap on a node drawn here, sent back as a click on the element it came from.
    func tap(_ node: KitNode) {
        webView?.evaluateJavaScript("window.soloKit?.tap(\(node.id))")
    }

    @ObservationIgnored private var origin: URL?

    // MARK: From the page

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let text = message.body as? String, let data = text.data(using: .utf8),
              let object = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let type = object["type"] as? String else { return }
        let elapsed = started.map { Date().timeIntervalSince($0) }
        switch type {
        case "ready":
            readyAfter = elapsed
            pageReadyAfter = object["sinceNavigation"] as? Int
            status = "Running"
        case "tree":
            trees += 1
            if let decoded = try? JSONDecoder().decode(Tree.self, from: data) {
                roots = decoded.roots
                // Rows, and not the "Loading…" that comes first, are what someone is waiting for.
                if firstRowsAfter == nil, decoded.roots.contains(where: { $0.nodes.contains { $0.kind == "row" } }) {
                    firstRowsAfter = elapsed
                }
            }
        case "beat":
            let now = Date()
            if let lastBeat { longestGap = max(longestGap, now.timeIntervalSince(lastBeat)) }
            if firstBeat == nil { firstBeat = now }
            lastBeat = now
            beats += 1
            beatsExpected = Int(now.timeIntervalSince(firstBeat ?? now) / 0.25) + 1
            visibility = object["visibility"] as? String ?? "—"
        case "open":
            lastOpen = object["route"] as? String
        case "log":
            note(object["text"] as? String ?? "")
        default:
            break
        }
    }

    private struct Tree: Decodable {
        var roots: [KitRoot]
    }

    private func note(_ line: String) {
        lines.append(line)
        if lines.count > 30 { lines.removeFirst(lines.count - 30) }
    }

    // MARK: The web view stays on the instance

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction) async -> WKNavigationActionPolicy {
        guard let url = action.request.url, let origin else { return .cancel }
        // The session was put into this page; it goes nowhere but the instance that issued it.
        return url.scheme == origin.scheme && url.host == origin.host && url.port == origin.port ? .allow : .cancel
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        status = "Failed: \(error.localizedDescription)"
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        status = "Failed: \(error.localizedDescription)"
    }

    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        status = "The system ended the web view's process."
        note("web content process terminated")
    }
}

/// Holds the host's web view wherever the placement says.
struct KitWebViewSlot: UIViewRepresentable {
    var host: KitHost
    var webView: WKWebView?
    var placement: KitHost.Placement

    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.clipsToBounds = true
        return view
    }

    func updateUIView(_ view: UIView, context: Context) {
        guard let webView else { return }
        switch placement {
        case .detached:
            webView.removeFromSuperview()
        case .hidden:
            if webView.superview !== view { view.addSubview(webView) }
            webView.isHidden = true
        case .tiny:
            if webView.superview !== view { view.addSubview(webView) }
            webView.isHidden = false
            webView.alpha = 0.01
            webView.frame = CGRect(x: 0, y: 0, width: 1, height: 1)
        case .shown:
            if webView.superview !== view { view.addSubview(webView) }
            webView.isHidden = false
            webView.alpha = 1
            webView.frame = view.bounds
            webView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        }
    }
}

/// A kit node, drawn with the app's own controls.
struct KitNodeView: View {
    @Environment(\.theme) private var theme
    var node: KitNode
    var host: KitHost

    private static let symbols = ["Timeline": "chart.bar.xaxis", "Flag": "flag"]

    var body: some View {
        switch node.kind {
        case "row":
            Button {
                host.tap(node)
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: Self.symbols[node.props["icon"] ?? ""] ?? "puzzlepiece.extension")
                        .foregroundStyle(theme.primary)
                    Text(node.props["title"] ?? "").foregroundStyle(theme.textPrimary)
                    Spacer(minLength: 8)
                    ForEach(node.children) { KitNodeView(node: $0, host: host) }
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .disabled(node.props["tappable"] == nil)
        case "badge":
            Text(node.props["text"] ?? "")
                .font(.caption.weight(.medium))
                .padding(.horizontal, 8)
                .padding(.vertical, 3)
                .foregroundStyle(node.props["tone"] == "primary" ? theme.primary : theme.textSecondary)
                .background((node.props["tone"] == "primary" ? theme.primary : theme.textSecondary).opacity(0.16), in: .capsule)
        case "text":
            Text(node.props["text"] ?? "")
                .font(.caption)
                .foregroundStyle(node.props["tone"] == "error" ? Color.red : theme.textSecondary)
        default:
            // A node this build doesn't know is left out rather than guessed at.
            EmptyView()
        }
    }
}

struct KitSpikeView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @State private var host = KitHost()
    @State private var placement: KitHost.Placement = .detached

    var body: some View {
        List {
            Section {
                LabeledContent("State", value: host.status)
                LabeledContent("Host page up", value: seconds(host.readyAfter))
                LabeledContent("First rows", value: seconds(host.firstRowsAfter))
                LabeledContent("Page's own count", value: host.pageReadyAfter.map { "\($0) ms" } ?? "—")
                LabeledContent("Trees received", value: "\(host.trees)")
            } header: {
                Text("Hidden web view")
            } footer: {
                Text("Times are from asking the web view to load. The instance needs the spike extension installed.")
            }
            .themedRow()

            Section {
                Picker("Web view", selection: $placement) {
                    ForEach(KitHost.Placement.allCases) { Text($0.rawValue).tag($0) }
                }
                LabeledContent("Pulses heard", value: "\(host.beats) of \(host.beatsExpected)")
                LabeledContent("Longest gap", value: String(format: "%.2f s", host.longestGap))
                LabeledContent("Page says it is", value: host.visibility)
                Button("Count Again") { host.resetPulse() }
            } header: {
                Text("How the system treats it")
            } footer: {
                Text("The page pulses four times a second. Fewer heard, or long gaps, mean it is being held back.")
            }
            .themedRow()

            ForEach(host.roots) { root in
                Section {
                    ForEach(root.nodes) { KitNodeView(node: $0, host: host) }
                } header: {
                    Text("\(root.name ?? root.project) · Roadmaps (kit)")
                }
                .themedRow()
            }

            if let route = host.lastOpen {
                Section("The component asked to open") {
                    Text(route).font(.footnote.monospaced())
                }
                .themedRow()
            }

            Section {
                Button("Load Again") { load(cached: true) }
                Button("Load Again Without the Cache") { load(cached: false) }
            }
            .themedRow()

            if !host.lines.isEmpty {
                Section("Page errors") {
                    ForEach(Array(host.lines.enumerated()), id: \.offset) { Text($0.element).font(.caption.monospaced()) }
                }
                .themedRow()
            }

        }
        // Outside the list, whose rows exist only while they are on screen: the placement has to
        // be what it says whatever is scrolled into view.
        .safeAreaInset(edge: .bottom, spacing: 0) {
            KitWebViewSlot(host: host, webView: host.webView, placement: placement)
                .frame(height: placement == .shown ? 300 : 1)
        }
        .scrollContentBackground(.hidden)
        .themedBackground()
        .navigationTitle("Plugin Kit Spike")
        .task {
            #if DEBUG
            // Simulator measurement: the placement and the cache are chosen at launch, and the
            // numbers on this screen are written out once a second for a script to read.
            let environment = ProcessInfo.processInfo.environment
            if let wanted = environment["SOLO_KIT_PLACEMENT"],
               let chosen = KitHost.Placement.allCases.first(where: { "\($0)" == wanted }) {
                placement = chosen
            }
            if host.webView == nil { load(cached: environment["SOLO_KIT_CACHED"] != "0") }
            let file = URL.documentsDirectory.appending(path: "kit-spike.json")
            while !Task.isCancelled {
                let snapshot: [String: Any] = [
                    "status": host.status, "placement": "\(placement)", "ready": host.readyAfter ?? -1,
                    "rows": host.firstRowsAfter ?? -1, "page": host.pageReadyAfter ?? -1, "trees": host.trees,
                    "beats": host.beats, "expected": host.beatsExpected, "gap": host.longestGap,
                    "visibility": host.visibility, "open": host.lastOpen ?? "", "lines": host.lines,
                    "titles": host.roots.flatMap { $0.nodes.compactMap { $0.props["title"] } },
                ]
                try? JSONSerialization.data(withJSONObject: snapshot).write(to: file)
                try? await Task.sleep(for: .seconds(1))
            }
            #else
            if host.webView == nil { load(cached: true) }
            #endif
        }
        .onDisappear { host.stop() }
    }

    private func load(cached: Bool) {
        guard let token = session.api.token else { return }
        Task { await host.start(instance: session.api.baseURL, token: token, cached: cached) }
    }

    private func seconds(_ value: TimeInterval?) -> String {
        value.map { String(format: "%.2f s", $0) } ?? "—"
    }
}
