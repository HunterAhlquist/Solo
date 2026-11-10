import Foundation

/// Local ports per instance, kept for good: the port is part of the page's
/// origin, and the origin holds the instance's storage — its session, and any
/// edits still waiting to sync. So a port, once handed to an instance, is never
/// handed to another one, or that one would open signed in as the first, with
/// its offline data. Ports are handed out from 41321 up, like the desktop app's.
///
/// Each instance has its main port and the fallbacks it was given when another
/// app held that port. A launch tries the port the instance last opened on
/// first, so it moves only when that port is held: moving strands whatever
/// is on the old origin (a session, edits still waiting to sync) until the
/// instance opens there again, so it shouldn't happen just because the main
/// port came free. A fallback stays the instance's, so what was left on its
/// origin is there again whenever it's used.
///
/// Foundation only, so `src/Solo.Mobile/scripts/check-instance-ports.sh` can
/// compile and run it on the Mac.
enum InstancePorts {
    private static let mainKey = "solo.instances.ports"
    private static let fallbackKey = "solo.instances.fallbackPorts"
    private static let lastKey = "solo.instances.lastPorts"
    static let first: UInt16 = 41321
    /// New fallbacks one launch may add once every saved port has failed.
    /// Bounded, so a launch that can't listen at all doesn't burn through
    /// ports; but always some, so the saved ones failing on every launch
    /// can't leave the instance with nowhere to go.
    static let newPortsPerLaunch = 5

    /// The first result `start` gives on one of the instance's ports: the one
    /// it last opened on, then every other saved port, main port first, then
    /// up to `newPortsPerLaunch` new fallbacks, each saved before it's tried.
    /// Nil when none started.
    static func start<T>(for instance: URL, defaults: UserDefaults = .standard,
                         _ start: (UInt16) throws -> T) -> T? {
        let saved = ports(for: instance, defaults: defaults)
        let last = (defaults.dictionary(forKey: lastKey) as? [String: Int])?[instance.absoluteString]
            .map(UInt16.init).flatMap { saved.contains($0) ? $0 : nil }
        let candidates = (last.map { [$0] } ?? []) + saved.filter { $0 != last }
        func opened(_ port: UInt16) {
            var lasts = (defaults.dictionary(forKey: lastKey) as? [String: Int]) ?? [:]
            lasts[instance.absoluteString] = Int(port)
            defaults.set(lasts, forKey: lastKey)
        }
        for port in candidates {
            if let started = try? start(port) { opened(port); return started }
        }
        for _ in 0..<newPortsPerLaunch {
            let port = addFallback(for: instance, defaults: defaults)
            if let started = try? start(port) { opened(port); return started }
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
