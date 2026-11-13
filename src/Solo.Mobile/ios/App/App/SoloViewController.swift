import UIKit
import SwiftUI
import WebKit
import Capacitor

/// Capacitor's bridge view controller, plus what no web API gives the page:
///
/// - The Apple Pencil double-tap. Native code dispatches a generic
///   `solo:device-action` event and the page decides what it means
///   (`ISoloDeviceActions` in Solo.Shared, `solo.deviceActions` in the client).
/// - Sign-in, which Google refuses inside a web view. The page can post one
///   fixed message, `request_sign-in`, and the sign-in sheet takes it from
///   there (`AppSignIn`).
/// - Its list of instances (`InstancesView`), opened by the fixed message
///   `request_instance-settings`. Opening another instance builds a new view
///   controller on it, since Capacitor reads the address once, at load.
/// - The local origin the page loads from (`LocalProxy`): a loopback server
///   forwarding everything to the instance, so the page lives on
///   http://localhost:<port>, the one App-Bound Domain (Info.plist), and gets
///   the service workers the client's offline mode needs.
/// - Status bar text that reads against the app bar, whose colour the client
///   publishes as the page's `theme-color`. The page itself draws behind the
///   status bar and the home indicator (`contentInset: 'never'`), so the app
///   paints nothing there.
///
/// Nothing native is handed to the page or the extensions it loads.
class SoloViewController: CAPBridgeViewController, UIPencilInteractionDelegate, WKScriptMessageHandler {
    private static let messageHandler = "soloApp"

    private var signIn: AppSignIn?
    /// The instance this controller opened; nil until one is added.
    private var instance: URL?
    /// The one built in with `SOLO_INSTANCE_URL`, which the debug shim's
    /// session belongs to.
    private var builtIn: URL?

    private var themeColorObservation: NSKeyValueObservation?
    private var foregroundObserver: NSObjectProtocol?
    private var backgroundObserver: NSObjectProtocol?

    /// The running proxy, kept across the view controllers that opening
    /// another instance builds, and replaced when the instance changes.
    private static var proxy: LocalProxy?
    /// Where the page actually loads from: the proxy's local origin.
    private var pageURL: URL?

    override func instanceDescriptor() -> InstanceDescriptor {
        let descriptor = super.instanceDescriptor()
        builtIn = descriptor.serverURL.flatMap(SavedInstances.normalize)
        instance = SavedInstances.resolve(builtIn: builtIn)
        // With none, Capacitor shows the bundled placeholder under the sheet.
        // The trailing slash keeps an instance hosted under a path resolving
        // its relative URLs there.
        if let instance {
            pageURL = Self.startProxy(for: instance)?.pageURL
        }
        descriptor.serverURL = pageURL?.absoluteString ?? instance.map { $0.absoluteString + "/" }
        return descriptor
    }

    // Not webViewConfiguration(for:): Capacitor swaps in its own content
    // controller after that returns, which drops any script added there.
    override func webView(with frame: CGRect, configuration: WKWebViewConfiguration) -> WKWebView {
        // The page is always on localhost, the one App-Bound Domain. Script
        // injection and message handlers then work there only, which is all
        // they need; a link elsewhere opens in Safari, as Capacitor does.
        configuration.limitsNavigationsToAppBoundDomains = true
        let controller = configuration.userContentController
        controller.add(WeakMessageHandler(self), name: Self.messageHandler)
        // What solo.desktop in the client looks for, in place of the desktop
        // app's window.soloDesktop.
        controller.addUserScript(WKUserScript(
            source: """
                window.soloApp = Object.freeze({
                    beginSignIn: () => window.webkit.messageHandlers.\(Self.messageHandler).postMessage('request_sign-in'),
                    openInstances: () => window.webkit.messageHandlers.\(Self.messageHandler).postMessage('request_instance-settings'),
                });
                """,
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true))
        #if DEBUG
        // The debug session belongs to the built-in instance, so only its
        // local origin gets it; another instance opens signed out.
        DevShim.install(in: controller, sessionFor: instance != nil && instance == builtIn ? pageURL : nil)
        #endif
        return super.webView(with: frame, configuration: configuration)
    }

    deinit {
        if let foregroundObserver { NotificationCenter.default.removeObserver(foregroundObserver) }
        if let backgroundObserver { NotificationCenter.default.removeObserver(backgroundObserver) }
    }

    override func capacitorDidLoad() {
        // iOS defuncts a suspended app's listener without telling it, so the
        // proxy closes it on the way into the background and opens a new one
        // on the way back (LocalProxy.suspend, resume).
        foregroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.willEnterForegroundNotification, object: nil, queue: .main
        ) { _ in Self.proxy?.resume() }
        backgroundObserver = NotificationCenter.default.addObserver(
            forName: UIApplication.didEnterBackgroundNotification, object: nil, queue: .main
        ) { _ in Self.proxy?.suspend() }


        let pencil = UIPencilInteraction()
        pencil.delegate = self
        webView?.addInteraction(pencil)

        themeColorObservation = webView?.observe(\.themeColor, options: [.initial, .new]) { [weak self] webView, _ in
            MainActor.assumeIsolated { self?.styleStatusBar(webView.themeColor) }
        }
    }

    override func viewDidAppear(_ animated: Bool) {
        super.viewDidAppear(animated)
        if instance == nil {
            showInstances()
        }
    }

    // Only from the instance's own page, never a frame inside it.
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame,
              let instance,
              Self.isOrigin(message.frameInfo.securityOrigin, of: pageURL ?? instance)
        else { return }

        switch message.body as? String {
        case "request_sign-in": beginSignIn(on: instance)
        case "request_instance-settings": showInstances()
        default: break
        }
    }

    // iPadOS 17.5 and later call this one and skip pencilInteractionDidTap.
    @available(iOS 17.5, *)
    func pencilInteraction(_ interaction: UIPencilInteraction, didReceiveTap tap: UIPencilInteraction.Tap) {
        stylusTapped()
    }

    func pencilInteractionDidTap(_ interaction: UIPencilInteraction) {
        stylusTapped()
    }

    // Follows the person's Pencil setting in iPadOS. Switching to the eraser or
    // to the previous tool is the tool swap; "Ignore" and the palettes aren't.
    private func stylusTapped() {
        switch UIPencilInteraction.preferredTapAction {
        case .switchEraser, .switchPrevious:
            dispatch(deviceAction: "action_stylus-tool-swap")
        default:
            break
        }
    }

    private func dispatch(deviceAction id: String) {
        webView?.evaluateJavaScript(
            "window.dispatchEvent(new CustomEvent('solo:device-action', { detail: { id: '\(id)' } }))")
    }

    // The same hand-off the desktop app makes: deliverSession stores the
    // session and tells the client; signInFailed shows the message on the
    // sign-in page. Closing the sheet says nothing. The async call throws when
    // the script's result is undefined, which these are, though they've run.
    private func beginSignIn(on instance: URL) {
        guard signIn == nil, let window = view.window else { return }
        let flow = AppSignIn(instance: instance, window: window)
        signIn = flow
        Task {
            defer { signIn = nil }
            do {
                if let session = try await flow.run() {
                    _ = try? await webView?.evaluateJavaScript("window.solo.desktop.deliverSession(\(session.token), \(session.user))")
                }
            } catch {
                let message = (try? JSONSerialization.data(withJSONObject: error.localizedDescription, options: .fragmentsAllowed))
                    .map { String(decoding: $0, as: UTF8.self) } ?? "null"
                _ = try? await webView?.evaluateJavaScript("window.solo.desktop.signInFailed(\(message))")
            }
        }
    }

    // Light or dark text against the app bar. Until the client sets a
    // theme-color (before it boots, and on the bundled placeholder) the system
    // default stands. The client sends the colour without alpha.
    private func styleStatusBar(_ themeColor: UIColor?) {
        guard let themeColor else {
            setStatusBarStyle(.default)
            return
        }

        var red: CGFloat = 0, green: CGFloat = 0, blue: CGFloat = 0
        themeColor.getRed(&red, green: &green, blue: &blue, alpha: nil)
        setStatusBarStyle(0.299 * red + 0.587 * green + 0.114 * blue < 0.5 ? .lightContent : .darkContent)
    }

    // Without an instance there's nothing behind the sheet, so it can't close.
    private func showInstances() {
        guard presentedViewController == nil else { return }
        let sheet = UIHostingController(rootView: InstancesView(
            current: instance,
            onOpen: { [weak self] chosen in self?.reopen(on: chosen) },
            onDone: instance == nil ? nil : { [weak self] in self?.dismiss(animated: true) }))
        sheet.isModalInPresentation = instance == nil
        present(sheet, animated: true)
    }

    private func reopen(on chosen: SavedInstance) {
        reopen(on: chosen.url)
    }

    private func reopen(on url: URL) {
        SavedInstances.current = url
        guard presentedViewController != nil else {
            view.window?.rootViewController = SoloViewController()
            return
        }
        dismiss(animated: true) { [weak self] in
            self?.view.window?.rootViewController = SoloViewController()
        }
    }

    /// The instance's proxy, started if it isn't running, on the port the
    /// instance last opened on (`InstancePorts`). Only when another app holds
    /// that port does it move to another of the instance's own, which has a
    /// sign-in and offline storage of its own.
    private static func startProxy(for instance: URL) -> LocalProxy? {
        if let proxy, proxy.target == instance { return proxy }
        proxy?.stop()
        proxy = InstancePorts.start(for: instance) { port in
            // A relaunch can find its own port held for a moment by the
            // process it replaces; moving on from it would sign the instance
            // out, so it gets a second try first.
            for attempt in 0..<2 {
                let candidate = LocalProxy(port: port, target: instance)
                do {
                    try candidate.start()
                    return candidate
                } catch {
                    NSLog("Solo: local port \(port) unavailable (\(error)) for \(instance.host ?? "").")
                    candidate.stop()
                    if attempt == 1 { throw error }
                    Thread.sleep(forTimeInterval: 0.3)
                }
            }
            throw CancellationError()
        }
        return proxy
    }

    private static func isOrigin(_ origin: WKSecurityOrigin, of url: URL) -> Bool {
        let defaultPort = url.scheme == "https" ? 443 : 80
        return origin.protocol == url.scheme
            && origin.host == url.host
            && (origin.port == 0 ? defaultPort : origin.port) == (url.port ?? defaultPort)
    }
}

/// The content controller holds its handlers strongly, and the web view it
/// belongs to is held by the view controller.
private final class WeakMessageHandler: NSObject, WKScriptMessageHandler {
    private weak var target: WKScriptMessageHandler?

    init(_ target: WKScriptMessageHandler) {
        self.target = target
    }

    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        target?.userContentController(userContentController, didReceive: message)
    }
}
