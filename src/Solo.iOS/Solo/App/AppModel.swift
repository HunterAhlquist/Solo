import SwiftUI

/// Where the app is between launch and a signed-in session. One instance at a
/// time, as on the desktop: changing instance means signing out of this one.
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

    private static let instanceKey = "solo.instance.url"
    private static let recentsKey = "solo.instance.recents"

    private(set) var phase: Phase = .launching
    private(set) var instanceURL: URL?
    private(set) var config: AuthConfig?
    private(set) var session: Session?
    private(set) var api: APIClient?
    /// Why the last session ended, shown once on the sign-in screen.
    var notice: String?
    private(set) var recentInstances: [String]

    let themes = ThemeStore()

    init() {
        recentInstances = UserDefaults.standard.stringArray(forKey: Self.recentsKey) ?? []
    }

    // MARK: Launch

    func launch() async {
        #if DEBUG
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
        #endif

        guard let stored = UserDefaults.standard.string(forKey: Self.instanceKey), let url = URL(string: stored) else {
            phase = .needsInstance
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
        do {
            let config = try await api.authConfig()
            self.api = api
            self.config = config
            instanceURL = url
            UserDefaults.standard.set(url.absoluteString, forKey: Self.instanceKey)
            remember(url)
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
        self.session = session
        phase = .signedIn
        await session.start()
    }

    /// Signing out revokes the session on the instance, then forgets it here regardless.
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
        api.token = nil
        api.onUnauthorized = nil
        session = nil
        // The cache holds what this person could see; the next one starts clean.
        await api.clearCache()
    }

    /// Leaves this instance for another. Signs out first if signed in.
    func changeInstance() async {
        if phase == .signedIn {
            await signOut()
        }
        UserDefaults.standard.removeObject(forKey: Self.instanceKey)
        api = nil
        config = nil
        instanceURL = nil
        themes.apply(nil)
        phase = .needsInstance
    }

    private func remember(_ url: URL) {
        let address = url.absoluteString
        recentInstances = [address] + recentInstances.filter { $0 != address }.prefix(4)
        UserDefaults.standard.set(recentInstances, forKey: Self.recentsKey)
    }

    func forgetRecent(_ address: String) {
        recentInstances.removeAll { $0 == address }
        UserDefaults.standard.set(recentInstances, forKey: Self.recentsKey)
    }
}
