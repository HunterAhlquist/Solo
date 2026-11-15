import Observation
import SwiftUI
import WebKit

// What an extension adds to core's own screens, drawn by the app. The app has
// no native half for a third-party extension and cannot run its code, so the
// code runs where it always does: in the instance's web client, in one web
// view for the whole app that nobody sees (the web client's "kit surface").
// The app asks that page to mount a place — this project's card, that task's
// detail — and the page answers with what the extension's kit components
// would show there, as a tree of plain nodes. The app draws the nodes with its
// own controls, and a press goes back as a click on the node it came from.
//
// Only kit components (Solo.Shared.Kit) cross over, and only as data: a node
// is a kind and some text. The page is handed nothing of the device and can
// ask the app for nothing. An extension that draws with anything else simply
// has nothing here.
//
// An extension's own page is mounted the same way, and drawn as a screen of
// the app's when it is built from the kit (KitPageView, at the end). A page
// that says it is the web's, or that draws something the kit has not got, is
// shown from the web client instead (HostedPage).

/// One kit component, as the page describes it.
nonisolated struct KitNode: Decodable, Identifiable, Sendable, Equatable {
    var id: Int
    var kind: String
    var props: [String: String]
    var children: [KitNode]

    subscript(_ name: String) -> String? { props[name] }
    func flag(_ name: String) -> Bool { props[name] != nil }
}

/// One extension's contribution to a place, under the title core gives it there.
nonisolated struct KitSection: Decodable, Identifiable, Sendable, Equatable {
    var extensionId: String
    var title: String
    var order: Int
    var nodes: [KitNode]

    var id: String { "\(extensionId)|\(title)|\(order)" }

    private enum CodingKeys: String, CodingKey {
        case extensionId = "extension", title, order, nodes
    }
}

/// What the page sends when something it mounted has changed.
nonisolated struct KitUpdate: Decodable, Sendable {
    var units: [Unit]

    struct Unit: Decodable, Sendable {
        var mount: String
        /// Set when this is one item of a mount — one card of a board — and nothing else of it.
        var item: String?
        var sections: [KitSection]
        /// Set when the whole mount is being sent: every item it has now.
        var items: [Item]?
        /// For a mount that is a whole page: "native" when the app can draw it, "web" when it cannot.
        var page: String?
        /// Whose page it is.
        var owner: String?

        private enum CodingKeys: String, CodingKey {
            case mount, item, sections, items, page, owner = "extension"
        }
    }

    struct Item: Decodable, Sendable {
        var item: String
        var sections: [KitSection]
    }
}

/// The sections of one mounted place, or of one item in it. A view reads the one it draws,
/// so a card whose badge changes is the only card drawn again.
@Observable
final class KitBox {
    fileprivate(set) var sections: [KitSection] = []
    /// For a whole page: who draws it, once the web client has said. Nil until then.
    fileprivate(set) var page: KitPageDrawer?
    /// The extension a page belongs to.
    fileprivate(set) var owner: String?
}

/// Who draws an extension's page.
nonisolated enum KitPageDrawer: Sendable {
    /// The app, from the kit nodes the page is made of.
    case app
    /// The web client, in a web view: the page says so, or draws something the kit has not got.
    case web
}

/// The places the web client can mount, by the names it knows them by.
nonisolated enum KitSeam: String, Sendable {
    case projectCard, projectHome, projectNav, dashboard, taskDetail, taskCard
    /// The cards of Your Tasks: a board's card contributions, over the person's own work.
    case assignedCards
    case boardColumn, projectSettings, boardSettings, extensionSettings, appBar
    /// What extensions add to a task's menu.
    case taskMenu
    /// An extension's whole page, by its address.
    case page
}

@Observable
final class KitSurfaceHost: NSObject, WKScriptMessageHandler, WKNavigationDelegate {
    enum State: Equatable {
        /// Not running, and would start if something were mounted.
        case idle
        case starting
        case running
        /// The instance's extensions contribute nothing the app could draw: there is no point running.
        case unneeded
        case failed
    }

    private struct Mount {
        var seam: KitSeam
        var project: UUID?
        var board: UUID?
        var task: UUID?
        /// For a place that is one extension's alone: its settings panel.
        var plugin: String?
        /// For a whole page: its address, relative to the instance.
        var path: String?
    }

    private(set) var state = State.idle
    /// The web view, for whatever keeps it in a window: out of one, the system holds its page back.
    private(set) var webView: WKWebView?
    /// The places some extension contributes to, once the page has said.
    private(set) var seams: Set<String> = []
    /// True while the page has a dialog up — a contribution asked for a task to be picked, or
    /// for something to be confirmed — and so has to be shown.
    private(set) var presenting = false

    @ObservationIgnored weak var session: Session?
    @ObservationIgnored private var mounts: [String: Mount] = [:]
    @ObservationIgnored private var sent: Set<String> = []
    @ObservationIgnored private var boxes: [String: KitBox] = [:]
    @ObservationIgnored private var origin: URL?
    @ObservationIgnored private var restarts = 0
    #if DEBUG
    /// Simulator measurement: when things happened, and how much was sent. Written to the
    /// app's documents after every message, for a script to read.
    @ObservationIgnored private var figures: [String: Any] = [:]
    @ObservationIgnored private var startedAt: Date?
    @ObservationIgnored private var mountedAt: [String: Date] = [:]
    @ObservationIgnored private var bytes = 0
    @ObservationIgnored private var messages = 0

    private func record(_ name: String, _ value: Any) {
        figures[name] = value
        figures["state"] = "\(state)"
        figures["seams"] = seams.sorted()
        figures["mounts"] = mounts.count
        figures["messages"] = messages
        figures["bytes"] = bytes
        try? JSONSerialization.data(withJSONObject: figures)
            .write(to: URL.documentsDirectory.appending(path: "kit-surface.json"))
    }
    #endif
    /// Told that a task-detail contribution saved something to a task.
    @ObservationIgnored private var taskWatchers: [UUID: [String: () -> Void]] = [:]

    override init() {
        super.init()
        NotificationCenter.default.addObserver(self, selector: #selector(memoryIsShort),
                                               name: UIApplication.didReceiveMemoryWarningNotification, object: nil)
    }

    // MARK: Places

    /// The sections of a mount, or of one item of it. The same box every time it is asked for.
    func box(mount: String, item: String? = nil) -> KitBox {
        let key = item.map { mount + "\u{1}" + $0.lowercased() } ?? mount
        if let box = boxes[key] { return box }
        let box = KitBox()
        boxes[key] = box
        return box
    }

    func mount(_ id: String, _ seam: KitSeam, project: UUID? = nil, board: UUID? = nil, task: UUID? = nil,
               plugin: String? = nil, path: String? = nil) {
        mounts[id] = Mount(seam: seam, project: project, board: board, task: task, plugin: plugin, path: path)
        sent.remove(id)
        switch state {
        case .idle: start()
        case .running: send(id)
        case .starting, .unneeded, .failed: break
        }
    }

    func unmount(_ id: String) {
        guard mounts.removeValue(forKey: id) != nil else { return }
        if sent.remove(id) != nil { tell(["op": "unmount", "id": id]) }
        for key in boxes.keys where key == id || key.hasPrefix(id + "\u{1}") {
            boxes[key] = nil
        }
    }

    /// What the mount shows went stale — the task was edited, the board changed — and should be read again.
    func refresh(_ id: String) {
        guard sent.contains(id) else { return }
        tell(["op": "refresh", "id": id])
    }

    /// Whether asking for a place could still be answered. False once it is known that it never
    /// will be — the page couldn't run, or nothing on the instance contributes there — so that a
    /// screen waiting on an answer can stop waiting.
    func mayAnswer(_ seam: KitSeam) -> Bool {
        switch state {
        case .running: seams.contains(seam.rawValue)
        case .starting: true
        case .idle: worthStarting
        case .unneeded, .failed: false
        }
    }

    /// A node the app drew was pressed: the component's own handler answers it.
    func press(_ node: KitNode) {
        webView?.evaluateJavaScript("window.solo?.kitSurface?.press(\(node.id))")
    }

    /// Runs while a task's detail is open, when one of its contributions saved something to the task.
    func watch(task: UUID, as watcher: String, _ changed: (() -> Void)?) {
        taskWatchers[task, default: [:]][watcher] = changed
        if taskWatchers[task]?.isEmpty == true { taskWatchers[task] = nil }
    }

    // MARK: Running

    /// Whether this instance has anything that could contribute: an extension with a client half
    /// that the app has no native half for.
    private var worthStarting: Bool {
        guard let session else { return false }
        return !session.extensions.unsupported.isEmpty && session.api.token != nil
    }

    private func start() {
        guard state == .idle, worthStarting, let session, let token = session.api.token else { return }
        let instance = session.api.baseURL
        let baked = HostedRoutes.Baked(roadmap: session.extensions.hasRoadmap, designDocs: session.extensions.hasDesignDocs,
                                       mcp: session.extensions.hasMcp)
        guard let configuration = HostedWeb.configuration(
            instance: instance, token: token, user: session.user, routes: HostedRoutes.patterns(baked),
            baked: ExtensionCatalog.bakedIds.sorted(), listener: self) else {
            state = .failed
            return
        }
        // Wide enough that nothing in the page lays out as for a phone; nobody sees it either way.
        let view = WKWebView(frame: CGRect(x: 0, y: 0, width: 800, height: 600), configuration: configuration)
        view.navigationDelegate = self
        view.isUserInteractionEnabled = false
        view.accessibilityElementsHidden = true
        #if DEBUG
        view.isInspectable = true
        #endif
        origin = instance
        webView = view
        state = .starting
        #if DEBUG
        startedAt = Date()
        #endif
        view.load(URLRequest(url: instance.appending(path: "kit-surface")))
    }

    /// The person closed the sheet the page's dialog was shown in: the dialog is closed with it.
    func dismissDialog() {
        webView?.evaluateJavaScript("window.solo?.kitSurface?.dismiss()")
        presenting = false
    }

    /// Closes the web view. Mounts are remembered, so a later start picks them up again.
    func stop(as next: State = .idle) {
        presenting = false
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: HostedWeb.channel)
        webView?.stopLoading()
        webView?.removeFromSuperview()
        webView = nil
        sent = []
        for box in boxes.values where !box.sections.isEmpty {
            box.sections = []
        }
        state = next
    }

    /// The instance restarted, perhaps with different extensions: what was true of the old ones
    /// is forgotten, and whatever is on screen is mounted again.
    func restart() {
        stop()
        seams = []
        restarts = 0
        if !mounts.isEmpty { start() }
    }

    /// Signing out: everything goes, mounts included.
    func reset() {
        mounts = [:]
        boxes = [:]
        seams = []
        restarts = 0
        stop()
    }

    private func send(_ id: String) {
        guard state == .running, let mount = mounts[id], seams.contains(mount.seam.rawValue), !sent.contains(id) else { return }
        var request: [String: Any] = ["op": "mount", "id": id, "seam": mount.seam.rawValue]
        request["project"] = mount.project?.uuidString.lowercased()
        request["board"] = mount.board?.uuidString.lowercased()
        request["task"] = mount.task?.uuidString.lowercased()
        request["extension"] = mount.plugin
        request["path"] = mount.path
        sent.insert(id)
        #if DEBUG
        mountedAt[id] = Date()
        #endif
        tell(request)
    }

    private func tell(_ request: [String: Any]) {
        guard let webView, let data = try? JSONSerialization.data(withJSONObject: request),
              // The request as JSON text, itself made a JavaScript string by encoding it once more.
              let literal = try? JSONSerialization.data(withJSONObject: [String(decoding: data, as: UTF8.self)]),
              let argument = String(data: literal, encoding: .utf8) else { return }
        webView.evaluateJavaScript("window.solo?.kitSurface?.receive(\(argument)[0])")
    }

    /// With nothing mounted the page is costing memory for nothing; it comes back when asked.
    @objc private func memoryIsShort() {
        if mounts.isEmpty, state == .running || state == .starting {
            stop()
        }
    }

    // MARK: What the page says

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        guard let notice = HostedWeb.notice(message, from: origin) else { return }
        #if DEBUG
        messages += 1
        bytes += notice.data.count
        defer { record("last", notice.type) }
        if notice.type == "kitReady", let startedAt, figures["readyAfter"] == nil {
            figures["readyAfter"] = Date().timeIntervalSince(startedAt)
        }
        if notice.type == "kit", let update = try? JSONDecoder().decode(KitUpdate.self, from: notice.data) {
            for unit in update.units {
                let items = unit.items?.filter { !$0.sections.isEmpty }.count ?? 0
                // The first tree with something in it: how long a place waited for its contributions.
                if let asked = mountedAt[unit.mount], !unit.sections.isEmpty || items > 0 {
                    figures["first:" + unit.mount.prefix(13)] = ["after": Date().timeIntervalSince(asked), "items": items,
                                                                  "bytes": notice.data.count]
                    mountedAt[unit.mount] = nil
                }
            }
        }
        #endif
        switch notice.type {
        case "kitReady":
            seams = Set((notice.body["seams"] as? [String]) ?? [])
            if seams.isEmpty {
                // Nothing here could ever be drawn: the page is closed for good.
                stop(as: .unneeded)
            } else {
                state = .running
                restarts = 0
                mounts.keys.forEach(send)
            }
        case "kit":
            if let update = try? JSONDecoder().decode(KitUpdate.self, from: notice.data) { apply(update) }
        case "open", "navigate":
            if let path = notice.body["path"] as? String, path.count < 2_000 { session?.openKitLink(path) }
        case "taskChanged":
            if let text = notice.body["task"] as? String, let task = UUID(uuidString: text) {
                taskWatchers[task]?.values.forEach { $0() }
            }
        case "kitDialog":
            presenting = (notice.body["open"] as? Bool) == true
        case "kitToast":
            // What a contribution would have said in a snackbar, said the way the app says things.
            if let text = notice.body["text"] as? String, !text.isEmpty {
                let style: ToastCenter.Style = switch notice.body["tone"] as? String {
                case "error": .error
                case "warning": .warning
                case "success": .success
                default: .info
                }
                session?.toasts.show(String(text.prefix(300)), style)
            }
        case "sessionRefused":
            // Asked of the instance rather than believed, as for a hosted page.
            Task { [weak self] in
                guard let self, let session = self.session else { return }
                if (try? await session.api.currentUser()) != nil, !self.mounts.isEmpty {
                    self.stop()
                    self.start()
                }
            }
        default:
            break
        }
    }

    /// Takes what the page sent into the boxes views are reading. Units for a place that is no
    /// longer mounted are dropped: the page answered after the app stopped asking.
    func apply(_ update: KitUpdate) {
        for unit in update.units where mounts[unit.mount] != nil {
            if let item = unit.item {
                set(box(mount: unit.mount, item: item), unit.sections)
                continue
            }
            let whole = box(mount: unit.mount)
            set(whole, unit.sections)
            if let page = unit.page {
                // Once a page has been seen to need the web it is the web's: it does not flicker back.
                if whole.page != .web { whole.page = page == "native" ? .app : .web }
                if whole.owner != unit.owner { whole.owner = unit.owner }
            }
            guard let items = unit.items else { continue }
            var current = Set<String>()
            for item in items {
                current.insert(unit.mount + "\u{1}" + item.item.lowercased())
                set(box(mount: unit.mount, item: item.item), item.sections)
            }
            // Items the mount no longer has: a task that left the board.
            for (key, box) in boxes where key.hasPrefix(unit.mount + "\u{1}") && !current.contains(key) {
                set(box, [])
            }
        }
    }

    private func set(_ box: KitBox, _ sections: [KitSection]) {
        if box.sections != sections { box.sections = sections }
    }

    // MARK: The web view stays on the instance

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction) async -> WKNavigationActionPolicy {
        guard let url = action.request.url, let origin else { return .cancel }
        if action.targetFrame?.isMainFrame == false { return .allow }
        return HostedWeb.address(of: url) == HostedWeb.address(of: origin) ? .allow : .cancel
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        gaveUp(error)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        gaveUp(error)
    }

    private func gaveUp(_ error: Error) {
        guard (error as NSError).code != NSURLErrorCancelled else { return }
        // Contributions are extras: without the page there are none, and nothing else is affected.
        stop(as: .failed)
    }

    /// The system ended the page's process, which it does when memory is short. It is started
    /// again if anything is still mounted, but not over and over.
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        restarts += 1
        // Three times running without once coming up: it is not going to, and whatever was
        // waiting on it — a page, above all — is told so rather than left waiting.
        stop(as: restarts > 2 ? .failed : .idle)
        if !mounts.isEmpty, restarts <= 2 { start() }
    }
}

extension Session {
    /// Follows an address a contribution asked to open: a screen of the app's where it has one,
    /// and otherwise a page of the web client's, shown inside the app.
    func openKitLink(_ path: String) {
        var path = path
        if let url = URL(string: path), let scheme = url.scheme?.lowercased() {
            guard ["http", "https"].contains(scheme), HostedWeb.address(of: url) == HostedWeb.address(of: api.baseURL) else {
                // Somewhere off the instance is the browser's, as a link in a page of the web's would be.
                if ["http", "https", "mailto"].contains(scheme) { UIApplication.shared.open(url) }
                return
            }
            // The instance's own address written out in full: the same place as the path alone.
            path = url.path(percentEncoded: true) + (url.query(percentEncoded: true).map { "?" + $0 } ?? "")
        }
        while path.hasPrefix("/") { path.removeFirst() }
        let baked = HostedRoutes.Baked(roadmap: extensions.hasRoadmap, designDocs: extensions.hasDesignDocs, mcp: extensions.hasMcp)
        switch HostedRoutes.destination(for: path, baked) {
        case .tab(let tab): navigator.popToRoot(tab)
        case .route(let route): navigator.open(route)
        case nil: navigator.push(.pluginPage(path: path, title: ""))
        }
    }
}

/// Keeps the kit surface's web view in the window, where its page runs as a visible one does,
/// at a size nobody can see or touch.
struct KitSurfaceAnchor: UIViewRepresentable {
    var webView: WKWebView?
    /// While the page is being shown for a dialog it is in the sheet, not here.
    var lent = false

    func makeUIView(context: Context) -> UIView {
        let view = UIView()
        view.clipsToBounds = true
        view.isUserInteractionEnabled = false
        view.accessibilityElementsHidden = true
        return view
    }

    func updateUIView(_ view: UIView, context: Context) {
        guard let webView, !lent else {
            view.subviews.forEach { $0.removeFromSuperview() }
            return
        }
        if webView.superview !== view {
            view.subviews.forEach { $0.removeFromSuperview() }
            view.addSubview(webView)
        }
        webView.frame = CGRect(x: 0, y: 0, width: 800, height: 600)
        webView.alpha = 0.01
        webView.isUserInteractionEnabled = false
    }
}

/// The page itself, shown: for a dialog one of its contributions opened. Nothing else on the
/// page can be seen, so what the sheet shows is that dialog on the app's own background.
struct KitSurfaceDialog: UIViewRepresentable {
    var webView: WKWebView

    func makeUIView(context: Context) -> UIView { UIView() }

    func updateUIView(_ view: UIView, context: Context) {
        if webView.superview !== view {
            view.addSubview(webView)
        }
        webView.frame = view.bounds
        webView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        webView.alpha = 1
        webView.isOpaque = false
        webView.backgroundColor = .clear
        webView.scrollView.backgroundColor = .clear
        webView.isUserInteractionEnabled = true
    }
}

// MARK: - Drawing

/// Wraps its children onto further lines when one line won't hold them, as a row of badges
/// or buttons does on the web.
nonisolated struct KitFlow: Layout {
    var spacing: CGFloat
    var alignment: VerticalAlignment

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let lines = arrange(subviews, in: proposal.width ?? .infinity)
        return CGSize(width: lines.map(\.width).max() ?? 0,
                      height: lines.map(\.height).reduce(0, +) + spacing * CGFloat(max(lines.count - 1, 0)))
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var y = bounds.minY
        for line in arrange(subviews, in: bounds.width) {
            var x = bounds.minX
            for index in line.items {
                let size = subviews[index].sizeThatFits(.unspecified)
                let offset: CGFloat = switch alignment {
                case .top: 0
                case .bottom: line.height - size.height
                default: (line.height - size.height) / 2
                }
                subviews[index].place(at: CGPoint(x: x, y: y + offset), proposal: ProposedViewSize(size))
                x += size.width + spacing
            }
            y += line.height + spacing
        }
    }

    private struct Line {
        var items: [Int] = []
        var width: CGFloat = 0
        var height: CGFloat = 0
    }

    private func arrange(_ subviews: Subviews, in width: CGFloat) -> [Line] {
        var lines = [Line()]
        for index in subviews.indices {
            let size = subviews[index].sizeThatFits(.unspecified)
            let needed = lines[lines.count - 1].items.isEmpty ? size.width : lines[lines.count - 1].width + spacing + size.width
            if needed > width, !lines[lines.count - 1].items.isEmpty {
                lines.append(Line())
            }
            var line = lines[lines.count - 1]
            line.width = line.items.isEmpty ? size.width : line.width + spacing + size.width
            line.height = max(line.height, size.height)
            line.items.append(index)
            lines[lines.count - 1] = line
        }
        return lines.filter { !$0.items.isEmpty }
    }
}

extension EnvironmentValues {
    /// True for nodes drawn as a screen of their own, which have a screen's room: text at the
    /// size a screen's text is, and rows gathered into a panel as a settings screen's are.
    @Entry var kitPage = false
}

/// A kit node, drawn with the app's own controls.
struct KitNodeView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.kitPage) private var onPage
    var node: KitNode

    var body: some View {
        switch node.kind {
        case "stack": stack
        case "text": text
        case "icon": icon
        case "badge": badge
        case "button": button
        case "row": row
        case "progress": progress
        case "toggle": toggle
        case "menuItem": menuItem
        // A kind this build doesn't know — a newer kit than the app — is left out, not guessed at.
        default: EmptyView()
        }
    }

    private var children: some View {
        ForEach(node.children) { KitNodeView(node: $0) }
    }

    private func tone(_ name: String?, unmarked: Color) -> Color {
        switch name {
        case "Muted": theme.textSecondary
        case "Primary": theme.primary
        case "Success": theme.success
        case "Warning": theme.warning
        case "Danger": theme.error
        default: unmarked
        }
    }

    private var gap: CGFloat {
        switch node["spacing"] {
        case "None": 0
        case "Medium": 8
        case "Large": 16
        default: 4
        }
    }

    @ViewBuilder private var stack: some View {
        if node["direction"] == "Horizontal" {
            let across: VerticalAlignment = switch node["align"] {
            case "Center": .center
            case "End": .bottom
            default: .top
            }
            KitFlow(spacing: max(gap, onPage ? 8 : 6), alignment: across) { children }
        } else if onPage, !node.children.isEmpty, node.children.allSatisfy({ $0.kind == "row" }) {
            // Nothing but rows, on a screen: a panel of them, as the app's own lists of things are.
            VStack(spacing: 0) {
                ForEach(node.children) { child in
                    KitNodeView(node: child)
                    if child.id != node.children.last?.id { Divider() }
                }
            }
            .card(padding: 0)
            .padding(.vertical, gap)
        } else {
            // A stack that runs down is as wide as its place; its children sit across it as it says.
            let across: HorizontalAlignment = switch node["align"] {
            case "Center": .center
            case "End": .trailing
            default: .leading
            }
            VStack(alignment: across, spacing: gap) { children }
                .frame(maxWidth: .infinity, alignment: Alignment(horizontal: across, vertical: .center))
        }
    }

    private var text: some View {
        let font: Font = switch node["variant"] {
        case "Caption": onPage ? .footnote : .caption
        case "Label": .subheadline.weight(.semibold)
        case "Heading": onPage ? .title3.weight(.semibold) : .headline
        default: onPage ? .body : .subheadline
        }
        return Text(node["text"] ?? "")
            .font(font)
            .foregroundStyle(tone(node["tone"], unmarked: theme.textPrimary))
            .multilineTextAlignment(.leading)
    }

    @ViewBuilder private var icon: some View {
        let image = Image(systemName: SoloIcons.symbol(node["name"]))
            .font(.subheadline)
            .foregroundStyle(tone(node["tone"], unmarked: theme.textPrimary))
        if let label = node["label"] {
            image.accessibilityLabel(label)
        } else {
            image.accessibilityHidden(true)
        }
    }

    private var badge: some View {
        let color = tone(node["tone"], unmarked: theme.textSecondary)
        return HStack(spacing: 4) {
            if let icon = node["icon"] {
                Image(systemName: SoloIcons.symbol(icon)).accessibilityHidden(true)
            }
            Text(node["text"] ?? "")
        }
        .font(.caption.weight(.medium))
        .lineLimit(1)
        // A badge is a word or two: it is shown whole, and what is beside it gives way.
        .fixedSize()
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .foregroundStyle(color)
        .background(color.opacity(0.16), in: .capsule)
    }

    /// The press goes to the extension's handler, and then the app goes where it was sent.
    private func pressed() {
        if node.flag("press") { session.kit.press(node) }
        if let href = node["href"] { session.openKitLink(href) }
    }

    private var acts: Bool { !node.flag("disabled") && (node.flag("press") || node["href"] != nil) }

    @ViewBuilder private var button: some View {
        let label = Label {
            Text(node["text"] ?? "")
        } icon: {
            if let icon = node["icon"] { Image(systemName: SoloIcons.symbol(icon)) }
        }
        let role: ButtonRole? = node.flag("destructive") ? .destructive : nil
        // The app's own buttons: glass, and filled with the accent for the one that matters most.
        Group {
            switch node["variant"] {
            case "Primary":
                if node.flag("destructive") {
                    Button(role: role, action: pressed) { label.foregroundStyle(.white) }
                        .buttonStyle(.glassProminent)
                        .tint(theme.error)
                } else {
                    Button(role: role, action: pressed) { label.onAccent() }
                        .buttonStyle(.glassProminent)
                }
            case "Plain":
                Button(role: role, action: pressed) { label }
                    .buttonStyle(.borderless)
                    .tint(node.flag("destructive") ? theme.error : theme.textPrimary)
            default:
                if node.flag("destructive") {
                    Button(role: role, action: pressed) { label }
                        .buttonStyle(.glass)
                        .tint(theme.error)
                } else {
                    Button(role: role, action: pressed) { label }
                        .buttonStyle(.glass)
                }
            }
        }
        .disabled(!acts)
    }

    @ViewBuilder private var row: some View {
        let content = HStack(spacing: 8) {
            if let icon = node["icon"] {
                Image(systemName: SoloIcons.symbol(icon))
                    .foregroundStyle(theme.primary)
                    .frame(width: 22)
                    .accessibilityHidden(true)
            }
            VStack(alignment: .leading, spacing: 1) {
                Text(node["title"] ?? "").foregroundStyle(theme.textPrimary).lineLimit(1)
                if let subtitle = node["subtitle"] {
                    Text(subtitle).font(.caption).foregroundStyle(theme.textSecondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            children
        }
        .font(onPage ? .body : .subheadline)
        .padding(.vertical, onPage ? 11 : 6)
        .padding(.horizontal, onPage ? 14 : 0)
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(.rect)
        if acts {
            Button(action: pressed) { content }.buttonStyle(.plain)
        } else {
            content.opacity(node.flag("disabled") ? 0.5 : 1)
        }
    }

    private var progress: some View {
        ProgressView(value: min(max(Double(node["value"] ?? "") ?? 0, 0), 1))
            .tint(tone(node["tone"], unmarked: theme.primary))
            .accessibilityLabel(node["label"] ?? "Progress")
    }

    /// An item an extension adds to a task's menu, for use inside a menu.
    @ViewBuilder private var menuItem: some View {
        if node.flag("separator") { Divider() }
        Button(node["label"] ?? "", role: node.flag("danger") ? .destructive : nil) { session.kit.press(node) }
            .disabled(node.flag("disabled"))
    }

    private var toggle: some View {
        // The value is the extension's. The switch asks for the other one and shows it when it arrives.
        Toggle(node["label"] ?? "", isOn: Binding(get: { node.flag("value") }, set: { _ in session.kit.press(node) }))
            .font(onPage ? .body : .subheadline)
            .tint(theme.fill)
            .disabled(node.flag("disabled"))
    }
}

/// One mounted place, for as long as the view that asked for it is in the tree. A view with
/// nothing to show has nowhere to hang a task or an appearance on, so the mount is kept by an
/// object the view owns instead: made with the view, and gone with it.
final class KitMountLifetime {
    let id = UUID().uuidString
    private var asked: String?
    private var revision = 0
    private weak var host: KitSurfaceHost?

    /// Called as the view is drawn. Mounts the place the first time and when it changes, and
    /// has it read again when the revision moves; otherwise does nothing.
    func keep(on host: KitSurfaceHost, _ seam: KitSeam, project: UUID? = nil, board: UUID? = nil, task: UUID? = nil,
              plugin: String? = nil, path: String? = nil, revision: Int = 0) {
        let wanted = "\(seam.rawValue)|\(project?.uuidString ?? "")|\(board?.uuidString ?? "")|\(task?.uuidString ?? "")|\(plugin ?? "")|\(path ?? "")"
        let id = id
        if asked != wanted {
            asked = wanted
            self.revision = revision
            self.host = host
            // After this pass of drawing: mounting changes what views are watching.
            DispatchQueue.main.async { host.mount(id, seam, project: project, board: board, task: task, plugin: plugin, path: path) }
        } else if self.revision != revision {
            self.revision = revision
            DispatchQueue.main.async { host.refresh(id) }
        }
    }

    /// Lets the place go before the view does: it is being shown some other way.
    func drop() {
        guard asked != nil else { return }
        asked = nil
        let id = id
        DispatchQueue.main.async { [weak host] in host?.unmount(id) }
    }

    deinit {
        let id = id
        Task { @MainActor [weak host] in host?.unmount(id) }
    }
}

/// What extensions contribute to one place, each under the title core gives it there. The
/// place is mounted for as long as this is in the tree, and with nothing contributed it is no
/// views at all: it takes no room, and adds no gap, in whatever stack it is in.
struct KitContributions<Header: View>: View {
    @Environment(Session.self) private var session
    var seam: KitSeam
    var project: UUID?
    var board: UUID?
    var task: UUID?
    /// The one extension whose place this is, for a place that belongs to one.
    var plugin: String?
    /// Moved when what the place shows has gone stale, so the contributions are handed it afresh.
    var revision: Int = 0
    /// Room above each section, where the stack around it leaves none.
    var gap: CGFloat = 0
    /// How this place draws a section's title. Not called for a section that has none.
    @ViewBuilder var header: (String) -> Header

    @State private var lifetime = KitMountLifetime()

    var body: some View {
        let _ = lifetime.keep(on: session.kit, seam, project: project, board: board, task: task, plugin: plugin,
                              revision: revision)
        ForEach(session.kit.box(mount: lifetime.id).sections) { section in
            VStack(alignment: .leading, spacing: 6) {
                if !section.title.isEmpty { header(section.title) }
                ForEach(section.nodes) { KitNodeView(node: $0) }
            }
            // As wide as its place, from the leading edge, whatever the stack around it would do.
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.top, gap)
            // A part of someone else's screen: its buttons are the small ones.
            .controlSize(.small)
        }
    }
}

/// What extensions add to one item of a mount: a card of a board or of Your Tasks, or a column
/// of a board. The whole board is one mount, so the page is asked once however many there are.
struct KitItemContributions: View {
    @Environment(Session.self) private var session
    var mount: String
    /// A task's id, or a column's status.
    var item: String

    var body: some View {
        let sections = session.kit.box(mount: mount, item: item).sections
        if !sections.isEmpty {
            // A card is one button, so what sits on it is for reading: a press opens the task.
            HStack(spacing: 6) {
                ForEach(sections) { section in
                    ForEach(section.nodes) { KitNodeView(node: $0) }
                }
            }
            .allowsHitTesting(false)
            .controlSize(.small)
        }
    }
}

nonisolated enum BoardKitMount {
    static func id(for board: UUID) -> String { "board:" + board.uuidString.lowercased() }
    static func columns(for board: UUID) -> String { "columns:" + board.uuidString.lowercased() }
    /// The cards of Your Tasks.
    static let assigned = "assigned"
}

/// What extensions add to a task's menu, as a menu of its own beside the task's other actions.
/// No button at all when nothing is added.
struct KitTaskMenu: View {
    @Environment(Session.self) private var session
    var task: UUID
    var project: UUID?
    /// Moved when the task changes, since what a menu offers depends on the task.
    var revision: Int = 0

    @State private var lifetime = KitMountLifetime()

    var body: some View {
        let _ = lifetime.keep(on: session.kit, .taskMenu, project: project, task: task, revision: revision)
        let sections = session.kit.box(mount: lifetime.id).sections
        if !sections.isEmpty {
            Menu("More Actions", systemImage: "ellipsis") {
                ForEach(sections) { section in
                    Section {
                        ForEach(section.nodes) { KitNodeView(node: $0) }
                    }
                }
            }
            // Only the dots: the task's own actions are beside it and need the room for their words.
            .labelStyle(.iconOnly)
            .buttonStyle(.glass)
        }
    }
}

// MARK: - Pages

/// An extension's page, drawn by the app from the kit nodes it is made of. The page's own
/// code is running in the kit surface; this is what it says it shows, as a screen.
struct KitPageView: View {
    @Environment(Session.self) private var session
    /// The page's mount on the surface.
    var mount: String

    var body: some View {
        let sections = session.kit.box(mount: mount).sections
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                ForEach(sections) { section in
                    ForEach(section.nodes) { KitNodeView(node: $0) }
                }
            }
            .frame(maxWidth: 720, alignment: .leading)
            .frame(maxWidth: .infinity)
            .padding(.horizontal, 16)
            .padding(.vertical, 12)
            .environment(\.kitPage, true)
            .animation(.snappy, value: sections)
        }
        // Reading it again starts the page over, as reloading it on the web would.
        .refreshable { session.kit.refresh(mount) }
    }
}
