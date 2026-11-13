import Foundation
import Observation
import UserNotifications

/// The signed-in person's inbox, kept current by the notifications topic.
@Observable
final class NotificationStore {
    private(set) var inbox: NotificationInbox?
    var unreadCount: Int { inbox?.unreadCount ?? 0 }

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private let hub: HubClient
    @ObservationIgnored private let toasts: ToastCenter
    @ObservationIgnored private var known: Set<UUID> = []
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var started = false

    init(api: APIClient, hub: HubClient, toasts: ToastCenter) {
        self.api = api
        self.hub = hub
        self.toasts = toasts
    }

    func start() async {
        guard !started else { return }
        started = true
        subscriptions = [
            hub.topicMessage.subscribe { [weak self] message in
                guard message.topic == HubTopics.notifications else { return }
                Task { await self?.refresh() }
            },
            hub.reconnected.subscribe { [weak self] in
                Task { await self?.refresh() }
            },
        ]
        await hub.subscribeUserTopic(HubTopics.notifications)
        await refresh()
        await reportTimeZone()
    }

    func refresh() async {
        guard let fetched = try? await api.notifications() else { return }
        // Only what arrived since the last look is announced; the first load
        // announces nothing, or signing in would replay the whole inbox.
        let fresh = inbox == nil ? [] : fetched.items.filter { $0.isUnread && !known.contains($0.id) }
        known.formUnion(fetched.items.map(\.id))
        inbox = fetched
        for item in fresh {
            announce(item)
        }
        try? await UNUserNotificationCenter.current().setBadgeCount(fetched.unreadCount)
    }

    func markRead(_ notification: NotificationItem) async {
        guard notification.isUnread else { return }
        try? await api.markNotificationRead(notification.id)
        if var current = inbox, let index = current.items.firstIndex(where: { $0.id == notification.id }) {
            current.items[index].readAt = Date()
            current.unreadCount = max(0, current.unreadCount - 1)
            inbox = current
            try? await UNUserNotificationCenter.current().setBadgeCount(current.unreadCount)
        }
    }

    func markAllRead() async {
        try? await api.markAllNotificationsRead()
        await refresh()
    }

    func clearAll() async {
        try? await api.clearNotifications()
        await refresh()
    }

    /// In the app it is a toast. Behind the app it is a notification on the
    /// device, when the person has allowed them: the hub connection only lives
    /// as long as iOS lets the app run, so this covers the minutes after
    /// leaving, not the hours.
    private func announce(_ item: NotificationItem) {
        toasts.show(item.title, .info)
        Task {
            let center = UNUserNotificationCenter.current()
            guard await center.notificationSettings().authorizationStatus == .authorized else { return }
            let content = UNMutableNotificationContent()
            content.title = item.title
            content.body = item.body ?? ""
            content.sound = .default
            try? await center.add(UNNotificationRequest(identifier: item.id.uuidString, content: content, trigger: nil))
        }
    }

    /// Reminders and digests go by UTC until a client says where its person is.
    private func reportTimeZone() async {
        guard var preferences = try? await api.notificationPreferences(), preferences.timeZone == nil else { return }
        preferences.timeZone = TimeZone.current.identifier
        _ = try? await api.updateNotificationPreferences(preferences)
    }
}
