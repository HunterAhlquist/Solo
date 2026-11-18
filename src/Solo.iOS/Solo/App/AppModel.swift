import SwiftUI
import WidgetKit

/// An instance this device knows: where it is, and what was last heard of it.
nonisolated struct SavedInstance: Codable, Identifiable, Hashable, Sendable {
    var url: URL
    /// What the instance calls itself, as of the last time it was asked.
    var name: String
    /// Who is signed in to it here, or nil when nobody is.
    var signedInAs: String?

    var id: URL { url }

    /// The address as a person would say it: the host, and the port where it isn't the usual one.
    var address: String {
        let host = url.host() ?? url.absoluteString
        return url.port.map { "\(host):\($0)" } ?? host
    }
}

/// Where the app is between launch and a signed-in session. Any number of
/// instances are kept, each with its own session, and one of them is showing:
/// going to another leaves the first signed in, to come back to.
@Observable
final class AppModel {
    enum Phase: Equatable {
        case launching
        /// No instance chosen yet, or the person asked to change it.
        case needsInstance
        /// The instance answered but nobody has completed its first-run setup.
        case setupRequired
        case signedOut
        case signedIn
        /// The instance didn't answer and nothing is cached to show instead.
        case unreachable(String)
        /// The instance is on the local network, and Solo has been refused the local network.
        case localNetworkDenied
    }

    /// The instance showing, by address.
    private static let instanceKey = "solo.instance.url"
    private static let instancesKey = "solo.instances"
    private static let recentsKey = "solo.instance.recents"

    private(set) var phase: Phase = .launching
    private(set) var instanceURL: URL?
    private(set) var config: AuthConfig?
    private(set) var session: Session?
    private(set) var api: APIClient?
    /// Why the last session ended, shown once on the sign-in screen.
    var notice: String?
    /// Every instance kept on this device, in the order they were added.
    private(set) var instances: [SavedInstance]
    /// Unread notifications on the instances that aren't showing, as of the last time each was asked.
    private(set) var unreadElsewhere: [URL: Int] = [:]
    /// True while the sheet that adds an instance is up. One sheet, on the main window, for
    /// every place that offers it.
    var addingInstance = false
    /// Addresses connected to before and since removed, offered again when adding one.
    private(set) var recentInstances: [String]
    /// Where a widget asked to go before there was a session to take it there.
    private var pendingLink: WidgetLink.Destination?
    #if os(macOS)
    /// True across a change of instance made from the Settings window's Instances page.
    @ObservationIgnored private var settingsOnInstances = false
    #endif

    let themes = ThemeStore()

    /// The instances kept on this device, for what runs without the app's own model: a
    /// background refresh reads each of them (`BackgroundSync`).
    static func storedInstances() -> [SavedInstance] {
        UserDefaults.standard.data(forKey: instancesKey)
            .flatMap { try? JSONDecoder().decode([SavedInstance].self, from: $0) } ?? []
    }

    init() {
        recentInstances = UserDefaults.standard.stringArray(forKey: Self.recentsKey) ?? []
        let stored = UserDefaults.standard.data(forKey: Self.instancesKey)
            .flatMap { try? JSONDecoder().decode([SavedInstance].self, from: $0) }
        let showing = UserDefaults.standard.string(forKey: Self.instanceKey).flatMap(URL.init)
        if let stored {
            instances = stored
        } else if let showing {
            // From before there was a list: the one instance the app knew is its first entry.
            instances = [SavedInstance(url: showing, name: showing.host() ?? showing.absoluteString,
                                       signedInAs: Keychain.read(account: showing.absoluteString) == nil ? nil : "")]
        } else {
            instances = []
        }
        themes.use(showing)
        DeviceNotifications.shared.deliver = { [weak self] tap in self?.take(tap) ?? false }
    }

    /// A notification on the device was tapped. With no session to follow it with yet — a
    /// tap that launched the app — it is kept, under the instance's address as the app
    /// spells it, until there is one.
    private func take(_ tap: DeviceNotifications.Tap) -> Bool {
        // An instance since removed from the list just opens the app.
        let address = DeviceNotifications.address(of: tap.instance)
        guard let instance = instances.first(where: { DeviceNotifications.address(of: $0.url) == address })?.url else {
            return true
        }
        var tap = tap
        tap.instance = instance
        if instance == instanceURL {
            guard let session, phase == .signedIn else {
                DeviceNotifications.shared.hold(tap)
                return true
            }
            Task { await session.open(tap) }
        } else {
            // Another instance's: the app turns to it first, and it is followed from there.
            DeviceNotifications.shared.hold(tap)
            Task { await switchTo(instance) }
        }
        return true
    }

    /// The saved entry for the instance showing, when it has one: an address that has never
    /// answered isn't kept.
    var current: SavedInstance? {
        instances.first { $0.url == instanceURL }
    }

    var otherInstances: [SavedInstance] {
        instances.filter { $0.url != instanceURL }
    }

    /// What to call an instance where only its name is shown. Two can go by one name —
    /// production and staging, say — and then the address is what tells them apart.
    func label(for instance: SavedInstance) -> String {
        instances.contains { $0.url != instance.url && $0.name == instance.name }
            ? "\(instance.name) (\(instance.address))" : instance.name
    }

    // MARK: Launch

    func launch() async {
        #if DEBUG || PERF
        // Simulator verification without a provider round-trip: a session
        // minted straight into a development database is handed in through
        // the environment. Compiled out of release builds.
        let environment = ProcessInfo.processInfo.environment
        if let address = environment["SOLO_INSTANCE_URL"], let url = Self.normalize(address) {
            UserDefaults.standard.set(url.absoluteString, forKey: Self.instanceKey)
            if let token = environment["SOLO_SESSION_TOKEN"] {
                Keychain.write(token, account: url.absoluteString)
            }
        }
        // A second instance to switch to, signed in the same way.
        if let address = environment["SOLO_OTHER_INSTANCE_URL"], let url = Self.normalize(address),
           let token = environment["SOLO_OTHER_SESSION_TOKEN"] {
            Keychain.write(token, account: url.absoluteString)
            if !instances.contains(where: { $0.url == url }) {
                instances.append(SavedInstance(url: url, name: url.host() ?? address, signedInAs: ""))
                saveInstances()
            }
        }
        #endif

        guard let stored = UserDefaults.standard.string(forKey: Self.instanceKey), let url = URL(string: stored) else {
            // Nothing was showing, but something may be kept: an instance was removed and the app quit.
            if let first = instances.first {
                await connect(to: first.url)
            } else {
                phase = .needsInstance
            }
            return
        }
        await connect(to: url)
    }

    /// Turns what someone typed into an instance address: a scheme is added
    /// when missing (http for a bare host on the local network, https otherwise).
    static func normalize(_ input: String) -> URL? {
        var text = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty else { return nil }
        if !text.contains("://") {
            let host = text.split(separator: "/").first.map(String.init) ?? text
            let name = host.split(separator: ":").first.map(String.init) ?? host
            let isLocal = name == "localhost" || name.hasSuffix(".local") || !name.contains(".")
                || name.wholeMatch(of: /\d{1,3}(\.\d{1,3}){3}/) != nil
            text = (isLocal ? "http://" : "https://") + text
        }
        while text.hasSuffix("/") { text.removeLast() }
        guard let url = URL(string: text), url.host() != nil, ["http", "https"].contains(url.scheme) else { return nil }
        return url
    }

    /// Reads the instance's bootstrap config, then resumes a stored session if there is one.
    func connect(to url: URL) async {
        await connect(to: url, askedAlready: false)
    }

    /// `askedAlready` is true for the second try, made once the system's question about the local
    /// network has been answered: that one's failure is the answer, not another question.
    private func connect(to url: URL, askedAlready: Bool) async {
        phase = .launching
        let api = APIClient(baseURL: url)
        // Its own theme from the last time it was shown, while it is asked for the one it has now.
        themes.use(url)
        do {
            let config = try await api.authConfig()
            self.api = api
            self.config = config
            instanceURL = url
            UserDefaults.standard.set(url.absoluteString, forKey: Self.instanceKey)
            keep(url, name: config.siteName)
            await themes.load(from: api)

            guard config.setupCompleted else {
                phase = .setupRequired
                return
            }
            if let token = Keychain.read(account: url.absoluteString) {
                api.token = token
                do {
                    try await begin(api: api, config: config, user: try await api.currentUser())
                    return
                } catch let error as APIError where error.isUnauthorized || error.isForbidden {
                    Keychain.delete(account: url.absoluteString)
                    api.token = nil
                    signedIn(url, as: nil)
                    notice = "Your session ended. Sign in again."
                } catch {
                    await failed(url, (error as? LocalizedError)?.errorDescription ?? "Couldn't reach the instance.",
                                 askedAlready: askedAlready)
                    return
                }
            }
            phase = .signedOut
        } catch {
            instanceURL = url
            await failed(url, (error as? LocalizedError)?.errorDescription ?? "Couldn't reach \(url.host() ?? "the instance").",
                         askedAlready: askedAlready)
        }
    }

    /// The instance couldn't be reached. Before saying so: the first connection to an address on
    /// the local network fails by design, while the system asks whether Solo may use the local
    /// network at all. That failure isn't shown — the answer is waited for, and the connection
    /// made again. Only a refusal, or a failure that had nothing to do with the question, is.
    private func failed(_ url: URL, _ message: String, askedAlready: Bool) async {
        if !askedAlready, await LocalNetworkAccess.alertCameAndWent() {
            await connect(to: url, askedAlready: true)
            return
        }
        phase = await LocalNetworkAccess.isDenied(for: url) ? .localNetworkDenied : .unreachable(message)
    }

    func retry() async {
        if let instanceURL {
            await connect(to: instanceURL)
        } else {
            phase = .needsInstance
        }
    }

    // MARK: Sign-in

    /// Runs the system sign-in sheet. Returns quietly when the person closes it.
    func signIn() async throws {
        guard let api, let config, let instanceURL else { return }
        guard let grant = try await SignInFlow(api: api).run() else { return }
        Keychain.write(grant.token, account: instanceURL.absoluteString)
        api.token = grant.token
        notice = nil
        try await begin(api: api, config: config, user: grant.user)
    }

    private func begin(api: APIClient, config: AuthConfig, user: User) async throws {
        let session = Session(api: api, config: config, user: user)
        api.onUnauthorized = { [weak self] in
            Task { await self?.sessionEnded() }
        }
        #if os(macOS)
        // The Settings window is where a Mac changes instance: it stays on that page across the change.
        if settingsOnInstances {
            settingsOnInstances = false
            session.settingsNavigator[.account] = [.instances]
        }
        #endif
        self.session = session
        phase = .signedIn
        if let instanceURL {
            signedIn(instanceURL, as: user.name)
            unreadElsewhere[instanceURL] = nil
        }
        publishToWidgets()
        if let pendingLink {
            self.pendingLink = nil
            follow(pendingLink)
        }
        if let instanceURL, let tap = DeviceNotifications.shared.takeTap(for: instanceURL) {
            Task { await session.open(tap) }
        }
        await session.start()
    }

    // MARK: Widgets

    /// Tells the widgets which instance to show and hands them a copy of the session to ask it
    /// with — or, signed out, takes both away. Called whenever either changes, and when the
    /// theme does, since the widgets wear its accent.
    func publishToWidgets() {
        // The widgets are an iPhone's and an iPad's: a Mac's build has none to tell.
        #if os(iOS)
        if let instanceURL, let token = api?.token, session != nil {
            let palette = themes.document
            WidgetStore.instance = WidgetInstance(
                url: instanceURL, name: config?.siteName ?? instanceURL.host() ?? "Solo",
                accentLight: CSSColor(palette?.light?.primary ?? "")?.hex,
                accentDark: CSSColor(palette?.dark?.primary ?? "")?.hex)
            if WidgetStore.token != token { WidgetStore.token = token }
        } else {
            WidgetStore.token = nil
            if instanceURL == nil { WidgetStore.instance = nil }
        }
        WidgetCenter.shared.reloadAllTimelines()
        #endif
    }

    /// A widget was tapped. Before sign-in has finished there is nowhere to go yet, so the
    /// link is kept until there is.
    func open(_ url: URL) {
        guard let destination = WidgetLink.parse(url) else { return }
        if session != nil { follow(destination) } else { pendingLink = destination }
    }

    private func follow(_ destination: WidgetLink.Destination) {
        guard let navigator = session?.navigator else { return }
        switch destination {
        case .yourTasks:
            navigator.popToRoot(.work)
        case .task(let serial):
            if let reference = TaskReference(serial: serial) { navigator.open(reference) }
        }
    }

    /// Signing out revokes the session on the instance, then forgets it here regardless. The
    /// instance stays in the list, signed out.
    func signOut() async {
        guard let api, let instanceURL else { return }
        await session?.stop()
        try? await api.send("DELETE", "api/auth/sessions/current")
        await endSession(instanceURL: instanceURL, api: api)
        phase = .signedOut
    }

    /// A request came back 401: the session was revoked from another device, or expired.
    private func sessionEnded() async {
        guard phase == .signedIn, let api, let instanceURL else { return }
        await session?.stop()
        await endSession(instanceURL: instanceURL, api: api)
        notice = "You were signed out. Sign in again to continue."
        phase = .signedOut
    }

    private func endSession(instanceURL: URL, api: APIClient) async {
        Keychain.delete(account: instanceURL.absoluteString)
        // Its reminders were this person's, and so was what the device had announced.
        DeviceNotifications.forget(instanceURL)
        api.token = nil
        api.onUnauthorized = nil
        session = nil
        signedIn(instanceURL, as: nil)
        publishToWidgets()
        // The cache holds what this person could see; the next one starts clean.
        await api.clearCache()
    }

    // MARK: Instances

    /// Shows another instance. The one that was showing stays signed in, and is as it was
    /// left the next time it is shown; only its live connection is put down.
    func switchTo(_ url: URL) async {
        guard url != instanceURL || phase != .signedIn else { return }
        await leave()
        await connect(to: url)
        Task { await refreshElsewhere() }
    }

    /// Puts down the instance showing without signing out of it.
    private func leave() async {
        #if os(macOS)
        // With nobody signed in, that page is all the window has.
        settingsOnInstances = session.map { $0.settingsNavigator[.account].first == .instances } ?? true
        #endif
        if let session {
            // What it had unread is the last that is known of it once it isn't showing.
            if let instanceURL { unreadElsewhere[instanceURL] = session.notifications.unreadCount }
            await session.stop()
        }
        api?.onUnauthorized = nil
        session = nil
        api = nil
        config = nil
        notice = nil
    }

    /// Forgets an instance: its session is revoked there and dropped here, with everything
    /// kept of it. If it was the one showing, another takes its place, or the app asks for one.
    func remove(_ url: URL) async {
        let showing = url == instanceURL
        if showing {
            await leave()
        }
        if let token = Keychain.read(account: url.absoluteString) {
            let api = APIClient(baseURL: url, token: token)
            try? await api.send("DELETE", "api/auth/sessions/current")
            Keychain.delete(account: url.absoluteString)
            await api.clearCache()
        }
        instances.removeAll { $0.url == url }
        saveInstances()
        unreadElsewhere[url] = nil
        themes.forget(url)
        DeviceNotifications.forget(url)
        // Still offered when adding one: an instance removed by mistake is one tap from back.
        recentInstances = [url.absoluteString] + recentInstances.filter { $0 != url.absoluteString }.prefix(4)
        UserDefaults.standard.set(recentInstances, forKey: Self.recentsKey)
        guard showing else { return }
        instanceURL = nil
        UserDefaults.standard.removeObject(forKey: Self.instanceKey)
        publishToWidgets()
        if let next = instances.first {
            await connect(to: next.url)
        } else {
            themes.use(nil)
            phase = .needsInstance
        }
    }

    /// Asks an address whether a Solo instance answers there, without leaving the one that is
    /// showing: what adding an instance does first, so that a mistyped address costs nothing.
    /// Returns what went wrong, or nil.
    func probe(_ url: URL) async -> String? {
        let api = APIClient(baseURL: url)
        do {
            _ = try await api.authConfig()
            return nil
        } catch {
            // The first connection to the local network fails while the system asks about it: see `failed`.
            if await LocalNetworkAccess.alertCameAndWent(), (try? await api.authConfig()) != nil {
                return nil
            }
            if await LocalNetworkAccess.isDenied(for: url) {
                return "Solo hasn't been allowed to use your local network. Turn on Local Network for Solo in Settings."
            }
            return (error as? LocalizedError)?.errorDescription ?? "Couldn't reach \(url.host() ?? "that address")."
        }
    }

    /// Asks the instances that aren't showing how much each has unread. They have no live
    /// connection, so this is as current as the last time it was asked: when the app comes
    /// forward, when the instance showing changes, and every few minutes between.
    func refreshElsewhere() async {
        for instance in otherInstances {
            guard let token = Keychain.read(account: instance.url.absoluteString) else {
                unreadElsewhere[instance.url] = nil
                continue
            }
            do {
                // Asked of the instance itself: a count from the cache would be a guess. What
                // is new there is announced on the device, and its reminders scheduled, as
                // they would be with it showing: it has no live connection to hear over.
                let inbox = try await DeviceNotifications.sync(instance.url, api: APIClient(baseURL: instance.url, token: token))
                unreadElsewhere[instance.url] = inbox.unreadCount
            } catch let error as APIError where error.isUnauthorized {
                // Its session ended while it wasn't showing.
                Keychain.delete(account: instance.url.absoluteString)
                DeviceNotifications.forget(instance.url)
                signedIn(instance.url, as: nil)
                unreadElsewhere[instance.url] = nil
            } catch {
                // Out of reach: what was last known stands.
            }
        }
    }

    /// Everything unread on the instances that aren't showing.
    var unreadElsewhereTotal: Int {
        otherInstances.reduce(0) { $0 + (unreadElsewhere[$1.url] ?? 0) }
    }

    /// Keeps an instance that answered, under the name it gave.
    private func keep(_ url: URL, name: String) {
        if let index = instances.firstIndex(where: { $0.url == url }) {
            guard instances[index].name != name else { return }
            instances[index].name = name
        } else {
            instances.append(SavedInstance(url: url, name: name))
            forgetRecent(url.absoluteString)
        }
        saveInstances()
    }

    private func signedIn(_ url: URL, as name: String?) {
        guard let index = instances.firstIndex(where: { $0.url == url }), instances[index].signedInAs != name else { return }
        instances[index].signedInAs = name
        saveInstances()
    }

    private func saveInstances() {
        UserDefaults.standard.set(try? JSONEncoder().encode(instances), forKey: Self.instancesKey)
    }

    /// The recent addresses worth offering: the ones that aren't kept already.
    var recentsToOffer: [String] {
        recentInstances.filter { address in !instances.contains { $0.url.absoluteString == address } }
    }

    func forgetRecent(_ address: String) {
        recentInstances.removeAll { $0 == address }
        UserDefaults.standard.set(recentInstances, forKey: Self.recentsKey)
    }
}
