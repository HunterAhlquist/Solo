#if os(macOS)
import SwiftUI

/// The menu bar. What the sidebar and the bars do with a click, by name and by key: where to
/// go, the way back, a new task on the board that is showing, and Settings.
struct SoloCommands: Commands {
    var model: AppModel
    @FocusedValue(\.newTask) private var newTask
    @Environment(\.openWindow) private var openWindow

    private var navigator: Navigator? { model.session?.navigator }

    var body: some Commands {
        SidebarCommands()

        CommandGroup(replacing: .newItem) {
            Button("New Task") { newTask?() }
                .keyboardShortcut("n")
                .disabled(newTask == nil)
        }

        CommandGroup(after: .sidebar) {
            Button("Reload") { Task { await ReloadCenter.shared.reload() } }
                .keyboardShortcut("r")
                .disabled(navigator == nil)
        }

        // Where a Mac keeps such things, and under the key it keeps them on.
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { openWindow(id: SettingsWindow.scene) }
                .keyboardShortcut(",")
        }

        CommandMenu("Go") {
            Group {
                Button("Back") { navigator?.back() }
                    .keyboardShortcut("[")
                    .disabled(navigator?.current == nil)
                Divider()
                Button("Your Tasks") { navigator?.popToRoot(.work) }
                    .keyboardShortcut("1")
                Button("All Projects") { navigator?.popToRoot(.projects) }
                    .keyboardShortcut("2")
                Button("Inbox") { navigator?.popToRoot(.inbox) }
                    .keyboardShortcut("3")
            }
            .disabled(navigator == nil)
        }
    }
}

extension FocusedValues {
    /// Starts a new task on the board in the window, where there is one and it may be written to.
    @Entry var newTask: (() -> Void)?
}
#endif
