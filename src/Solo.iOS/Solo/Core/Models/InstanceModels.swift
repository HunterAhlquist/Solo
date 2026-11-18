import Foundation

// MARK: - Bootstrap

/// Anonymous client bootstrap: what the instance is called and how it signs people in.
nonisolated struct AuthConfig: Codable, Hashable, Sendable {
    var authority: String
    var clientId: String
    var provider: String
    var setupCompleted: Bool
    var displayName: String?
    var tagline: String?
    var emailEnabled: Bool?

    var siteName: String { displayName?.nilIfBlank ?? "Solo" }
    var siteTagline: String { tagline?.nilIfBlank ?? "Project management for game development." }

    /// "Google", "Microsoft", or a neutral word for a generic OIDC provider.
    var providerName: String {
        switch provider.lowercased() {
        case "google": "Google"
        case "microsoft", "entra", "azure": "Microsoft"
        default: "single sign-on"
        }
    }
}

nonisolated struct InstanceVersion: Codable, Hashable, Sendable {
    var extensionApiVersion: String
    var serverVersion: String
}

/// A minted API session. The token is shown exactly once; the server keeps only a hash.
nonisolated struct SessionGrant: Codable, Sendable {
    var token: String
    var expiresAt: Date
    var user: User
}

nonisolated struct AuthSession: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var description: String?
    var createdAt: Date
    var lastUsedAt: Date
    var expiresAt: Date
    var isCurrent: Bool
}

nonisolated struct DesktopAuthFlow: Codable, Sendable {
    var state: String
    var expiresAt: Date
}

// MARK: - Instance settings

nonisolated struct InstanceSettings: Codable, Hashable, Sendable {
    var auditRetentionDays: Int
    var setupCompleted: Bool
    var themeId: String?
    var version: UInt32
    var displayName: String?
    var tagline: String?
}

nonisolated struct UpdateInstanceSettingsRequest: Encodable, Sendable {
    var auditRetentionDays: Int
    var themeId: String?
    /// Empty clears it; nil leaves it as it is.
    var displayName: String?
    var tagline: String?
    var version: UInt32
}

// MARK: - Themes

nonisolated struct ThemeDocument: Codable, Hashable, Sendable {
    var id: String
    var name: String
    var description: String?
    var author: String?
    var isBuiltIn: Bool?
    var isFallback: Bool?
    var requestedThemeId: String?
    var typography: ThemeTypography?
    var layout: ThemeLayout?
    var login: ThemeLogin?
    /// The picture behind the app itself, where the theme has one.
    var background: ThemeBackground?
    var dark: ThemePalette?
    var light: ThemePalette?
}

nonisolated struct ThemeSummary: Codable, Hashable, Sendable, Identifiable {
    var id: String
    var name: String
    var description: String?
    var author: String?
    var isBuiltIn: Bool
    var isActive: Bool
}

nonisolated struct ThemeScanResult: Codable, Hashable, Sendable {
    var themes: [ThemeSummary]
    var errors: [ThemeScanError]
}

nonisolated struct ThemeScanError: Codable, Hashable, Sendable {
    var file: String
    var message: String
}

/// The palette keys a theme may set. Every value is a CSS colour string.
nonisolated struct ThemePalette: Codable, Hashable, Sendable {
    var primary: String?
    var secondary: String?
    var tertiary: String?
    var primaryContrastText: String?
    var background: String?
    var backgroundGray: String?
    var surface: String?
    var appbarBackground: String?
    var appbarText: String?
    var drawerBackground: String?
    var drawerText: String?
    var drawerIcon: String?
    var textPrimary: String?
    var textSecondary: String?
    var textDisabled: String?
    var actionDefault: String?
    var divider: String?
    var dividerLight: String?
    var linesDefault: String?
    var linesInputs: String?
    var success: String?
    var warning: String?
    var error: String?
    var info: String?
}

nonisolated struct ThemeTypography: Codable, Hashable, Sendable {
    var fontFamily: [String]?
    var headingFontFamily: [String]?
    var headingWeight: String?
    var buttonWeight: String?
}

nonisolated struct ThemeLayout: Codable, Hashable, Sendable {
    var borderRadius: String?
}

nonisolated struct ThemeLogin: Codable, Hashable, Sendable {
    var background: ThemeBackground?
    var logo: String?
    var logoLight: String?
}

nonisolated struct ThemeBackground: Codable, Hashable, Sendable {
    var image: String?
    var imageLight: String?
    var fit: String?
    var position: String?
    var scrim: Double?
    var color: String?
}

// MARK: - Notifications

nonisolated struct NotificationItem: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var kind: NotificationKind
    var title: String
    var body: String?
    var projectId: UUID?
    var boardId: UUID?
    var taskId: UUID?
    var epicId: UUID?
    var createdAt: Date
    var readAt: Date?
    /// A deadline entry's reminder key: the `UpcomingReminder.key` it was announced under
    /// before the instance raised it. Nil for other kinds, and from an instance that
    /// doesn't offer reminders ahead.
    var key: String?

    var isUnread: Bool { readAt == nil }
}

/// A deadline reminder the instance will raise later, for the device to show at that time
/// itself (`DeviceNotifications`).
nonisolated struct UpcomingReminder: Codable, Hashable, Sendable {
    var key: String
    var taskId: UUID
    var fireAt: Date
    var title: String
    var body: String?
}

nonisolated struct NotificationInbox: Codable, Hashable, Sendable {
    var items: [NotificationItem]
    var unreadCount: Int
}

nonisolated struct NotificationPreferences: Codable, Hashable, Sendable {
    /// An IANA id; nil until a client reports one.
    var timeZone: String?
    var notifyOnAssigned: Bool
    var notifyOnDeadline: Bool
    var emailOnAssigned: Bool
    var emailOnDeadline: Bool
    var digestFrequency: DigestFrequency
    var digestHour: Int
    var digestWeekday: Weekday
    var version: UInt32
}

// MARK: - Email (admin)

nonisolated struct EmailSettings: Codable, Hashable, Sendable {
    var enabled: Bool
    var publicAddress: String?
    var fromName: String?
    var fromAddress: String?
    var smtpHost: String?
    var smtpPort: Int
    var security: SmtpSecurity
    var username: String?
    var hasPassword: Bool
    var passwordFromEnvironment: Bool
    var recentFailures: [EmailFailure]
    var version: UInt32
}

nonisolated struct EmailFailure: Codable, Hashable, Sendable {
    var to: String
    var subject: String
    var error: String?
    var failedAt: Date
}

/// Also the body of a test send. `password`: nil keeps the stored one, empty clears it.
nonisolated struct UpdateEmailSettingsRequest: Encodable, Sendable {
    var enabled: Bool
    var publicAddress: String?
    var fromName: String?
    var fromAddress: String?
    var smtpHost: String?
    var smtpPort: Int
    var security: SmtpSecurity
    var username: String?
    var password: String?
    var version: UInt32
}

nonisolated struct TestEmailResult: Codable, Sendable {
    var sentTo: String
}

// MARK: - Users (admin)

nonisolated struct AdminUser: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var email: String
    var displayName: String?
    var avatarUrl: String?
    var isSuperAdmin: Bool
    var activeSessions: Int

    var name: String { displayName ?? email }
}

nonisolated struct SignOutEverywhereResult: Codable, Sendable {
    var sessions: Int
    var extensions: [RevokedCredentials]
}

nonisolated struct RevokedCredentials: Codable, Sendable {
    var extensionId: String
    var count: Int
}

// MARK: - Extensions & updates

/// An extension the instance has loaded, as any signed-in client learns of it.
nonisolated struct ClientExtension: Codable, Hashable, Sendable, Identifiable {
    var id: String
    var name: String
    var version: String
    var soloClientVersion: String?
    var clientAssembly: String?
    var nav: [ClientExtensionNav]
}

nonisolated struct ClientExtensionNav: Codable, Hashable, Sendable {
    var route: String
    var label: String
    var icon: String?
    var requires: String?
}

nonisolated struct ExtensionRegistration: Codable, Hashable, Sendable, Identifiable {
    var extensionId: String
    var displayName: String?
    var version: String?
    var schemaName: String?
    var state: String
    var failureReason: String?
    var installedAt: Date
    var lastLoadedAt: Date?
    var hooks: [String]
    var canPurge: Bool

    var id: String { extensionId }
}

nonisolated struct ExtensionFailure: Codable, Hashable, Sendable {
    var source: String
    var reason: String
}

nonisolated struct ExtensionAdmin: Codable, Hashable, Sendable {
    var extensions: [ExtensionRegistration]
    var failures: [ExtensionFailure]
}

nonisolated struct UpdateStatus: Codable, Hashable, Sendable {
    var enabled: Bool
    var checkedAt: Date?
    var error: String?
    var server: ServerUpdate?
    var extensions: [ExtensionUpdate]
}

nonisolated struct ServerUpdate: Codable, Hashable, Sendable {
    var runningVersion: String
    var availableVersion: String
    var releaseUrl: String
    var image: String?
    var published: Date?
}

nonisolated struct ExtensionUpdate: Codable, Hashable, Sendable, Identifiable {
    var extensionId: String
    var displayName: String?
    var installedVersion: String?
    var availableVersion: String
    var sizeBytes: Int64
    var compatible: Bool
    var incompatibility: String?

    var id: String { extensionId }
}

nonisolated struct ExtensionUpdateResult: Codable, Hashable, Sendable {
    var extensionId: String
    var version: String
    var restarting: Bool
}

nonisolated struct ExtensionBulkUpdateResult: Codable, Hashable, Sendable {
    var installed: [ExtensionUpdateResult]
    var failed: [ExtensionUpdateFailure]
    var restarting: Bool
}

nonisolated struct ExtensionUpdateFailure: Codable, Hashable, Sendable {
    var extensionId: String
    var reason: String
}

// MARK: - Hub payloads

nonisolated struct EntityChangedEvent: Codable, Sendable {
    var entityType: String
    var entityId: UUID
    var kind: ChangeKind
    var changedFields: [String]
    var actorUserId: UUID
}

nonisolated struct PresenceEntry: Codable, Hashable, Sendable {
    var userId: UUID
    var displayName: String?
    var entityType: String
    var entityId: UUID
    var mode: PresenceMode
}

nonisolated struct BoardPresenceSnapshot: Codable, Sendable {
    var boardId: UUID
    var entries: [PresenceEntry]
}

/// A message an extension (or core) published on one of its live topics.
nonisolated struct TopicMessage: Codable, Sendable {
    var projectId: UUID
    var topic: String
    var kind: String
    var data: String?
    var actorUserId: UUID
}

nonisolated struct TopicPresenceEntry: Codable, Hashable, Sendable {
    var userId: UUID
    var displayName: String?
    var mode: PresenceMode
}

nonisolated struct TopicPresenceSnapshot: Codable, Sendable {
    var projectId: UUID
    var topic: String
    var entries: [TopicPresenceEntry]
}

nonisolated enum LiveSendResult: Int, Codable, Sendable {
    case accepted, notSubscribed, noHandler, tooLarge, rateLimited, failed, notConnected
}

nonisolated enum HubTopics {
    /// A nudge whenever a task assigned to the subscriber, or anything around one, changes.
    static let assignedTasks = "solo:assigned-tasks"
    /// One message per notification raised for the subscriber.
    static let notifications = "solo:notifications"
}

extension String {
    nonisolated var nilIfBlank: String? {
        let trimmed = trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }
}
