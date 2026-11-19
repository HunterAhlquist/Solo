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

        // The instances kept on this Mac, the one showing ticked: the Settings window's list,
        // a key away. The first nine are on Control and their number.
        CommandGroup(after: .newItem) {
            Divider()
            Menu("Switch Instance") {
                ForEach(Array(model.instances.enumerated()), id: \.element.id) { index, instance in
                    Toggle(isOn: Binding(get: { instance.url == model.instanceURL },
                                         set: { _ in Task { await model.switchTo(instance.url) } })) {
                        let unread = instance.url == model.instanceURL ? 0 : model.unreadElsewhere[instance.url] ?? 0
                        Text(unread > 0 ? "\(model.label(for: instance)) — \(unread) unread" : model.label(for: instance))
                    }
                    .keyboardShortcut(index < 9
                        ? KeyboardShortcut(KeyEquivalent(Character("\(index + 1)")), modifiers: .control) : nil)
                }
                if model.instances.isEmpty {
                    Text("No Instances")
                }
                Divider()
                Button("Add Instance…") {
                    openWindow(id: "main")
                    model.addingInstance = true
                }
                .disabled(model.instances.isEmpty)
            }
        }

        CommandGroup(after: .sidebar) {
            Button("Reload") { Task { await ReloadCenter.shared.reload() } }
                .keyboardShortcut("r")
                .disabled(navigator == nil)
        }

        CommandGroup(replacing: .appInfo) {
            Button("About \(AboutWindow.appName)") { openWindow(id: AboutWindow.scene) }
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
                // Places that need the instance: with it out of reach there is nowhere to go
                // but what is on this Mac, which the window is already showing.
                Group {
                    Button("Your Tasks") { navigator?.popToRoot(.work) }
                        .keyboardShortcut("1")
                    Button("All Projects") { navigator?.popToRoot(.projects) }
                        .keyboardShortcut("2")
                    Button("Inbox") { navigator?.popToRoot(.inbox) }
                        .keyboardShortcut("3")
                }
                .disabled(model.session?.offlineMode == true)
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
