import Foundation

/// Local ports per instance, kept for good: the port is part of the page's
/// origin, and the origin holds the instance's storage — its session, and any
/// edits still waiting to sync. So a port, once handed to an instance, is never
/// handed to another one, or that one would open signed in as the first, with
/// its offline data. Ports are handed out from 41321 up, like the desktop app's.
///
/// Each instance has its main port, tried first on every launch, and the
/// fallbacks it was given when another app held that port. A fallback stays
/// the instance's, so whatever was left on its origin is there again whenever
/// it's used.
///
/// Foundation only, so `src/Solo.Mobile/scripts/check-instance-ports.sh` can
/// compile and run it on the Mac.
enum InstancePorts {
    private static let mainKey = "solo.instances.ports"
    private static let fallbackKey = "solo.instances.fallbackPorts"
    static let first: UInt16 = 41321
    /// New fallbacks one launch may add once every saved port has failed.
    /// Bounded, so a launch that can't listen at all doesn't burn through
    /// ports; but always some, so the saved ones failing on every launch
    /// can't leave the instance with nowhere to go.
    static let newPortsPerLaunch = 5

    /// The first result `start` gives on one of the instance's ports: every
    /// saved port in order, main port first, then up to `newPortsPerLaunch`
    /// new fallbacks, each saved before it's tried. Nil when none started.
    static func start<T>(for instance: URL, defaults: UserDefaults = .standard,
                         _ start: (UInt16) throws -> T) -> T? {
        for port in ports(for: instance, defaults: defaults) {
            if let started = try? start(port) { return started }
        }
        for _ in 0..<newPortsPerLaunch {
            if let started = try? start(addFallback(for: instance, defaults: defaults)) { return started }
        }
        return nil
    }

    /// The instance's ports, main port first. Assigns the main port the first
    /// time an instance is seen.
    static func ports(for instance: URL, defaults: UserDefaults = .standard) -> [UInt16] {
        var main = mainPorts(defaults)
        let id = instance.absoluteString
        if main[id] == nil {
            main[id] = nextFree(defaults)
            defaults.set(main, forKey: mainKey)
        }
        return ([main[id]!] + (fallbackPorts(defaults)[id] ?? [])).map { UInt16($0) }
    }

    /// A new port of the instance's own.
    private static func addFallback(for instance: URL, defaults: UserDefaults) -> UInt16 {
        var fallbacks = fallbackPorts(defaults)
        let port = nextFree(defaults)
        fallbacks[instance.absoluteString, default: []].append(port)
        defaults.set(fallbacks, forKey: fallbackKey)
        return UInt16(port)
    }

    private static func mainPorts(_ defaults: UserDefaults) -> [String: Int] {
        (defaults.dictionary(forKey: mainKey) as? [String: Int]) ?? [:]
    }

    private static func fallbackPorts(_ defaults: UserDefaults) -> [String: [Int]] {
        (defaults.dictionary(forKey: fallbackKey) as? [String: [Int]]) ?? [:]
    }

    /// The lowest port no instance has ever been given.
    private static func nextFree(_ defaults: UserDefaults) -> Int {
        let taken = Set(mainPorts(defaults).values).union(fallbackPorts(defaults).values.joined())
        var port = Int(first)
        while taken.contains(port) { port += 1 }
        return port
    }
}
