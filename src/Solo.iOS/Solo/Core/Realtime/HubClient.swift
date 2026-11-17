import Foundation
import Observation
import SignalRClient

/// A list of listeners that forgets one when its subscription is released.
final class EventSource<Payload> {
    private var handlers: [UUID: (Payload) -> Void] = [:]

    func subscribe(_ handler: @escaping (Payload) -> Void) -> EventSubscription {
        let id = UUID()
        handlers[id] = handler
        return EventSubscription { [weak self] in self?.handlers[id] = nil }
    }

    func send(_ payload: Payload) {
        for handler in handlers.values {
            handler(payload)
        }
    }
}

/// Keeps a listener registered for as long as something holds it.
final class EventSubscription {
    private var cancelAction: (() -> Void)?

    init(_ cancel: @escaping () -> Void) {
        cancelAction = cancel
    }

    func cancel() {
        cancelAction?()
        cancelAction = nil
    }

    isolated deinit {
        cancelAction?()
    }
}

nonisolated struct TopicKey: Hashable, Sendable {
    var projectId: UUID
    var topic: String
}

/// The app's one live connection to `/hubs/board`: change events for the open
/// board, presence, and the topics core and extensions publish on.
///
/// What is joined and subscribed is remembered here rather than on the
/// connection, because a reconnect lands in a new connection id with empty
/// groups: everything is restated, and then the pages refetch.
@Observable
final class HubClient {
    enum Phase: Equatable {
        case idle, connecting, connected, reconnecting
    }

    private(set) var phase: Phase = .idle

    @ObservationIgnored let entityChanged = EventSource<EntityChangedEvent>()
    @ObservationIgnored let presenceChanged = EventSource<BoardPresenceSnapshot>()
    @ObservationIgnored let topicMessage = EventSource<TopicMessage>()
    @ObservationIgnored let topicPresenceChanged = EventSource<TopicPresenceSnapshot>()
    /// Fires after a reconnect has restated everything: whatever happened while away was missed.
    @ObservationIgnored let reconnected = EventSource<Void>()
    /// Fires when the connection is lost, which is often the first sign the instance is gone.
    @ObservationIgnored let dropped = EventSource<Void>()

    @ObservationIgnored private let api: APIClient
    @ObservationIgnored private var connection: HubConnection?
    @ObservationIgnored private var starting: Task<Void, Error>?
    @ObservationIgnored private var retry: Task<Void, Never>?
    @ObservationIgnored private var generation = 0
    @ObservationIgnored private var wanted = false

    @ObservationIgnored private var boardId: UUID?
    @ObservationIgnored private var presence: (entityType: String, entityId: UUID, mode: PresenceMode)?
    @ObservationIgnored private var topics: [TopicKey: (holders: Int, mode: PresenceMode)] = [:]
    @ObservationIgnored private var userTopics: [String: Int] = [:]

    init(api: APIClient) {
        self.api = api
    }

    var isConnected: Bool { phase == .connected }

    // MARK: Board

    func joinBoard(_ id: UUID) async {
        let previous = boardId
        boardId = id
        presence = nil
        guard await ensureStarted(), previous != id else { return }
        if let previous {
            await invoke("LeaveBoard", previous.api)
        }
        await invoke("JoinBoard", id.api)
    }

    func leaveBoard(_ id: UUID) async {
        guard boardId == id else { return }
        boardId = nil
        presence = nil
        guard isConnected else { return }
        // Clear presence before leaving, so the others see the avatar go
        // rather than waiting out a disconnect timeout.
        await invoke("SetPresence", EntityTypes.board, id.api, PresenceMode.none.rawValue)
        await invoke("LeaveBoard", id.api)
    }

    func setPresence(_ entityType: String, _ entityId: UUID, _ mode: PresenceMode) async {
        presence = mode == .none ? nil : (entityType, entityId, mode)
        guard isConnected else { return }
        await invoke("SetPresence", entityType, entityId.api, mode.rawValue)
    }

    // MARK: Topics

    /// Counted: a page and its sidebar section can hold the same topic, and it
    /// stays subscribed until the last of them lets go.
    func subscribe(projectId: UUID, topic: String) async {
        let key = TopicKey(projectId: projectId, topic: topic)
        if let held = topics[key] {
            topics[key] = (held.holders + 1, held.mode)
            return
        }
        // Held before the call, so a connection that comes up later still subscribes it.
        topics[key] = (1, .none)
        guard await ensureStarted() else { return }
        if await !invoke("SubscribeTopic", projectId.api, topic) {
            topics[key] = nil
        }
    }

    func unsubscribe(projectId: UUID, topic: String) async {
        let key = TopicKey(projectId: projectId, topic: topic)
        guard let held = topics[key] else { return }
        if held.holders > 1 {
            topics[key] = (held.holders - 1, held.mode)
            return
        }
        topics[key] = nil
        guard isConnected else { return }
        await invoke("UnsubscribeTopic", projectId.api, topic)
    }

    func setTopicPresence(projectId: UUID, topic: String, _ mode: PresenceMode) async {
        let key = TopicKey(projectId: projectId, topic: topic)
        guard let held = topics[key] else { return }
        topics[key] = (held.holders, mode)
        guard isConnected else { return }
        await invoke("SetTopicPresence", projectId.api, topic, mode.rawValue)
    }

    func subscribeUserTopic(_ topic: String) async {
        if let holders = userTopics[topic] {
            userTopics[topic] = holders + 1
            return
        }
        userTopics[topic] = 1
        guard await ensureStarted() else { return }
        if await !invoke("SubscribeUserTopic", topic) {
            userTopics[topic] = nil
        }
    }

    func unsubscribeUserTopic(_ topic: String) async {
        guard let holders = userTopics[topic] else { return }
        if holders > 1 {
            userTopics[topic] = holders - 1
            return
        }
        userTopics[topic] = nil
        guard isConnected else { return }
        await invoke("UnsubscribeUserTopic", topic)
    }

    /// Sends to the owner of a held project topic. Accepted does not imply a persisted edit.
    func sendTopicMessage(projectId: UUID, topic: String, kind: String, data: String?) async -> LiveSendResult {
        guard let connection, isConnected else { return .notConnected }
        do {
            let raw: Int = try await connection.invoke(
                method: "SendTopicMessage", arguments: projectId.api, topic, kind, data ?? NSNull() as Any)
            return LiveSendResult(rawValue: raw) ?? .failed
        } catch {
            return isConnected ? .failed : .notConnected
        }
    }

    // MARK: Lifecycle

    /// Drops the connection and everything held on it. Signing out, or leaving the instance.
    func shutDown() async {
        wanted = false
        generation += 1
        retry?.cancel()
        retry = nil
        starting = nil
        boardId = nil
        presence = nil
        topics = [:]
        userTopics = [:]
        let old = connection
        connection = nil
        api.connectionId = nil
        phase = .idle
        await old?.stop()
    }

    /// The app came back to the foreground: iOS suspends sockets behind it, so
    /// the connection may be dead without having said so.
    func resume() {
        guard wanted, phase != .connected, phase != .connecting else { return }
        scheduleRetry(after: 0)
    }

    @discardableResult
    private func ensureStarted() async -> Bool {
        wanted = true
        if isConnected { return true }
        do {
            try await start()
            return true
        } catch {
            scheduleRetry(after: 3)
            return false
        }
    }

    private func start() async throws {
        if let starting {
            return try await starting.value
        }
        let task = Task { try await connect() }
        starting = task
        defer { starting = nil }
        try await task.value
    }

    /// Negotiates by hand and then connects the WebSocket to that negotiation.
    /// The library can negotiate for itself, but it keeps the connection id it
    /// learns to itself, and REST calls need it: the server leaves the sender
    /// out of the change broadcast for its own save, so nothing echoes back.
    private func connect() async throws {
        generation += 1
        let mine = generation
        if phase != .reconnecting {
            phase = .connecting
        }

        do {
            let negotiation = try await negotiate()
            var options = HttpConnectionOptions()
            options.skipNegotiation = true
            options.transport = .webSockets
            options.logLevel = .warning
            // Asked again for every connect: a ticket is single-use, minted
            // just before it is spent, and never the sign-in itself.
            options.accessTokenFactory = { [api] in try await api.hubTicket() }

            var hub = URLComponents(url: api.url("hubs/board"), resolvingAgainstBaseURL: false)!
            hub.queryItems = [URLQueryItem(name: "id", value: negotiation.connectionToken ?? negotiation.connectionId)]
            let connection = HubConnectionBuilder()
                .withUrl(url: hub.url!.absoluteString, options: options)
                .withLogLevel(logLevel: .warning)
                .build()

            await connection.on("EntityChanged") { @MainActor [weak self] (event: EntityChangedEvent) in
                self?.entityChanged.send(event)
            }
            await connection.on("PresenceChanged") { @MainActor [weak self] (snapshot: BoardPresenceSnapshot) in
                self?.presenceChanged.send(snapshot)
            }
            await connection.on("TopicMessage") { @MainActor [weak self] (message: TopicMessage) in
                self?.topicMessage.send(message)
            }
            await connection.on("TopicPresenceChanged") { @MainActor [weak self] (snapshot: TopicPresenceSnapshot) in
                self?.topicPresenceChanged.send(snapshot)
            }
            await connection.onClosed { @MainActor [weak self] _ in
                self?.connectionClosed(generation: mine)
            }

            try await connection.start()
            guard mine == generation, wanted else {
                await connection.stop()
                return
            }

            self.connection = connection
            api.connectionId = negotiation.connectionId
            let wasReconnect = phase == .reconnecting
            phase = .connected
            await restore()
            if wasReconnect {
                reconnected.send(())
            }
        } catch {
            if mine == generation, phase == .connecting {
                phase = .idle
            }
            throw error
        }
    }

    private func negotiate() async throws -> Negotiation {
        var request = api.request("POST", "hubs/board/negotiate?negotiateVersion=1")
        request.setValue("Bearer \(try await api.hubTicket())", forHTTPHeaderField: "Authorization")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else {
            throw APIError(status: (response as? HTTPURLResponse)?.statusCode ?? 0, title: "Live updates are unavailable")
        }
        return try SoloJSON.decoder.decode(Negotiation.self, from: data)
    }

    private func connectionClosed(generation closed: Int) {
        guard closed == generation, wanted else { return }
        connection = nil
        api.connectionId = nil
        phase = .reconnecting
        dropped.send(())
        scheduleRetry(after: 1)
    }

    private func scheduleRetry(after delay: TimeInterval) {
        guard retry == nil else { return }
        retry = Task { [weak self] in
            var wait = delay
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(wait))
                guard let self, self.wanted, !Task.isCancelled else { return }
                if (try? await self.start()) != nil, self.isConnected {
                    break
                }
                wait = min(max(wait, 1) * 2, 15)
            }
            self?.retry = nil
        }
    }

    private func restore() async {
        if let boardId {
            await invoke("JoinBoard", boardId.api)
            if let presence {
                await invoke("SetPresence", presence.entityType, presence.entityId.api, presence.mode.rawValue)
            }
        }
        for (key, held) in topics {
            // Access lost while away, say: that topic stays quiet, the rest carry on.
            guard await invoke("SubscribeTopic", key.projectId.api, key.topic) else {
                topics[key] = nil
                continue
            }
            if held.mode != .none {
                await invoke("SetTopicPresence", key.projectId.api, key.topic, held.mode.rawValue)
            }
        }
        for topic in userTopics.keys {
            if await !invoke("SubscribeUserTopic", topic) {
                userTopics[topic] = nil
            }
        }
    }

    @discardableResult
    private func invoke(_ method: String, _ arguments: any Sendable...) async -> Bool {
        guard let connection else { return false }
        do {
            switch arguments.count {
            case 1: try await connection.invoke(method: method, arguments: arguments[0])
            case 2: try await connection.invoke(method: method, arguments: arguments[0], arguments[1])
            case 3: try await connection.invoke(method: method, arguments: arguments[0], arguments[1], arguments[2])
            default: try await connection.invoke(method: method, arguments: arguments[0], arguments[1], arguments[2], arguments[3])
            }
            return true
        } catch {
            return false
        }
    }
}

nonisolated private struct Negotiation: Decodable, Sendable {
    var connectionId: String
    var connectionToken: String?
}
