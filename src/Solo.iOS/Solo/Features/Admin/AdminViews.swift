import SwiftUI

/// Instance settings, for the super admin: what the instance is called, how
/// long it keeps its audit trail, and the way in to themes, email, users,
/// extensions and updates.
struct AdminSettingsView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State private var settings: InstanceSettings?
    @State private var displayName = ""
    @State private var tagline = ""
    @State private var retentionDays = 30
    @State private var failed: String?
    @FocusState private var retentionFocused: Bool

    private var brandingChanged: Bool {
        guard let settings else { return false }
        return displayName != (settings.displayName ?? "") || tagline != (settings.tagline ?? "")
    }

    var body: some View {
        Form {
            if let settings {
                Section {
                    TextField("Name", text: $displayName, prompt: Text("Solo"))
                    TextField("Tagline", text: $tagline, prompt: Text("Project management for game development."), axis: .vertical)
                    // Saved by the button rather than on leaving the field: a
                    // new name reaches everyone looking at the instance, which
                    // is not something to set off from a half-typed word.
                    Button("Save Branding") {
                        Task {
                            await save(UpdateInstanceSettingsRequest(
                                auditRetentionDays: settings.auditRetentionDays, themeId: settings.themeId,
                                displayName: String(displayName.prefix(FieldLimits.instanceName)),
                                tagline: String(tagline.prefix(FieldLimits.tagline)), version: settings.version))
                        }
                    }
                    .disabled(!brandingChanged)
                } header: {
                    Text("Branding")
                } footer: {
                    Text("What this instance is called: in the app, on the sign-in page and in the desktop app's list of "
                        + "instances. The tagline is the line under the name on the sign-in page. Leave either empty for the default.")
                }
                .themedRow()

                Section {
                    LabeledContent("Days") {
                        TextField("Days", value: $retentionDays, format: .number)
                            .keyboardType(.numberPad)
                            .multilineTextAlignment(.trailing)
                            .focused($retentionFocused)
                    }
                } header: {
                    Text("Audit retention")
                } footer: {
                    Text("Audit entries older than this are deleted automatically. Setup completed: \(settings.setupCompleted ? "yes" : "no").")
                }
                .themedRow()

                Section {
                    RouteLink(route: Route.adminUpdates) { Label("Version & Updates", systemImage: "arrow.down.circle") }
                    RouteLink(route: Route.adminThemes) { Label("Theme", systemImage: "paintpalette") }
                    RouteLink(route: Route.adminEmail) { Label("Email", systemImage: "envelope") }
                    RouteLink(route: Route.adminUsers) { Label("Users", systemImage: "person.2") }
                    RouteLink(route: Route.adminExtensions) { Label("Extensions", systemImage: "puzzlepiece.extension") }
                }
                .themedRow()

                AdminExtensionSettings()
            } else if let failed {
                Section { Text(failed).foregroundStyle(theme.error) }.themedRow()
            } else {
                Section { ProgressView() }.themedRow()
            }
        }
        .themedList()
        .navigationTitle("Admin Settings")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItemGroup(placement: .keyboard) {
                Spacer()
                Button("Done") { retentionFocused = false }
            }
        }
        .task { await load() }
        .onChange(of: retentionFocused) { _, focused in
            guard !focused, let settings else { return }
            let days = min(max(retentionDays, 1), 3650)
            retentionDays = days
            guard days != settings.auditRetentionDays else { return }
            Task {
                await save(UpdateInstanceSettingsRequest(auditRetentionDays: days, themeId: settings.themeId,
                                                         version: settings.version))
            }
        }
    }

    private func load() async {
        do {
            apply(try await session.api.instanceSettings())
        } catch let error as APIError where error.isForbidden {
            failed = "Only the instance super admin can change these settings."
        } catch {
            failed = (error as? LocalizedError)?.errorDescription ?? "Couldn't load instance settings."
        }
    }

    private func apply(_ fetched: InstanceSettings) {
        settings = fetched
        displayName = fetched.displayName ?? ""
        tagline = fetched.tagline ?? ""
        retentionDays = fetched.auditRetentionDays
    }

    private func save(_ request: UpdateInstanceSettingsRequest) async {
        do {
            apply(try await session.api.updateInstanceSettings(request))
            session.toasts.show("Saved.", .success)
        } catch let error as APIError where error.isConflict {
            session.toasts.show("Settings changed elsewhere. This page has been refreshed.", .info)
            await load()
        } catch {
            session.toasts.error(error, fallback: "That didn't save.")
            await load()
        }
    }
}

// MARK: - Themes

/// Which theme the whole instance wears. Light or dark stays each person's own choice.
struct AdminThemesView: View {
    @Environment(Session.self) private var session
    @Environment(ThemeStore.self) private var themes
    @Environment(\.theme) private var theme

    @State private var scan: ThemeScanResult?
    @State private var settings: InstanceSettings?
    @State private var rescanning = false

    var body: some View {
        List {
            if let document = themes.document, document.isFallback == true {
                Section {
                    Label("Theme '\(document.requestedThemeId ?? "?")' is selected for this instance but is not installed. "
                        + "Showing '\(document.name)' instead. Restore its package in the themes directory and rescan, "
                        + "or select another theme below.", systemImage: "exclamationmark.triangle")
                        .foregroundStyle(theme.warning)
                }
                .themedRow()
            }

            if let scan {
                Section {
                    ForEach(scan.themes) { item in
                        Button { Task { await select(item) } } label: {
                            HStack(spacing: 12) {
                                Image(systemName: item.isActive ? "largecircle.fill.circle" : "circle")
                                    .foregroundStyle(item.isActive ? theme.primary : theme.textDisabled)
                                VStack(alignment: .leading, spacing: 2) {
                                    HStack(spacing: 6) {
                                        Text(item.name).foregroundStyle(theme.textPrimary)
                                        if item.isBuiltIn {
                                            Text("built-in").font(.caption).foregroundStyle(theme.textSecondary)
                                        }
                                    }
                                    if let description = item.description {
                                        Text(description).font(.caption).foregroundStyle(theme.textSecondary)
                                            .multilineTextAlignment(.leading)
                                    }
                                }
                            }
                        }
                    }
                } footer: {
                    Text("Sets the theme for everyone on this instance. Each person can still choose light or dark for "
                        + "themselves. To add a theme, copy its .zip file into the instance's themes directory and rescan.")
                }
                .themedRow()

                if !scan.errors.isEmpty {
                    Section("Couldn't load") {
                        ForEach(scan.errors, id: \.file) { failure in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(failure.file).font(.footnote.weight(.semibold))
                                Text(failure.message).font(.caption).foregroundStyle(theme.textSecondary)
                            }
                        }
                    }
                    .themedRow()
                }
            } else {
                ProgressView().themedRow()
            }
        }
        .themedList()
        .navigationTitle("Theme")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Rescan", systemImage: "arrow.clockwise") {
                    Task {
                        rescanning = true
                        if let result = await session.save({ try await session.api.rescanThemes() }) {
                            scan = result
                            session.toasts.show("\(result.themes.count) theme(s) available.", .success)
                        }
                        rescanning = false
                    }
                }
                .disabled(rescanning)
            }
        }
        .task {
            scan = try? await session.api.themes()
            settings = try? await session.api.instanceSettings()
        }
    }

    private func select(_ item: ThemeSummary) async {
        guard !item.isActive, let settings else { return }
        let saved = await session.save({
            try await session.api.updateInstanceSettings(UpdateInstanceSettingsRequest(
                auditRetentionDays: settings.auditRetentionDays, themeId: item.id, version: settings.version))
        }, refresh: { self.settings = try? await session.api.instanceSettings() })
        guard let saved else { return }
        self.settings = saved
        scan = try? await session.api.themes()
        // The web app asks for a reload here; the app simply puts the new theme on.
        await themes.load(from: session.api)
    }
}

// MARK: - Users

struct AdminUsersView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @State private var users: [AdminUser]?
    @State private var confirmation: Confirmation?

    var body: some View {
        List {
            if let users {
                Section {
                    ForEach(users) { user in
                        let isLast = user.isSuperAdmin && users.count(where: \.isSuperAdmin) == 1
                        HStack(spacing: 12) {
                            UserAvatar(userId: user.id, name: user.name, hasPicture: user.avatarUrl?.nilIfBlank != nil, size: 34)
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(user.name).lineLimit(1)
                                    if user.isSuperAdmin {
                                        Badge(text: "Super admin", color: theme.accentWash, foreground: theme.primary)
                                    }
                                }
                                if user.displayName != nil {
                                    Text(user.email).font(.caption).foregroundStyle(theme.textSecondary).lineLimit(1)
                                }
                                Text("\(user.activeSessions) signed-in \(user.activeSessions == 1 ? "device" : "devices")")
                                    .font(.caption).foregroundStyle(theme.textSecondary)
                            }
                            Spacer(minLength: 0)
                            Menu("User actions", systemImage: "ellipsis") {
                                Button(user.isSuperAdmin ? "Remove Super Admin" : "Make Super Admin",
                                       systemImage: user.isSuperAdmin ? "shield.slash" : "shield") {
                                    confirmAdmin(user, !user.isSuperAdmin)
                                }
                                .disabled(isLast)
                                if isLast {
                                    Text("The instance must keep at least one super admin.")
                                }
                                Divider()
                                Button("Sign Out Everywhere", systemImage: "rectangle.portrait.and.arrow.right", role: .destructive) {
                                    confirmSignOut(user)
                                }
                            }
                            .labelStyle(.iconOnly)
                        }
                    }
                } footer: {
                    Text("Everyone who has signed in to this instance. A super admin can change instance settings, "
                        + "create projects and manage users.")
                }
                .themedRow()
            } else {
                ProgressView().themedRow()
            }
        }
        .themedList()
        .navigationTitle("Users")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
        .confirmation($confirmation)
    }

    private func load() async {
        do {
            users = try await session.api.adminUsers()
        } catch {
            users = users ?? []
            session.toasts.error(error, fallback: "Couldn't load users.")
        }
    }

    private func confirmAdmin(_ user: AdminUser, _ isSuperAdmin: Bool) {
        confirmation = Confirmation(
            title: isSuperAdmin ? "Make super admin?" : "Remove super admin?",
            message: isSuperAdmin
                ? "\(user.name) will be able to change instance settings, create projects, and manage users, including you."
                : "\(user.name) will no longer be able to change instance settings, create projects, or manage users.",
            action: isSuperAdmin ? "Make Super Admin" : "Remove") {
            await session.save { try await session.api.setSuperAdmin(user.id, isSuperAdmin) }
            await load()
        }
    }

    private func confirmSignOut(_ user: AdminUser) {
        confirmation = Confirmation(
            title: "Sign out everywhere?",
            message: "\(user.name) will be signed out of every device and will need to sign in again. "
                + "Credentials issued to them by extensions are revoked too.",
            action: "Sign Out Everywhere") {
            if let result = await session.save({ try await session.api.signOutEverywhere(user.id) }) {
                let revoked = result.sessions + result.extensions.reduce(0) { $0 + $1.count }
                session.toasts.show("Signed \(user.name) out everywhere: \(revoked) credential\(revoked == 1 ? "" : "s") revoked.",
                                    .success)
            }
            await load()
        }
    }
}

// MARK: - Email

/// The instance's outgoing mail. Saved by its button: a half-typed server name
/// shouldn't start sending anything.
struct AdminEmailView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State private var saved: EmailSettings?
    @State private var form = UpdateEmailSettingsRequest(enabled: false, smtpPort: 587, security: .startTls, version: 0)
    @State private var password = ""
    @State private var problem: String?
    @State private var busy = false

    private var changed: Bool {
        guard let saved else { return false }
        return form.enabled != saved.enabled || form.publicAddress?.nilIfBlank != saved.publicAddress?.nilIfBlank
            || form.fromName?.nilIfBlank != saved.fromName?.nilIfBlank
            || form.fromAddress?.nilIfBlank != saved.fromAddress?.nilIfBlank
            || form.smtpHost?.nilIfBlank != saved.smtpHost?.nilIfBlank || form.smtpPort != saved.smtpPort
            || form.security != saved.security || form.username?.nilIfBlank != saved.username?.nilIfBlank
            || !password.isEmpty
    }

    var body: some View {
        Form {
            if let problem {
                Section { Label(problem, systemImage: "exclamationmark.triangle").foregroundStyle(theme.error) }.themedRow()
            }
            if let saved {
                Section {
                    Toggle("Send email", isOn: $form.enabled)
                    field("Public address", \.publicAddress, prompt: "https://solo.example.com", keyboard: .URL)
                } footer: {
                    Text("Project invites, and each person's digest of their notifications. Nothing is sent until email "
                        + "is switched on here. The public address is what people use to reach this instance; links in emails point there.")
                }
                .themedRow()

                Section("Sender") {
                    field("Name", \.fromName, prompt: session.siteName)
                    field("Address", \.fromAddress, prompt: "solo@example.com", keyboard: .emailAddress)
                }
                .themedRow()

                Section("Server") {
                    field("SMTP server", \.smtpHost, prompt: "smtp.example.com", keyboard: .URL)
                    LabeledContent("Port") {
                        TextField("Port", value: $form.smtpPort, format: .number.grouping(.never))
                            .keyboardType(.numberPad)
                            .multilineTextAlignment(.trailing)
                    }
                    Picker("Security", selection: Binding(get: { form.security }, set: setSecurity)) {
                        ForEach(SmtpSecurity.allCases) { Text($0.title).tag($0) }
                    }
                    field("Username", \.username, prompt: "")
                    LabeledContent("Password") {
                        SecureField(saved.hasPassword ? "Saved" : "", text: $password)
                            .multilineTextAlignment(.trailing)
                            .disabled(saved.passwordFromEnvironment)
                    }
                    if saved.passwordFromEnvironment {
                        Text("Set by the server's environment.").font(.caption).foregroundStyle(theme.textSecondary)
                    }
                }
                .themedRow()

                Section {
                    Button("Send Test Email") { Task { await sendTest() } }
                    Button("Save Changes") { Task { await save() } }
                        .fontWeight(.semibold)
                        .disabled(!changed)
                }
                .disabled(busy)
                .themedRow()

                if !saved.recentFailures.isEmpty {
                    Section("Couldn't send") {
                        ForEach(saved.recentFailures, id: \.self) { failure in
                            VStack(alignment: .leading, spacing: 2) {
                                Text("\(failure.subject) · \(failure.to)").font(.footnote)
                                Text("\(failure.failedAt.stamp) · \(failure.error ?? "")")
                                    .font(.caption).foregroundStyle(theme.textSecondary)
                            }
                        }
                    }
                    .themedRow()
                }
            } else if problem == nil {
                Section { ProgressView() }.themedRow()
            }
        }
        .themedList()
        .navigationTitle("Email")
        .navigationBarTitleDisplayMode(.inline)
        .task {
            do {
                load(try await session.api.emailSettings())
                // Offered, not saved: where this app reaches the instance is the best first guess.
                if saved?.publicAddress == nil, session.api.baseURL.host() != "localhost" {
                    form.publicAddress = session.api.baseURL.absoluteString.trimmingCharacters(in: ["/"])
                }
            } catch {
                problem = "Couldn't load the email settings."
            }
        }
    }

    private func field(_ title: String, _ keyPath: WritableKeyPath<UpdateEmailSettingsRequest, String?>, prompt: String,
                       keyboard: UIKeyboardType = .default) -> some View {
        LabeledContent(title) {
            TextField(title, text: Binding(get: { form[keyPath: keyPath] ?? "" }, set: { form[keyPath: keyPath] = $0 }),
                      prompt: Text(prompt))
                .keyboardType(keyboard)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .multilineTextAlignment(.trailing)
        }
    }

    /// Changing how the connection is secured moves the port along with it, when the port was the old default.
    private func setSecurity(_ security: SmtpSecurity) {
        switch (security, form.smtpPort) {
        case (.sslOnConnect, 587), (.sslOnConnect, 25): form.smtpPort = 465
        case (.startTls, 465), (.startTls, 25): form.smtpPort = 587
        default: break
        }
        form.security = security
    }

    private func load(_ settings: EmailSettings) {
        saved = settings
        password = ""
        form = UpdateEmailSettingsRequest(
            enabled: settings.enabled, publicAddress: settings.publicAddress, fromName: settings.fromName,
            fromAddress: settings.fromAddress, smtpHost: settings.smtpHost, smtpPort: settings.smtpPort,
            security: settings.security, username: settings.username, version: settings.version)
    }

    private var request: UpdateEmailSettingsRequest {
        var request = form
        request.publicAddress = form.publicAddress?.nilIfBlank
        request.fromName = form.fromName?.nilIfBlank
        request.fromAddress = form.fromAddress?.nilIfBlank
        request.smtpHost = form.smtpHost?.nilIfBlank
        request.username = form.username?.nilIfBlank
        // Nil keeps the stored password; it is only sent when retyped.
        request.password = password.isEmpty ? nil : password
        request.version = saved?.version ?? 0
        return request
    }

    private func save() async {
        busy = true
        problem = nil
        defer { busy = false }
        do {
            load(try await session.api.updateEmailSettings(request))
            session.toasts.show("Saved.", .success)
        } catch let error as APIError where error.isConflict {
            problem = "Someone else changed these settings. They've been reloaded."
            if let fresh = try? await session.api.emailSettings() { load(fresh) }
        } catch {
            problem = (error as? LocalizedError)?.errorDescription ?? "Couldn't save the email settings."
        }
    }

    private func sendTest() async {
        busy = true
        problem = nil
        defer { busy = false }
        do {
            let result = try await session.api.sendTestEmail(request)
            session.toasts.show("Test email sent to \(result.sentTo).", .success)
        } catch let error as APIError {
            problem = error.title == "Couldn't send" ? "Couldn't send: \(error.detail ?? "")"
                : error.detail ?? "Couldn't send the test email."
        } catch {
            problem = "Couldn't send the test email."
        }
    }
}

// MARK: - Updates

struct AdminUpdatesView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @State private var status: UpdateStatus?
    @State private var checking = false
    @State private var checkError: String?

    var body: some View {
        List {
            Section("This instance") {
                LabeledContent("Version", value: running)
                LabeledContent("Extension contract", value: session.serverVersion?.extensionApiVersion ?? "—")
            }
            .themedRow()

            if let status {
                if !status.enabled {
                    Section { Text("Update checks are disabled on this instance (Updates__Enabled).").foregroundStyle(theme.textSecondary) }
                        .themedRow()
                } else if let update = status.server {
                    Section {
                        LabeledContent("Latest release") { Text(update.availableVersion).foregroundStyle(theme.info) }
                        if let published = update.published {
                            LabeledContent("Published", value: Day(published).formatted)
                        }
                        if let image = update.image {
                            Button {
                                UIPasteboard.general.string = image
                                session.toasts.show("Image reference copied.", .success)
                            } label: {
                                LabeledContent("Image") { Text(image).font(.caption.monospaced()).lineLimit(2) }
                            }
                        }
                        if let url = URL(string: update.releaseUrl) {
                            Link(destination: url) { Label("Release Notes", systemImage: "arrow.up.right.square") }
                        }
                    } footer: {
                        Text("Updating the server is an operator action: pull the new image and restart the instance.")
                    }
                    .themedRow()
                } else {
                    Section { Label("This instance is up to date.", systemImage: "checkmark.seal").foregroundStyle(theme.success) }
                        .themedRow()
                }

                if let error = status.error ?? checkError {
                    Section { Label("Couldn't check for updates: \(error)", systemImage: "info.circle") }.themedRow()
                }
                if let checkedAt = status.checkedAt {
                    Section { Text("Checked \(checkedAt.relative).").font(.footnote).foregroundStyle(theme.textSecondary) }
                        .themedRow()
                }
            } else if let checkError {
                Section { Label("Couldn't check for updates: \(checkError)", systemImage: "info.circle") }.themedRow()
            } else {
                ProgressView().themedRow()
            }
        }
        .themedList()
        .navigationTitle("Version & Updates")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button("Check Now", systemImage: "arrow.clockwise") { Task { await check(refresh: true) } }
                    .disabled(checking)
            }
        }
        .task { await check(refresh: false) }
    }

    private var running: String {
        session.serverVersion?.serverVersion.split(separator: "+").first.map(String.init) ?? "—"
    }

    private func check(refresh: Bool) async {
        checking = true
        defer { checking = false }
        do {
            status = try await session.api.updates(refresh: refresh)
            checkError = nil
            if refresh, let status {
                session.toasts.show(status.error.map { "Couldn't check for updates: \($0)" }
                    ?? status.server.map { "Solo \($0.availableVersion) is available." } ?? "This instance is up to date.",
                    status.error == nil ? .success : .warning)
            }
        } catch {
            checkError = (error as? LocalizedError)?.errorDescription ?? "Try again."
        }
    }
}

// MARK: - Extensions

/// What is installed, what failed to load, what can be updated, and — for an
/// extension that is no longer loaded — deleting the data it left behind.
struct AdminExtensionsView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State private var admin: ExtensionAdmin?
    @State private var updates: UpdateStatus?
    @State private var confirmation: Confirmation?
    @State private var prompt: TextPrompt?
    @State private var updating = false

    private var available: [ExtensionUpdate] { (updates?.extensions ?? []).filter(\.compatible) }

    var body: some View {
        List {
            if let admin {
                if let updates, !updates.enabled {
                    Section { Text("Update checks are disabled on this instance (Updates__Enabled).").font(.footnote) }.themedRow()
                } else if let error = updates?.error {
                    Section { Label("Couldn't check for updates: \(error)", systemImage: "info.circle").font(.footnote) }.themedRow()
                }

                Section {
                    if admin.extensions.isEmpty {
                        Text("No extensions installed.").foregroundStyle(theme.textSecondary)
                    }
                    ForEach(admin.extensions) { item in
                        row(item)
                    }
                } footer: {
                    Text("Extension packages are installed in the extensions directory. Installing, updating, or removing "
                        + "one requires a restart; extensions cannot be rescanned.")
                }
                .themedRow()

                if !admin.failures.isEmpty {
                    Section("Couldn't load") {
                        ForEach(admin.failures, id: \.self) { failure in
                            VStack(alignment: .leading, spacing: 2) {
                                Text(failure.source).font(.footnote.weight(.semibold))
                                Text(failure.reason).font(.caption).foregroundStyle(theme.textSecondary)
                            }
                        }
                    }
                    .themedRow()
                }
            } else {
                ProgressView().themedRow()
            }
        }
        .themedList()
        .navigationTitle("Extensions")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            if available.count > 1 {
                ToolbarItem(placement: .primaryAction) {
                    Button(updating ? "Updating…" : "Update All (\(available.count))") { confirmUpdate(available) }
                        .disabled(updating)
                }
            }
        }
        .task { await load() }
        .refreshable { await load() }
        .confirmation($confirmation)
        .textPrompt($prompt)
    }

    @ViewBuilder
    private func row(_ item: ExtensionRegistration) -> some View {
        let update = updates?.extensions.first { $0.extensionId == item.extensionId }
        VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 8) {
                Text(item.displayName ?? item.extensionId).font(.subheadline.weight(.semibold))
                if let version = item.version {
                    Text(version).font(.caption.monospaced()).foregroundStyle(theme.textSecondary)
                }
                Spacer()
                Badge(text: item.state, color: stateColor(item.state).opacity(0.22), foreground: theme.textPrimary)
            }
            Text(item.extensionId).font(.caption.monospaced()).foregroundStyle(theme.textSecondary)
            if let reason = item.failureReason {
                Text(reason).font(.caption).foregroundStyle(theme.error)
            }
            if !item.hooks.isEmpty {
                Text("Intercepts: \(item.hooks.joined(separator: ", "))").font(.caption).foregroundStyle(theme.textSecondary)
            }
            // The extension's own settings panel, where it has one built from the kit.
            KitContributions(seam: .extensionSettings, plugin: item.extensionId) { _ in EmptyView() }
            if let update {
                if update.compatible {
                    Text("Version \(update.availableVersion) is available (\(Self.size(update.sizeBytes))).")
                        .font(.caption).foregroundStyle(theme.info)
                    Button("Update and Restart") { confirmUpdate([update]) }
                        .buttonStyle(.glass)
                        .disabled(updating)
                } else {
                    Text("Version \(update.availableVersion) is available, but this instance can't load it: "
                        + (update.incompatibility ?? "it needs a newer Solo."))
                        .font(.caption).foregroundStyle(theme.warning)
                }
            }
            if item.canPurge {
                Button("Delete Data…", role: .destructive) { confirmPurge(item) }
                    .buttonStyle(.glass)
                    .tint(theme.error)
            }
        }
        .padding(.vertical, 4)
    }

    private func stateColor(_ state: String) -> Color {
        switch state.lowercased() {
        case "loaded": theme.success
        case "failed": theme.error
        default: theme.warning
        }
    }

    private static func size(_ bytes: Int64) -> String {
        bytes <= 0 ? "size unknown" : bytes.formatted(.byteCount(style: .file))
    }

    private func load() async {
        do {
            admin = try await session.api.extensionAdmin()
        } catch {
            admin = admin ?? ExtensionAdmin(extensions: [], failures: [])
            session.toasts.error(error, fallback: "Couldn't load extensions.")
        }
        updates = try? await session.api.updates()
    }

    /// There is no way to apply an extension update without a restart, and the
    /// restart depends on something outside Solo bringing the instance back —
    /// so the confirmation says so.
    private func confirmUpdate(_ items: [ExtensionUpdate]) {
        let names = items.map { "\($0.displayName ?? $0.extensionId) \($0.availableVersion)" }.formatted(.list(type: .and))
        confirmation = Confirmation(
            title: items.count == 1 ? "Update \(items[0].displayName ?? items[0].extensionId)?" : "Update \(items.count) extensions?",
            message: "This downloads \(names) (\(Self.size(items.reduce(0) { $0 + $1.sizeBytes }))), replaces what is "
                + "installed, and then restarts this instance — extensions are only loaded at startup, so the restart is "
                + "how the update takes effect. Everyone signed in is disconnected for as long as that takes, and if this "
                + "host has no restart policy the instance stays down until it is started again.",
            action: "Update and Restart") {
            updating = true
            defer { updating = false }
            guard let result = await session.save({ try await session.api.updateExtensions(items.map(\.extensionId)) })
            else { return }
            let failures = result.failed.map { "\($0.extensionId) wasn't updated: \($0.reason)" }.joined(separator: " ")
            if result.installed.isEmpty {
                session.toasts.show(failures, .error)
            } else {
                let installed = result.installed.map { "\($0.extensionId) \($0.version)" }.formatted(.list(type: .and))
                session.toasts.show("\(installed) installed. \(failures)", failures.isEmpty ? .success : .warning)
                if result.restarting {
                    await session.awaitRestart()
                }
            }
            await load()
        }
    }

    /// Confirmed by retyping the id, because there is no undo for a dropped schema.
    private func confirmPurge(_ item: ExtensionRegistration) {
        confirmation = Confirmation(
            title: "Delete \(item.displayName ?? item.extensionId) data",
            message: "This permanently deletes schema \(item.schemaName ?? "(none)") and every row in it, and removes this "
                + "extension's custom fields from tasks, epics, and boards. To keep a backup, run "
                + "pg_dump -n \(item.schemaName ?? "<schema>") first.",
            action: "Continue") {
            prompt = TextPrompt(title: "Type \(item.extensionId) to confirm", placeholder: "Extension ID",
                                action: "Delete Data", limit: 64) { typed in
                guard typed == item.extensionId else {
                    session.toasts.show("That didn't match \(item.extensionId). Nothing was deleted.", .warning)
                    return
                }
                if await session.perform({ try await session.api.purgeExtension(item.extensionId) }) {
                    session.toasts.show("\(item.extensionId) data deleted.", .success)
                }
                await load()
            }
        }
    }
}
