#if os(macOS)
import SwiftUI

/// The menu bar. What the sidebar and the bars do with a click, by name and by key: where to
/// go, the way back, and a new task on the board that is showing.
struct SoloCommands: Commands {
    var model: AppModel
    @FocusedValue(\.newTask) private var newTask

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

        // The app's settings are a page of it — appearance, notifications, devices — under the account.
        CommandGroup(replacing: .appSettings) {
            Button("Settings…") { navigator?.popToRoot(.account) }
                .keyboardShortcut(",")
                .disabled(navigator == nil)
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
                Button("Account") { navigator?.popToRoot(.account) }
                    .keyboardShortcut("4")
                if model.session?.user.isSuperAdmin == true {
                    Button("Admin Settings") { navigator?.open(.admin) }
                        .keyboardShortcut("5")
                }
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
