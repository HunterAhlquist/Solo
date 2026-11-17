#if os(macOS)
import SwiftUI

/// What the Settings scene shows: the signed-in app's settings (`SettingsShell`), with the
/// environment a page of the app expects — a window starts with none of the main one's. With
/// nobody signed in it is the list of instances alone.
struct SettingsWindow: View {
    static let scene = "settings"

    var model: AppModel

    var body: some View {
        Group {
            if let session = model.session {
                ThemedRoot {
                    SettingsShell()
                        .environment(session)
                        .environment(session.toasts)
                        .environment(session.settingsNavigator)
                        .overlay { ToastOverlay().environment(session.toasts) }
                }
                // Another session is another instance's settings from the top.
                .id(ObjectIdentifier(session))
            } else {
                // Nobody signed in, or the moment between one instance and the next: the one
                // section that needs no session, which is also the way to one.
                ThemedRoot {
                    InstancesOnlySettings()
                }
            }
        }
        .environment(model)
        .environment(model.themes)
        .preferredColorScheme(model.themes.preference.colorScheme)
        .frame(minWidth: 760, minHeight: 500)
    }
}
#endif
