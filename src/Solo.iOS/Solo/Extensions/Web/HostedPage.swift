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
              let profile = (try? JSONEncoder().encode(user)).flatMap({ String(data: $0, encoding: .utf8) }),
              let handed = (try? JSONSerialization.data(withJSONObject: [Self.address(of: instance), token, profile, routes]))
                .flatMap({ String(data: $0, encoding: .utf8) }) else {
            problem = "This page's address couldn't be read."
            return
        }

        let configuration = WKWebViewConfiguration()
        // The session is put where the web client keeps one it has been handed, for this page's
        // lifetime and no longer, and only on the instance that issued it.
        let script = """
        (() => {
            const [origin, token, user, routes] = \(handed);
            if (location.origin !== origin) return;
            sessionStorage.setItem('solo.session.token', token);
            sessionStorage.setItem('solo.session.user', user);
            window.soloHost = { routes, post: message => window.webkit.messageHandlers.soloHost.postMessage(message) };
        })();
        """
        configuration.userContentController.addUserScript(
            WKUserScript(source: script, injectionTime: .atDocumentStart, forMainFrameOnly: true))
        configuration.userContentController.add(self, name: "soloHost")

        let view = WKWebView(frame: .zero, configuration: configuration)
        view.navigationDelegate = self
        view.uiDelegate = self
        // The app's background shows until the page has drawn, and around a page shorter than the screen.
        view.isOpaque = false
        view.backgroundColor = .clear
        view.scrollView.backgroundColor = .clear
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
        webView?.configuration.userContentController.removeScriptMessageHandler(forName: "soloHost")
        webView?.stopLoading()
        webView = nil
        isReady = false
        problem = nil
        title = nil
    }

    /// The scheme, host and port of an address, as a page's `location.origin` spells them.
    private static func address(of url: URL) -> String {
        var text = "\(url.scheme ?? "https")://\(url.host ?? "")"
        if let port = url.port { text += ":\(port)" }
        return text
    }

    private func isInstance(_ url: URL) -> Bool {
        guard let origin else { return false }
        return Self.address(of: url) == Self.address(of: origin)
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
        guard message.frameInfo.isMainFrame, let url = message.frameInfo.request.url, isInstance(url),
              let text = message.body as? String, let data = text.data(using: .utf8),
              let notice = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              let type = notice["type"] as? String else { return }
        switch type {
        case "ready":
            isReady = true
        case "sessionRefused":
            onSessionRefused()
        case "navigate":
            if let path = notice["path"] as? String, path.count < 2_000 { onNavigate(path) }
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
            await UIApplication.shared.open(url)
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
            UIApplication.shared.open(url)
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
private struct HostedWebView: UIViewRepresentable {
    var webView: WKWebView

    func makeUIView(context: Context) -> WKWebView { webView }
    func updateUIView(_ view: WKWebView, context: Context) {}
}

/// An extension's page: the web client's, inside the app's own bars.
struct PluginPageView: View {
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
