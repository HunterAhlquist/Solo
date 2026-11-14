import Network
import UIKit

// An instance on the local network is behind iOS's local network privacy: the
// first connection to it fails, and the system asks whether Solo may use the
// local network. The system offers no way to ask what the answer was — only a
// connection that works, or one that waits with "local network denied" as its
// reason. This reads what it can from those.

enum LocalNetworkAccess {
    /// Whether a system alert came up as a connection failed, and has now been answered.
    ///
    /// The permission alert is the system's, so the app only sees itself stop
    /// being active while it is up. The failure comes back a beat before or
    /// after the alert shows, hence the short wait for it to; then as long as
    /// the person takes to answer.
    static func alertCameAndWent() async -> Bool {
        var waited = 0
        while UIApplication.shared.applicationState == .active, waited < 6 {
            try? await Task.sleep(for: .milliseconds(100))
            waited += 1
        }
        guard UIApplication.shared.applicationState != .active else { return false }
        while UIApplication.shared.applicationState != .active {
            try? await Task.sleep(for: .milliseconds(150))
        }
        // The answer takes a moment to reach the network stack.
        try? await Task.sleep(for: .milliseconds(350))
        return true
    }

    /// Whether local network privacy is what stands between the app and this address.
    static func isDenied(for url: URL) async -> Bool {
        guard let host = url.host(), !host.isEmpty else { return false }
        let number = url.port ?? (url.scheme == "http" ? 80 : 443)
        guard let port = NWEndpoint.Port(rawValue: UInt16(clamping: number)) else { return false }
        return await Probe(host: host, port: port).run()
    }

    /// One attempt to open a connection, watched for why it doesn't.
    private final class Probe: @unchecked Sendable {
        private let connection: NWConnection
        private let queue = DispatchQueue(label: "solo.local-network-probe")
        private var continuation: CheckedContinuation<Bool, Never>?

        init(host: String, port: NWEndpoint.Port) {
            connection = NWConnection(host: NWEndpoint.Host(host), port: port, using: .tcp)
        }

        func run() async -> Bool {
            await withCheckedContinuation { continuation in
                queue.async {
                    self.continuation = continuation
                    self.connection.stateUpdateHandler = { [weak self] state in
                        guard let self else { return }
                        switch state {
                        case .ready:
                            self.finish(false)
                        case .waiting:
                            // Waiting is where a blocked connection sits; why it waits is on its path.
                            if self.connection.currentPath?.unsatisfiedReason == .localNetworkDenied {
                                self.finish(true)
                            }
                        case .failed, .cancelled:
                            self.finish(false)
                        default:
                            break
                        }
                    }
                    self.connection.start(queue: self.queue)
                    // An address that simply doesn't answer is not a denial.
                    self.queue.asyncAfter(deadline: .now() + 2.5) { self.finish(false) }
                }
            }
        }

        private func finish(_ denied: Bool) {
            guard let continuation else { return }
            self.continuation = nil
            connection.stateUpdateHandler = nil
            connection.cancel()
            continuation.resume(returning: denied)
        }
    }
}
