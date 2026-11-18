import SwiftUI
import UserNotifications

/// The signed-in person's notifications: assignments and deadline reminders.
struct InboxView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @State private var openTask: UUID?
    @State private var confirmation: Confirmation?

    var body: some View {
        let store = session.notifications
        Group {
            if let inbox = store.inbox {
                if inbox.items.isEmpty {
                    ContentUnavailableView("You're all caught up.", systemImage: "bell.slash",
                                           description: Text("Assignments and deadline reminders show up here."))
                } else {
                    GroupedList {
                        ForEach(inbox.items) { item in
                            Button { open(item) } label: { row(item) }
                                .themedRow()
                                .swipeActions(edge: .leading) {
                                    if item.isUnread {
                                        Button("Read", systemImage: "envelope.open") {
                                            Task { await store.markRead(item) }
                                        }
                                        .tint(theme.info)
                                    }
                                }
                                // The same by a secondary click, which is how a Mac gets at it.
                                #if os(macOS)
                                .contextMenu {
                                    if item.isUnread {
                                        Button("Mark as Read", systemImage: "envelope.open") {
                                            Task { await store.markRead(item) }
                                        }
                                    }
                                }
                                #endif
                        }
                    }
                    .themedList()
                }
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .confirmation($confirmation)
        .taskDetail($openTask)
        // Outside the detail panel, or the title and menu never reach the bar beside a sidebar.
        .navigationTitle("Inbox")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Menu("Inbox actions", systemImage: "ellipsis") {
                    Button("Mark All as Read", systemImage: "envelope.open") {
                        Task { await store.markAllRead() }
                    }
                    .disabled(store.unreadCount == 0)
                    Button("Clear All", systemImage: "trash", role: .destructive) {
                        confirmation = Confirmation(title: "Clear notifications",
                                                    message: "Remove every notification from your inbox?", action: "Clear All") {
                            await store.clearAll()
                        }
                    }
                    .disabled(store.inbox?.items.isEmpty ?? true)
                    Divider()
                    Button("Notification Settings", systemImage: "gearshape") {
                        navigator.push(.notificationSettings)
                    }
                }
            }
        }
    }

    private func row(_ item: NotificationItem) -> some View {
        HStack(alignment: .top, spacing: 12) {
            Image(systemName: item.kind.symbol)
                .font(.title3)
                .foregroundStyle(item.isUnread ? theme.primary : theme.textDisabled)
                .frame(width: 30)
            VStack(alignment: .leading, spacing: 3) {
                Text(item.title)
                    .font(.subheadline.weight(item.isUnread ? .semibold : .regular))
                    .foregroundStyle(theme.textPrimary)
                    .multilineTextAlignment(.leading)
                if let body = item.body?.nilIfBlank {
                    Text(body).font(.footnote).foregroundStyle(theme.textSecondary).lineLimit(3)
                        .multilineTextAlignment(.leading)
                }
                Text(item.createdAt.relative).font(.caption).foregroundStyle(theme.textDisabled)
            }
            Spacer(minLength: 0)
            if item.isUnread {
                Circle().fill(theme.primary).frame(width: 8, height: 8).padding(.top, 6)
                    .accessibilityLabel("Unread")
            }
        }
        .padding(.vertical, 4)
    }

    /// The text survives its task being deleted; the ids don't, and then there is nowhere to go.
    private func open(_ item: NotificationItem) {
        Task {
            await session.notifications.markRead(item)
            if let taskId = item.taskId {
                openTask = taskId
            } else if let boardId = item.boardId, let board = try? await session.api.board(boardId) {
                navigator.open(.board(project: board.projectAlias, board: board.alias))
            }
        }
    }
}

/// Which events notify, which reach email, and when the digest goes out.
struct NotificationSettingsView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Environment(\.openURL) private var openURL

    @State private var preferences: NotificationPreferences?
    @State private var permission: UNAuthorizationStatus = .notDetermined
    @State private var zones: [String] = []
    @State private var pickingZone = false
    @State private var failed = false
    @State private var busy = false

    private var emailOn: Bool { session.config.emailEnabled == true }

    /// What the device can and can't do, which is not what a notifications switch usually means.
    private static var deviceNote: String {
        let reminders = "Solo has no push notifications: an instance you host has no way to send them. "
            + "Your deadline reminders are scheduled on this device ahead of time, so they arrive when they are due "
            + "even with Solo closed. "
        #if os(macOS)
        return reminders + "Anything else, such as being assigned a task, shows while Solo is running."
        #else
        return reminders + "Anything else, such as being assigned a task, shows while Solo is open, and otherwise "
            + "when iOS next lets it check in the background, which can be hours later."
        #endif
    }
    private static let weekdays: [Weekday] = [.monday, .tuesday, .wednesday, .thursday, .friday, .saturday, .sunday]

    var body: some View {
        Form {
            Section {
                switch permission {
                case .authorized, .provisional, .ephemeral:
                    Label("Notifications are allowed on this device.", systemImage: "bell.badge")
                case .denied:
                    VStack(alignment: .leading, spacing: 8) {
                        Text("Notifications are blocked for Solo in Settings.")
                        Button("Open Settings") {
                            if let url = Platform.notificationSettingsURL { openURL(url) }
                        }
                    }
                default:
                    Button("Allow Notifications", systemImage: "bell.badge") {
                        Task {
                            _ = try? await UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .badge, .sound])
                            await readPermission()
                            // Allowed only now: the reminders asked for before this were turned away.
                            await session.notifications.refresh()
                        }
                    }
                }
            } header: {
                Text("On this device")
            } footer: {
                // Said outright, because the switch above looks like the one that turns push on.
                Text(Self.deviceNote)
            }
            .themedRow()

            if let preferences {
                Section("Notify me") {
                    Toggle("When I'm assigned to a task or epic", isOn: binding(\.notifyOnAssigned))
                    Toggle("When a task of mine is due in two weeks, and again in one", isOn: binding(\.notifyOnDeadline))
                }
                .themedRow()

                if emailOn {
                    Section("Email me") {
                        Toggle("When I'm assigned to a task or epic", isOn: binding(\.emailOnAssigned))
                        Toggle("When a task of mine is due in two weeks, and again in one", isOn: binding(\.emailOnDeadline))
                    }
                    .themedRow()
                }

                Section {
                    if emailOn {
                        Picker("How often", selection: binding(\.digestFrequency)) {
                            ForEach(DigestFrequency.allCases) { Text($0.title).tag($0) }
                        }
                        if preferences.digestFrequency == .weekly {
                            Picker("On", selection: binding(\.digestWeekday)) {
                                ForEach(Self.weekdays) { Text($0.rawValue).tag($0) }
                            }
                        }
                        if preferences.digestFrequency == .daily || preferences.digestFrequency == .weekly {
                            Picker("Send at", selection: binding(\.digestHour)) {
                                ForEach(0..<24, id: \.self) { Text("\($0):00").tag($0) }
                            }
                        }
                    } else {
                        Text("Email isn't set up on this instance. Ask an admin to turn it on.")
                            .foregroundStyle(theme.textSecondary)
                    }
                } header: {
                    Text("Email digest")
                } footer: {
                    if emailOn, preferences.digestFrequency == .asItHappens {
                        Text("New notifications are gathered into one email at most every 15 minutes.")
                    }
                }
                .themedRow()

                Section {
                    Button { pickingZone = true } label: {
                        LabeledContent("Time zone", value: preferences.timeZone ?? "UTC")
                    }
                    .foregroundStyle(theme.textPrimary)
                } footer: {
                    Text("Reminders go out at 9:00, \(preferences.timeZone ?? "UTC") time.")
                }
                .themedRow()
            } else if failed {
                Section {
                    Text("Couldn't load your notification settings. Try again in a moment.")
                        .foregroundStyle(theme.error)
                }
                .themedRow()
            } else {
                Section { ProgressView() }.themedRow()
            }
        }
        .themedList()
        .disabled(busy)
        .navigationTitle("Notifications")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            await readPermission()
            do {
                preferences = try await session.api.notificationPreferences()
            } catch {
                failed = true
            }
            zones = (try? await session.api.timeZones()) ?? []
        }
        .sheet(isPresented: $pickingZone) {
            TimeZonePicker(zones: zones, selection: preferences?.timeZone) { zone in
                guard var updated = preferences else { return }
                updated.timeZone = zone
                save(updated)
            }
        }
    }

    private func readPermission() async {
        permission = await UNUserNotificationCenter.current().notificationSettings().authorizationStatus
    }

    private func binding<Value>(_ keyPath: WritableKeyPath<NotificationPreferences, Value>) -> Binding<Value> {
        Binding(get: { preferences![keyPath: keyPath] }, set: { value in
            guard var updated = preferences else { return }
            updated[keyPath: keyPath] = value
            save(updated)
        })
    }

    /// Each change is its own save, applied here first so the control doesn't snap back while it travels.
    private func save(_ updated: NotificationPreferences) {
        let previous = preferences
        preferences = updated
        busy = true
        Task {
            defer { busy = false }
            if let saved = await session.save({ try await session.api.updateNotificationPreferences(updated) }) {
                preferences = saved
            } else {
                preferences = (try? await session.api.notificationPreferences()) ?? previous
            }
        }
    }
}

private struct TimeZonePicker: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme
    var zones: [String]
    var selection: String?
    var onPick: (String) -> Void
    @State private var query = ""

    var body: some View {
        NavigationStack {
            GroupedList {
                let device = TimeZone.current.identifier
                if query.isEmpty, zones.contains(device) {
                    Section("This device") { row(device) }
                }
                Section {
                    ForEach(zones.filter { query.isEmpty || $0.localizedCaseInsensitiveContains(query.replacingOccurrences(of: " ", with: "_")) },
                            id: \.self) { row($0) }
                }
            }
            .themedList()
            .searchable(text: $query, placement: .navigationBarDrawer(displayMode: .always))
            .navigationTitle("Time zone")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
            }
        }
    }

    private func row(_ zone: String) -> some View {
        Button {
            onPick(zone)
            dismiss()
        } label: {
            HStack {
                Text(zone.replacingOccurrences(of: "_", with: " ")).foregroundStyle(theme.textPrimary)
                Spacer()
                if zone == selection {
                    Image(systemName: "checkmark").foregroundStyle(theme.primary)
                }
            }
        }
        .themedRow()
    }
}
