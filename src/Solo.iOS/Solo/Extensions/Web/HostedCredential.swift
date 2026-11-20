import Foundation
import WebKit

/// What a page the app hosts is handed to sign in with, in place of the app's own session.
///
/// The app's session lasts a month and is renewed by being used. In a page it sat in storage
/// that the page's scripts can read, beside whatever an extension's page runs. The instance
/// issues a stand-in for it instead (`POST api/auth/sessions/hosted`): the same person, for
/// an hour, good for nothing but the page's own requests, and gone when the app's session is.
/// The app's own never leaves the app.
///
/// One is asked for when the first page opens and shared by every page showing. It is not
/// renewed by the page — it can't be — but by the app, ahead of time, which puts the new one
/// where the page reads it; a page that is up never notices. An instance from before any of
/// this answers that there is no such address, and its pages are handed the app's session as
/// they always were.
final class HostedCredential {
    nonisolated struct Grant: Decodable, Sendable {
        var token: String
        var expiresAt: Date
    }

    private final class Page {
        weak var view: WKWebView?
        let setup: HostedWeb.Setup
        init(_ view: WKWebView, _ setup: HostedWeb.Setup) { (self.view, self.setup) = (view, setup) }
    }

    private let api: APIClient
    private var held: (grant: Grant, since: Date)?
    private var unsupported = false
    private var pages: [Page] = []
    private var asking: Task<String?, Never>?
    private var renewal: Task<Void, Never>?

    init(api: APIClient) {
        self.api = api
    }

    // MARK: The rules, apart from the asking

    /// An instance that has no such address is one from before it, not one that refused.
    nonisolated static func isMissing(status: Int) -> Bool { status == 404 || status == 405 }

    /// How long after one is issued the next is asked for: three quarters of the way through
    /// it, so a slow answer or a missed turn still leaves the page one that works.
    nonisolated static func renewAfter(lifetime: TimeInterval) -> TimeInterval { max(20, lifetime * 0.75) }

    /// Whether one is worth handing to a page that is opening now: not if it is about to end.
    nonisolated static func worthHanding(expiresAt: Date, issued: Date, now: Date) -> Bool {
        expiresAt.timeIntervalSince(now) > min(120, expiresAt.timeIntervalSince(issued) / 4)
    }

    // MARK: For a page

    /// What to hand a page that is opening. Nil when the instance couldn't be asked, or
    /// wouldn't say: the page then has nothing to sign in with, and says it couldn't load.
    func token() async -> String? {
        if unsupported { return api.token }
        if let held, Self.worthHanding(expiresAt: held.grant.expiresAt, issued: held.since, now: .now) { return held.grant.token }
        return await ask()
    }

    /// A page that is up: when the next one comes, it is handed it.
    func track(_ view: WKWebView, _ setup: HostedWeb.Setup) {
        pages.removeAll { $0.view == nil || $0.view === view }
        pages.append(Page(view, setup))
    }

    /// A page said the instance turned away what it was handed. What is held is let go, unless
    /// it has only just been issued: several pages say so at once, and the first has seen to it.
    func refused() {
        if let held, Date.now.timeIntervalSince(held.since) > 10 { self.held = nil }
    }

    /// The app came forward. Nothing here ran while it was away, so the time to renew may
    /// have passed; the pages are seen to before they find out for themselves.
    func cameForward() {
        guard !unsupported, let held, !Self.worthHanding(expiresAt: held.grant.expiresAt, issued: held.since, now: .now) else { return }
        Task { await renew() }
    }

    func stop() {
        renewal?.cancel()
        asking?.cancel()
        (renewal, asking, held, pages) = (nil, nil, nil, [])
    }

    // MARK: Asking

    /// One request, however many pages are waiting on it.
    private func ask() async -> String? {
        if let asking { return await asking.value }
        let request = Task { [api] () -> String? in
            do {
                let grant: Grant = try await api.send("POST", "api/auth/sessions/hosted")
                guard !Task.isCancelled else { return nil }
                self.held = (grant, .now)
                self.schedule(after: Self.renewAfter(lifetime: grant.expiresAt.timeIntervalSinceNow))
                return grant.token
            } catch let error as APIError where Self.isMissing(status: error.status) {
                self.unsupported = true
                return api.token
            } catch {
                return nil
            }
        }
        asking = request
        defer { asking = nil }
        return await request.value
    }

    private func schedule(after wait: TimeInterval) {
        renewal?.cancel()
        renewal = Task { [weak self] in
            try? await Task.sleep(for: .seconds(wait))
            guard !Task.isCancelled else { return }
            await self?.renew()
        }
    }

    /// The next one, put where every page that is up reads it. With no page up there is
    /// nobody to hold one for: it is let go, and the next page to open asks afresh.
    private func renew() async {
        pages.removeAll { $0.view == nil }
        guard !pages.isEmpty else {
            held = nil
            return
        }
        guard let token = await ask() else {
            // Out of reach for now. The pages have the one they had; asked again shortly.
            schedule(after: 60)
            return
        }
        guard !unsupported else { return }
        for page in pages {
            if let view = page.view { HostedWeb.hand(token, to: view, page.setup) }
        }
    }
}
