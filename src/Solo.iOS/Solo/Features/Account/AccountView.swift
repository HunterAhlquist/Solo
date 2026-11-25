import SwiftUI

/// The person's own corner: appearance, notifications, devices, and the way out.
struct AccountView: View {
    @Environment(AppModel.self) private var model
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(ThemeStore.self) private var themes
    @Environment(\.theme) private var theme
    @State private var confirmation: Confirmation?

    var body: some View {
        @Bindable var themes = themes
        GlassList {
            Section {
                HStack(spacing: 14) {
                    UserAvatar(user: session.user, size: 56)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(session.user.name).font(.title3.weight(.semibold))
                        Text(session.user.email).font(.footnote).foregroundStyle(theme.textSecondary)
                        if session.user.isSuperAdmin {
                            Badge(text: "Super admin", color: theme.accentWash, foreground: theme.primary)
                        }
                    }
                }
                .padding(.vertical, 4)
            }
            .themedRow()

            // On a Mac the instances are a section of the Settings window, beside this one.
            #if os(iOS)
            InstancesSection()
            #endif

            Section("Appearance") {
                Picker("Colour scheme", selection: $themes.preference) {
                    ForEach(SchemePreference.allCases) { preference in
                        Label(preference.title, systemImage: preference.symbol).tag(preference)
                    }
                }
                .pickerStyle(.inline)
                .labelsHidden()
            }
            .themedRow()

            Section {
                link("Notifications", "bell.badge", .notificationSettings)
                link("Signed-in Devices", "laptopcomputer.and.iphone", .devices)
                // On a Mac this page is one section of the Settings window, and these are the
                // sections beside it (`SettingsShell`): they are not listed here as well.
                #if os(iOS)
                InstanceExtensionLinks()
                #endif
            }
            .themedRow()

            #if os(iOS)
            if session.user.isSuperAdmin {
                Section {
                    link("Admin Settings", "gearshape.2", .admin)
                } header: {
                    Text("Instance")
                } footer: {
                    if let update = session.serverUpdate {
                        // A development build's version carries its commit after a plus; only the release number matters here.
                        Text("Solo \(update.availableVersion) is available (running "
                            + "\(update.runningVersion.split(separator: "+").first.map(String.init) ?? update.runningVersion)).")
                    }
                }
                .themedRow()
            }
            #endif

            Section {
                link("About \(session.siteName)", "info.circle", .about)
                Button("Sign Out", systemImage: "rectangle.portrait.and.arrow.right") {
                    confirmation = Confirmation(title: "Sign out?",
                                                message: "You'll need to sign in again to reach \(session.siteName).",
                                                action: "Sign Out") {
                        await model.signOut()
                    }
                }
                .foregroundStyle(theme.error)
            }
            .themedRow()
        }
        .themedList()
        .navigationTitle("Account")
        .confirmation($confirmation)
    }

    private func link(_ title: String, _ symbol: String, _ route: Route) -> some View {
        RouteLink(route: route) {
            Label(title, systemImage: symbol)
        }
    }
}

struct AppearanceView: View {
    @Environment(ThemeStore.self) private var themes

    var body: some View {
        @Bindable var themes = themes
        GlassList(form: true) {
            Picker("Colour scheme", selection: $themes.preference) {
                ForEach(SchemePreference.allCases) { preference in
                    Label(preference.title, systemImage: preference.symbol).tag(preference)
                }
            }
            .pickerStyle(.inline)
            .themedRow()
        }
        .themedList()
        .navigationTitle("Appearance")
    }
}

/// The person's sessions — browsers, desktop apps, phones — each one revocable.
/// The current one is marked and left to the ordinary Sign Out.
struct DevicesView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @State private var sessions: [AuthSession]?
    @State private var failed: String?
    @State private var busy = false
    @State private var confirmation: Confirmation?

    var body: some View {
        GlassList {
            if let sessions {
                Section {
                    ForEach(sessions) { item in
                        HStack(spacing: 12) {
                            Image(systemName: symbol(item))
                                .font(.title3)
                                .foregroundStyle(theme.textSecondary)
                                .frame(width: 32)
                            VStack(alignment: .leading, spacing: 3) {
                                HStack(spacing: 6) {
                                    Text(item.description ?? "Unnamed device")
                                    if item.isCurrent {
                                        Badge(text: "This device", color: theme.accentWash, foreground: theme.primary)
                                    }
                                }
                                Text("Signed in \(Day(item.createdAt).formatted) · last used \(Day(item.lastUsedAt).formatted)")
                                    .font(.caption)
                                    .foregroundStyle(theme.textSecondary)
                            }
                        }
                        .swipeActions {
                            if !item.isCurrent {
                                // Red said outright: the app's tint reaches a swipe action too, the
                                // destructive ones included, and the system's white on a bright
                                // accent could not be read.
                                Button("Sign Out", role: .destructive) { Task { await revoke(item) } }
                                    .tint(theme.error)
                            }
                        }
                        .contextMenu {
                            if !item.isCurrent {
                                Button("Sign Out", systemImage: "rectangle.portrait.and.arrow.right", role: .destructive) {
                                    Task { await revoke(item) }
                                }
                            }
                        }
                    }
                } footer: {
                    Text("Browsers and apps signed in to your account. A device you sign out has to sign in again "
                        + "before it can reach \(session.siteName). Swipe a device to sign it out.")
                }
                .themedRow()

                if sessions.contains(where: { !$0.isCurrent }) {
                    Section {
                        // Asked first: one press takes every other browser and app out at once,
                        // and none of them can be put back from here.
                        Button("Sign Out All Other Devices", role: .destructive) {
                            let others = sessions.count { !$0.isCurrent }
                            confirmation = Confirmation(
                                title: "Sign out all other devices?",
                                message: (others == 1 ? "The other device" : "All \(others) other devices")
                                    + " signed in to your account will have to sign in again before "
                                    + "\(others == 1 ? "it" : "they") can reach \(session.siteName).",
                                action: "Sign Out") {
                                    busy = true
                                    if await session.perform({ try await session.api.revokeOtherSessions() }) {
                                        session.toasts.show("Signed out every other device.", .success)
                                    }
                                    busy = false
                                    await load()
                                }
                        }
                        .disabled(busy)
                    }
                    .themedRow()
                }
            } else if let failed {
                Text(failed).foregroundStyle(theme.error).themedRow()
            } else {
                ProgressView().themedRow()
            }
        }
        .themedList()
        .navigationTitle("Signed-in Devices")
        .navigationBarTitleDisplayMode(.inline)
        .confirmation($confirmation)
        .task { await load() }
        .reloads { await load() }
    }

    private func symbol(_ item: AuthSession) -> String {
        let text = (item.description ?? "").lowercased()
        if text.contains("iphone") { return "iphone" }
        if text.contains("ipad") { return "ipad" }
        if text.contains("desktop") || text.contains("mac") || text.contains("windows") { return "desktopcomputer" }
        return "globe"
    }

    private func load() async {
        do {
            sessions = try await session.api.sessions()
            failed = nil
        } catch {
            failed = (error as? LocalizedError)?.errorDescription ?? "Couldn't load your signed-in devices."
        }
    }

    private func revoke(_ item: AuthSession) async {
        busy = true
        defer { busy = false }
        do {
            try await session.api.revokeSession(item.id)
            session.toasts.show("Signed out \(item.description ?? "that device").", .success)
        } catch let error as APIError where error.isNotFound {
            // Already gone.
        } catch {
            session.toasts.error(error, fallback: "Couldn't sign that device out.")
        }
        await load()
    }
}

struct AboutView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var body: some View {
        GlassList {
            Section("Instance") {
                LabeledContent("Name", value: session.siteName)
                LabeledContent("Address", value: session.api.host)
                if let version = session.serverVersion {
                    LabeledContent("Server", value: version.serverVersion.split(separator: "+").first.map(String.init) ?? "")
                    LabeledContent("Extension contract", value: version.extensionApiVersion)
                }
            }
            .themedRow()

            Section {
                ForEach(session.extensions.installed) { item in
                    LabeledContent(item.name, value: item.version)
                }
                if session.extensions.installed.isEmpty {
                    Text("None installed.").foregroundStyle(theme.textSecondary)
                }
            } header: {
                Text("Extensions")
            } footer: {
                if !session.extensions.unsupported.isEmpty {
                    Text("\(session.extensions.unsupported.map(\.name).formatted(.list(type: .and))) "
                        + "\(session.extensions.unsupported.count == 1 ? "has" : "have") no screens of "
                        + "\(session.extensions.unsupported.count == 1 ? "its" : "their") own in this app. Their pages are "
                        + "shown from the web app, and what they add to projects, boards and tasks is drawn here where "
                        + "it is built from Solo's kit. Anything else they add is only in the web app.")
                }
            }
            .themedRow()

            Section {
                LabeledContent("App version", value: Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "")
                Link(destination: URL(string: "https://github.com/HunterAhlquist/Solo")!) {
                    Label("View Source on GitHub", systemImage: "chevron.left.forwardslash.chevron.right")
                }
            } footer: {
                Text("\(session.siteName), powered by Solo.")
            }
            .themedRow()
        }
        .themedList()
        .navigationTitle("About")
        .navigationBarTitleDisplayMode(.inline)
    }
}
