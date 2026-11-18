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
        // Asked every time, whether or not anything is announced: it is also how the device
        // comes to know what it has seen, so a background refresh doesn't say it again.
        let news = await DeviceNotifications.claim(fetched.items.filter(\.isUnread), on: api.baseURL)
        for item in fresh {
            toasts.show(item.title, .info)
        }
        // Behind another app, or put away with the connection still up, there is nobody to
        // read a toast: the device shows it. The inbox lists the newest first.
        if !Platform.isActive {
            for item in fetched.items.reversed() where news.contains(item.id) {
                await DeviceNotifications.show(item, on: api.baseURL)
            }
        }
        try? await UNUserNotificationCenter.current().setBadgeCount(fetched.unreadCount)
        await scheduleReminders()
    }

    /// Hands the device the deadline reminders still to come, to show at their time whether
    /// the app is open or not. Left as they are when the instance can't be asked.
    private func scheduleReminders() async {
        guard let reminders = try? await api.upcomingReminders() else { return }
        await DeviceNotifications.schedule(reminders, on: api.baseURL)
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

    /// Reminders and digests go by UTC until a client says where its person is.
    private func reportTimeZone() async {
        guard var preferences = try? await api.notificationPreferences(), preferences.timeZone == nil else { return }
        preferences.timeZone = TimeZone.current.identifier
        _ = try? await api.updateNotificationPreferences(preferences)
    }
}
