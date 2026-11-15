import QuartzCore
import UIKit

/// Measurement for a debug build: how smoothly frames arrive, and how often the
/// expensive parts of a screen are worked out. With `SOLO_PERF` set, a line a
/// second goes to `Documents/perf.log` — frames drawn, the longest wait between
/// two, how much of the second was spent late, and whatever was counted. In a
/// release build `count` is nothing at all.
enum Perf {
    #if DEBUG || PERF
    private static var counts: [String: Int] = [:]
    private static let watch = PerfWatch()

    @inline(__always) static func count(_ name: String, _ by: Int = 1) {
        counts[name, default: 0] += by
    }

    static func start() {
        guard ProcessInfo.processInfo.environment["SOLO_PERF"] != nil else { return }
        watch.start()
    }

    fileprivate static func drain() -> String {
        defer { counts = [:] }
        return counts.sorted { $0.key < $1.key }.map { "\($0.key)=\($0.value)" }.joined(separator: " ")
    }
    #else
    @inline(__always) static func count(_ name: String, _ by: Int = 1) {}
    static func start() {}
    #endif
}

#if DEBUG || PERF
private final class PerfWatch: NSObject {
    private var link: CADisplayLink?
    private var last: CFTimeInterval = 0
    private var began: CFTimeInterval = 0
    private var second = 0
    private var frames = 0
    private var worst = 0.0
    private var late = 0.0
    private var log = ""

    func start() {
        guard link == nil else { return }
        let link = CADisplayLink(target: self, selector: #selector(tick))
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    @objc private func tick(_ link: CADisplayLink) {
        let now = link.timestamp
        if began == 0 { began = now }
        if last > 0 {
            let gap = (now - last) * 1000
            let due = max(link.duration * 1000, 1)
            frames += 1
            worst = max(worst, gap)
            // A frame that took more than one and a half of its turns kept the screen waiting.
            if gap > due * 1.5 { late += gap - due }
        }
        last = now
        let at = Int(now - began)
        guard at != second else { return }
        log += String(format: "t=%d frames=%d worst=%.0fms late=%.0fms ", second, frames, worst, late) + Perf.drain() + "\n"
        second = at
        frames = 0
        worst = 0
        late = 0
        try? log.write(to: URL.documentsDirectory.appending(path: "perf.log"), atomically: true, encoding: .utf8)
    }
}
#endif
