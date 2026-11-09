import UIKit
import WebKit
import Capacitor

/// Capacitor's bridge view controller, plus the Apple Pencil double-tap that no
/// web API delivers. The page never calls into this: native code dispatches a
/// generic `solo:device-action` event and the page decides what it means
/// (`ISoloDeviceActions` in Solo.Shared, `solo.deviceActions` in the client).
/// So nothing native is handed to the page or the extensions it loads.
class SoloViewController: CAPBridgeViewController, UIPencilInteractionDelegate {
    override func webViewConfiguration(for instanceConfiguration: InstanceConfiguration) -> WKWebViewConfiguration {
        let configuration = super.webViewConfiguration(for: instanceConfiguration)
        #if DEBUG
        DevShim.install(in: configuration.userContentController)
        #endif
        return configuration
    }

    override func capacitorDidLoad() {
        let pencil = UIPencilInteraction()
        pencil.delegate = self
        webView?.addInteraction(pencil)
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
}
