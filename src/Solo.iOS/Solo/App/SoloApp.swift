import SwiftUI

@main
struct SoloApp: App {
    @State private var model = AppModel()

    init() {
        SoloImages.configure()
        Perf.start()
    }

    var body: some Scene {
        #if os(macOS)
        // One window, as the app is one session with one place it is looking at: a second
        // window would be the same navigator shown twice, each following the other about.
        Window("Solo", id: "main") {
            root.frame(minWidth: 860, minHeight: 540)
        }
        .defaultSize(width: 1360, height: 880)
        .commands { SoloCommands(model: model) }

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
