import SwiftUI

@main
struct SoloApp: App {
    @State private var model = AppModel()

    init() {
        SoloImages.configure()
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(model)
                .environment(model.themes)
                .preferredColorScheme(model.themes.preference.colorScheme)
                .task { await model.launch() }
        }
    }
}
