import Observation
import SwiftUI
import WebKit

// A page of the instance's web client, shown inside the app. This is how an
// extension the app has no native half for still works here: its page runs
// where it always does, in the web client, and the app is the bars and the
// navigation around it.
//
// The web client knows it is hosted (HostShell, in Solo.Client) and leaves its
// own bars out. The app hands it its session and the list of addresses it has
// screens of its own for, and that is all it hands over: the page gets nothing
// of the device, and can ask the app for nothing. What comes back are three
// plain notices — it is up, its session was refused, it would like to go
// somewhere — and the app decides what each one is worth.

/// What every web view that shows the instance's client is set up with.
enum HostedWeb {
    static let channel = "soloHost"

    /// A web view configuration that tells the web client it is hosted and hands it the app's
    /// session. The session is put where the web client keeps one it has been handed, for the
    /// page's lifetime and no longer, and only on the instance that issued it.
    ///
    /// - Parameters:
    ///   - routes: Patterns for the addresses the app has screens of its own for.
    ///   - baked: Extensions the app draws itself, which a kit surface then leaves out.
    static func configuration(instance: URL, token: String, user: User, routes: [String], baked: [String],
                              listener: any WKScriptMessageHandler) -> WKWebViewConfiguration? {
        guard let profile = (try? JSONEncoder().encode(user)).flatMap({ String(data: $0, encoding: .utf8) }),
              let handed = (try? JSONSerialization.data(withJSONObject: [address(of: instance), token, profile, routes, baked]))
                .flatMap({ String(data: $0, encoding: .utf8) }) else { return nil }
        let configuration = WKWebViewConfiguration()
        let script = """
        (() => {
            const [origin, token, user, routes, baked] = \(handed);
            if (location.origin !== origin) return;
            sessionStorage.setItem('solo.session.token', token);
            sessionStorage.setItem('solo.session.user', user);
            window.soloHost = { routes, baked, post: message => window.webkit.messageHandlers.\(channel).postMessage(message) };
        })();
        """
        configuration.userContentController.addUserScript(
            WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController.add(listener, name: channel)
        return configuration
    }

    /// The scheme, host and port of an address, as a page's `location.origin` spells them.
    static func address(of url: URL) -> String {
        var text = "\(url.scheme ?? "https")://\(url.host ?? "")"
        if let port = url.port { text += ":\(port)" }
        return text
    }

    /// What a hosted page said, if it was the instance's own page in the main frame saying it.
    static func notice(_ message: WKScriptMessage, from instance: URL?) -> (type: String, body: [String: Any], data: Data)? {
        guard let instance, message.frameInfo.isMainFrame, let url = message.frameInfo.request.url,
              address(of: url) == address(of: instance),
              let text = message.body as? String, let data = text.data(using: .utf8),
              let body = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let type = body["type"] as? String else { return nil }
        return (type, body, data)
    }
}

@Observable
final class HostedPage: NSObject, WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate {
    private(set) var webView: WKWebView?
    private(set) var isReady = false
    private(set) var problem: String?
    /// The page's own title, once it has one.
    private(set) var title: String?

    /// The page asked to go to one of the app's own screens.
    @ObservationIgnored var onNavigate: (String) -> Void = { _ in }
    /// The web client says the session it was handed was refused.
    @ObservationIgnored var onSessionRefused: () -> Void = {}

    @ObservationIgnored private var origin: URL?
    @ObservationIgnored private var target: URL?
    @ObservationIgnored private var titleWatch: NSKeyValueObservation?
    @ObservationIgnored private var siteName = ""
    @ObservationIgnored private var restarts = 0

    func open(path: String, instance: URL, token: String, user: User, siteName: String, routes: [String]) {
        close()
        guard let url = URL(string: path, relativeTo: instance)?.absoluteURL,
              let configuration = HostedWeb.configuration(instance: instance, token: token, user: user, routes: routes,
                                                          baked: [], listener: self) else {
            problem = "This page's address couldn't be read."
            return
        }

        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = self
        view.uiDelegate = self
        // The app's background shows until the page has drawn, and around a page shorter than the screen.
        view.showsThrough()
        view.allowsBackForwardNavigationGestures = false
        #if DEBUG
        view.isInspectable = true
        #endif
        titleWatch = view.observe(\.title, options: [.new]) { [weak self] view, _ in
            Task { @MainActor in self?.took(title: view.title) }
        }

        webView = view
        origin = instance
        target = url
        self.siteName = siteName
        view.load(URLRequest(url: url))
    }

    func reload() {
        guard let webView, let target else { return }
        problem = nil
        isReady = false
        webView.load(URLRequest(url: target))
    }

    func close() {
        titleWatch = nil
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: HostedWeb.channel)
        webView?.stopLoading()
        webView = nil
        isReady = false
        problem = nil
        title = nil
    }

    private func isInstance(_ url: URL) -> Bool {
        guard let origin else { return false }
        return HostedWeb.address(of: url) == HostedWeb.address(of: origin)
    }

    /// The web client titles a page "Roadmap · Instance"; the bar wants the first part.
    private func took(title: String?) {
        guard var text = title?.trimmingCharacters(in: .whitespaces), !text.isEmpty else { return }
        // An extension's own title ends in "Solo" whatever the instance is called.
        for name in [siteName, "Solo"] where !name.isEmpty {
            for separator in [" · ", " - ", " | "] where text.hasSuffix(separator + name) {
                text.removeLast(separator.count + name.count)
            }
        }
        guard !text.isEmpty, text != siteName, text != "Solo" else { return }
        self.title = text
    }

    // MARK: What the page says

    func userContentController(_ controller: WKUserContentController, didReceive message: WKScriptMessage) {
        // Only the instance's own page, in the main frame, is listened to.
        guard let notice = HostedWeb.notice(message, from: origin) else { return }
        switch notice.type {
        case "ready":
            isReady = true
        case "sessionRefused":
            onSessionRefused()
        case "navigate":
            if let path = notice.body["path"] as? String, path.count < 2_000 { onNavigate(path) }
        default:
            break
        }
    }

    // MARK: Where the page may go

    func webView(_ webView: WKWebView, decidePolicyFor action: WKNavigationAction) async -> WKNavigationActionPolicy {
        guard let url = action.request.url else { return .cancel }
        // Frames inside the page are the instance's own business: its content policy decides those.
        if action.targetFrame?.isMainFrame == false { return .allow }
        if isInstance(url) { return .allow }
        // A link that leaves the instance opens in the browser; nothing else may take the page elsewhere.
        if action.navigationType == .linkActivated, ["http", "https", "mailto"].contains(url.scheme ?? "") {
            Platform.open(url)
        }
        return .cancel
    }

    /// A link asking for a new window: there are none here, so it goes where it would have gone.
    func webView(_ webView: WKWebView, createWebViewWith configuration: WKWebViewConfiguration,
                 for action: WKNavigationAction, windowFeatures: WKWindowFeatures) -> WKWebView? {
        guard let url = action.request.url else { return nil }
        if isInstance(url) {
            webView.load(URLRequest(url: url))
        } else if ["http", "https", "mailto"].contains(url.scheme ?? "") {
            Platform.open(url)
        }
        return nil
    }

    func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) {
        failed(error)
    }

    private func failed(_ error: Error) {
        // A navigation the policy above turned away reports itself as cancelled; that is not a failure.
        guard (error as NSError).code != NSURLErrorCancelled else { return }
        problem = error.localizedDescription
    }

    /// The system ended the page's process, which it does when memory is short. Once, the page
    /// is simply loaded again; a second time it says so and waits to be asked.
    func webViewWebContentProcessDidTerminate(_ webView: WKWebView) {
        restarts += 1
        if restarts <= 1 {
            reload()
        } else {
            isReady = false
            problem = "This page stopped. Try loading it again."
        }
    }
}

/// The page's web view, as it is.
private struct HostedWebView: PlatformViewRepresentable {
    var webView: WKWebView

    func makeView(context: Context) -> WKWebView { webView }
    func updateView(_ view: WKWebView, context: Context) {}
}

/// An extension's page. Drawn by the app when the page is built from the kit, and otherwise
/// the web client's own, inside the app's bars — which of the two is the web client's to say,
/// since only it can run the page.
struct PluginPageView: View {
    @Environment(Session.self) private var session
    var path: String
    var title: String

    @State private var lifetime = KitMountLifetime()
    /// Decided for the web, for good: the page said so, drew something the kit has not got, or
    /// nothing could be asked.
    @State private var web = false

    var body: some View {
        let kit = session.kit
        let box = kit.box(mount: lifetime.id)
        // Asking is what starts the kit surface, so the page is asked for before anything is known.
        let _ = web ? () : lifetime.keep(on: kit, .page, path: path)
        Group {
            if web || !kit.mayAnswer(.page) {
                WebPluginPage(path: path, title: title)
            } else if box.page == .app {
                KitPageView(mount: lifetime.id)
                    .themedBackground()
                    .navigationTitle(title.isEmpty ? ownerName(box.owner) : title)
                    .toolbarTitleDisplayMode(.inline)
            } else {
                ProgressView().controlSize(.large)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .themedBackground()
                    .navigationTitle(title)
                    .toolbarTitleDisplayMode(.inline)
            }
        }
        .onChange(of: box.page) { _, drawer in
            if drawer == .web { giveToWeb() }
        }
        // Up and asked, the surface answers at once. Silence past that is an instance whose web
        // client is older than the app and knows nothing of pages: the page is still its to show.
        .task(id: kit.state) {
            guard kit.state == .running else { return }
            try? await Task.sleep(for: .seconds(4))
            if !Task.isCancelled, box.page == nil { giveToWeb() }
        }
    }

    /// The page is running in the surface as well, until it is let go there.
    private func giveToWeb() {
        web = true
        lifetime.drop()
    }

    /// A page reached by a link has no title of its own here: it goes by its extension's name.
    private func ownerName(_ id: String?) -> String {
        session.extensions.installed.first { $0.id == id }?.name ?? ""
    }
}

/// An extension's page as the web client draws it, inside the app's own bars.
private struct WebPluginPage: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var path: String
    var title: String

    @State private var page = HostedPage()

    private var baked: HostedRoutes.Baked {
        .init(roadmap: session.extensions.hasRoadmap, designDocs: session.extensions.hasDesignDocs,
              mcp: session.extensions.hasMcp)
    }

    var body: some View {
        ZStack {
            if let webView = page.webView {
                HostedWebView(webView: webView)
                    // Under the bars, which are glass: the web view keeps its content clear of
                    // them at rest and lets it pass beneath them as it scrolls.
                    .ignoresSafeArea()
                    .opacity(page.isReady ? 1 : 0)
                    .animation(.easeOut(duration: 0.2), value: page.isReady)
            }
            if let problem = page.problem {
                ContentUnavailableView {
                    Label("This page couldn't load", systemImage: "wifi.exclamationmark")
                } description: {
                    Text(problem)
                } actions: {
                    Button("Try Again") { page.reload() }
                }
            } else if !page.isReady {
                ProgressView().controlSize(.large)
            }
        }
        .themedBackground()
        .navigationTitle(page.title ?? title)
        .toolbarTitleDisplayMode(.inline)
        .task(id: path) { load() }
        .onDisappear { page.close() }
    }

    private func load() {
        guard let token = session.api.token else { return }
        page.onNavigate = { path in
            // The page is taken at its word only as far as the app's own list of screens goes.
            switch HostedRoutes.destination(for: path, baked) {
            case .tab(let tab): navigator.popToRoot(tab)
            case .route(let route): navigator.open(route)
            case nil: break
            }
        }
        page.onSessionRefused = {
            // Asked of the instance rather than believed: if the app's session really is gone its
            // own request is refused too, and signing out follows from that as it always does.
            // If it is not, the page only held a stale copy, and gets a fresh one.
            Task {
                if (try? await session.api.currentUser()) != nil { load() }
            }
        }
        page.open(path: path, instance: session.api.baseURL, token: token, user: session.user,
                  siteName: session.siteName, routes: HostedRoutes.patterns(baked))
    }
}
