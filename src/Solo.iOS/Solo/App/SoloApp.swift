import SwiftUI

@main
struct SoloApp: App {
    @State private var model = AppModel()

    init() {
        SoloImages.configure()
        Perf.start()
        // Both before launch finishes: a tap that launched the app is handed over as it
        // does, and the system takes a background refresh's handler no later.
        DeviceNotifications.shared.install()
        #if os(iOS)
        BackgroundSync.install()
        #endif
    }

    var body: some Scene {
        #if os(macOS)
        // One window, as the app is one session with one place it is looking at: a second
        // window would be the same navigator shown twice, each following the other about.
        Window("Solo", id: "main") {
            // No narrower than a board's three columns can be read in beside the task panel.
            root.frame(minWidth: 960, minHeight: 600).windowBarBacked().withoutBarLine()
        }
        .defaultSize(width: 1360, height: 880)
        .commands { SoloCommands(model: model) }

        // The account, the instance's admin pages and what extensions add beside them: on a
        // Mac these are Settings, in the app's menu and on Command-comma (`SoloCommands`). A
        // window of the app's own and not the system's Settings scene: that one keeps its
        // bar for a row of tabs, and puts a page's Back button in the middle of a second row.
        Window("Settings", id: SettingsWindow.scene) {
            SettingsWindow(model: model)
        }
        .defaultSize(width: 900, height: 640)
        .windowResizability(.contentMinSize)
        .restorationBehavior(.disabled)

        // A sketch or a picture opened from a note: see `CoverWindow`.
        WindowGroup(id: CoverWindow.scene, for: UUID.self) { $id in
            CoverWindow(id: id, model: model)
                .frame(minWidth: 560, minHeight: 420)
        }
        .defaultSize(width: 1040, height: 800)
        // What such a window shows is the page's to say, and the page is not there at the next launch.
        .restorationBehavior(.disabled)
        .commandsRemoved()
        #else
        WindowGroup { root }
        #endif
    }

    private var root: some View {
        RootView()
            .environment(model)
            .environment(model.themes)
            .preferredColorScheme(model.themes.preference.colorScheme)
            .task { await model.launch() }
    }
}
