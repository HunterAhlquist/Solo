import SwiftUI
#if os(iOS)
import UIKit

// A tab view swaps its pages with no transition at all: one is there, then the
// other. SwiftUI has no say in that, but the tab bar controller underneath it
// asks its delegate for one — so this lends it a delegate that answers, and
// passes every other question on to the delegate SwiftUI put there.

extension View {
    /// On a tab's content: has the tab view it is in slide and fade between its tabs.
    func animatesTabChanges() -> some View {
        background(TabTransitionInstaller().frame(width: 0, height: 0).accessibilityHidden(true))
    }
}

private struct TabTransitionInstaller: UIViewControllerRepresentable {
    func makeUIViewController(context: Context) -> Installer { Installer() }
    func updateUIViewController(_ controller: Installer, context: Context) { controller.install() }

    final class Installer: UIViewController {
        override func didMove(toParent parent: UIViewController?) {
            super.didMove(toParent: parent)
            install()
        }

        override func viewWillAppear(_ animated: Bool) {
            super.viewWillAppear(animated)
            install()
        }

        func install() {
            guard let tabs = tabBarController, !(tabs.delegate is TabTransitionDelegate) else { return }
            let delegate = TabTransitionDelegate(wrapping: tabs.delegate)
            // The controller holds its delegate weakly; the installer cannot be what keeps it, since
            // each tab has one and any of them may go.
            objc_setAssociatedObject(tabs, &TabTransitionDelegate.key, delegate, .OBJC_ASSOCIATION_RETAIN_NONATOMIC)
            tabs.delegate = delegate
        }
    }
}

private final class TabTransitionDelegate: NSObject, UITabBarControllerDelegate {
    nonisolated(unsafe) static var key: UInt8 = 0
    // Read from the two overrides below, which the runtime may call from anywhere; set once, here.
    nonisolated(unsafe) private weak var wrapped: (any UITabBarControllerDelegate)?

    init(wrapping delegate: UITabBarControllerDelegate?) {
        wrapped = delegate
    }

    // Everything this class doesn't answer itself goes to the delegate it stands in front of.
    nonisolated override func responds(to selector: Selector!) -> Bool {
        super.responds(to: selector) || (wrapped?.responds(to: selector) ?? false)
    }

    nonisolated override func forwardingTarget(for selector: Selector!) -> Any? {
        wrapped
    }

    func tabBarController(_ tabs: UITabBarController, animationControllerForTransitionFrom from: UIViewController,
                          to: UIViewController) -> UIViewControllerAnimatedTransitioning? {
        // SwiftUI fills the bar with tabs rather than a list of controllers, so the order is read from those.
        let controllers = tabs.viewControllers ?? tabs.tabs.compactMap(\.viewController)
        guard from !== to else { return nil }
        let old = controllers.firstIndex(of: from), new = controllers.firstIndex(of: to)
        return TabSlide(forward: (new ?? 0) >= (old ?? 0))
    }
}

/// The new page comes in from the side its tab is on while the old one leaves
/// by the other, both fading: enough movement to say which way you went,
/// short enough not to be waited for.
private final class TabSlide: NSObject, UIViewControllerAnimatedTransitioning {
    private let forward: Bool
    private let distance: CGFloat = 28

    init(forward: Bool) {
        self.forward = forward
    }

    func transitionDuration(using context: UIViewControllerContextTransitioning?) -> TimeInterval { 0.3 }

    func animateTransition(using context: UIViewControllerContextTransitioning) {
        guard let to = context.view(forKey: .to) else {
            context.completeTransition(false)
            return
        }
        let from = context.view(forKey: .from)
        if let controller = context.viewController(forKey: .to) {
            to.frame = context.finalFrame(for: controller)
        }
        context.containerView.addSubview(to)

        // With Reduce Motion the pages only cross-fade.
        let shift = UIAccessibility.isReduceMotionEnabled ? 0 : (forward ? distance : -distance)
        to.alpha = 0
        to.transform = CGAffineTransform(translationX: shift, y: 0)

        let animator = UIViewPropertyAnimator(duration: transitionDuration(using: context), dampingRatio: 1) {
            to.alpha = 1
            to.transform = .identity
            from?.alpha = 0
            from?.transform = CGAffineTransform(translationX: -shift, y: 0)
        }
        animator.addCompletion { _ in
            // The old page is kept, and must be as it was when its tab comes back.
            from?.alpha = 1
            from?.transform = .identity
            to.alpha = 1
            to.transform = .identity
            context.completeTransition(!context.transitionWasCancelled)
        }
        animator.startAnimation()
    }
}
#else
extension View {
    /// A Mac has a sidebar and no tab bar: there is nothing to animate.
    func animatesTabChanges() -> some View { self }
}
#endif
