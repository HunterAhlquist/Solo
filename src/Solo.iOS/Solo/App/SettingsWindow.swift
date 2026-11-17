#if os(macOS)
import SwiftUI

/// What the Settings scene shows: the signed-in app's settings (`SettingsShell`), with the
/// environment a page of the app expects — a window starts with none of the main one's.
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
                ContentUnavailableView("Not signed in", systemImage: "person.crop.circle",
                                       description: Text("Sign in to an instance to see its settings."))
            }
        }
        .environment(model)
        .environment(model.themes)
        .preferredColorScheme(model.themes.preference.colorScheme)
        .frame(minWidth: 760, minHeight: 500)
    }
}
#endif
