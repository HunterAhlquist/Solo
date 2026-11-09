import UIKit
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
///
/// Nothing native is handed to the page or the extensions it loads.
class SoloViewController: CAPBridgeViewController, UIPencilInteractionDelegate, WKScriptMessageHandler {
    private static let messageHandler = "soloApp"

    private var signIn: AppSignIn?

    // Not webViewConfiguration(for:): Capacitor swaps in its own content
    // controller after that returns, which drops any script added there.
    override func webView(with frame: CGRect, configuration: WKWebViewConfiguration) -> WKWebView {
        let controller = configuration.userContentController
        controller.add(WeakMessageHandler(self), name: Self.messageHandler)
        // What solo.desktop in the client looks for, in place of the desktop
        // app's window.soloDesktop.
        controller.addUserScript(WKUserScript(
            source: """
                window.soloApp = Object.freeze({
                    beginSignIn: () => window.webkit.messageHandlers.\(Self.messageHandler).postMessage('request_sign-in'),
                });
                """,
            injectionTime: .atDocumentStart,
            forMainFrameOnly: true))
        #if DEBUG
        DevShim.install(in: controller)
        #endif
        return super.webView(with: frame, configuration: configuration)
    }

    override func capacitorDidLoad() {
        let pencil = UIPencilInteraction()
        pencil.delegate = self
        webView?.addInteraction(pencil)
    }

    // Only from the instance's own page, never a frame inside it.
    func userContentController(_ userContentController: WKUserContentController, didReceive message: WKScriptMessage) {
        guard message.frameInfo.isMainFrame,
              let instance = bridge?.config.serverURL,
              Self.isOrigin(message.frameInfo.securityOrigin, of: instance),
              message.body as? String == "request_sign-in"
        else { return }

        beginSignIn(on: instance)
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
