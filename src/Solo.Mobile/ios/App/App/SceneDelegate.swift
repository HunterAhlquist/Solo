import UIKit
import Capacitor

class SceneDelegate: UIResponder, UIWindowSceneDelegate {
    var window: UIWindow?

    func scene(_ scene: UIScene, willConnectTo session: UISceneSession, options connectionOptions: UIScene.ConnectionOptions) {
        guard let windowScene = scene as? UIWindowScene else { return }

        // A widget's tap that launched the app: before the controller reads
        // which instance and page to open.
        if let url = connectionOptions.urlContexts.first(where: { $0.url.scheme == WidgetLink.scheme })?.url {
            SoloViewController.openOnLaunch(widgetLink: url)
        }

        window = UIWindow(windowScene: windowScene)
        window?.rootViewController = SoloViewController()
        window?.makeKeyAndVisible()

        SceneDelegateProxy.shared.scene(scene, willConnectTo: session, options: connectionOptions)
    }

    func scene(_ scene: UIScene, openURLContexts URLContexts: Set<UIOpenURLContext>) {
        if let url = URLContexts.first(where: { $0.url.scheme == WidgetLink.scheme })?.url {
            (window?.rootViewController as? SoloViewController)?.open(widgetLink: url)
            return
        }
        SceneDelegateProxy.shared.scene(scene, openURLContexts: URLContexts)
    }

    func scene(_ scene: UIScene, continue userActivity: NSUserActivity) {
        SceneDelegateProxy.shared.scene(scene, continue: userActivity)
    }
}
