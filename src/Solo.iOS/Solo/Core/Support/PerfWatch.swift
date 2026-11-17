import QuartzCore
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

/// Measurement for a debug build: how smoothly frames arrive, and how often the
/// expensive parts of a screen are worked out. With `SOLO_PERF` set, a line a
/// second goes to `Documents/perf.log` — frames drawn, the longest wait between
/// two, how much of the second was spent late, and whatever was counted. In a
/// release build `count` is nothing at all.
///
/// `SOLO_PERF_SCROLL`, a speed in points a second, has the watch scroll the
/// screen itself once it has settled: down the longest page showing at that
/// speed, then back up at three times it. That is how a device is measured,
/// where nothing can swipe for you, and it is the same swipe every time.
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
    private var scroller = PerfScroller(speed: Double(ProcessInfo.processInfo.environment["SOLO_PERF_SCROLL"] ?? ""))

    func start() {
        guard link == nil else { return }
        #if os(macOS)
        // A Mac's link is its window's, and keeps time with whichever screen that is on: there
        // is no window yet when the app starts, so it is waited for.
        guard let view = NSApplication.shared.windows.first(where: \.isVisible)?.contentView else {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { [weak self] in self?.start() }
            return
        }
        let link = view.displayLink(target: self, selector: #selector(tick))
        #else
        let link = CADisplayLink(target: self, selector: #selector(tick))
        #endif
        // As often as the screen will have it. On a phone that is 60 a second
        // unless the build's Info.plist has CADisableMinimumFrameDurationOnPhone.
        link.preferredFrameRateRange = CAFrameRateRange(minimum: 60, maximum: 120, preferred: 120)
        link.add(to: .main, forMode: .common)
        self.link = link
    }

    /// How hot the device says it is, and whether it is saving power: either one slows
    /// everything, and a run measured that way is not one to compare with.
    private var heat: String {
        let process = ProcessInfo.processInfo
        let state = switch process.thermalState {
        case .nominal: "nominal"
        case .fair: "fair"
        case .serious: "serious"
        case .critical: "critical"
        @unknown default: "unknown"
        }
        return "heat=\(state)" + (process.isLowPowerModeEnabled ? " lowpower" : "")
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
        let step = last > 0 ? now - last : 0
        last = now
        scroller?.step(step, at: now - began)
        let at = Int(now - began)
        guard at != second else { return }
        // A run with nobody touching the screen must not end with it going dark. Said again each
        // second: said once while the app is still starting, it does not hold.
        #if os(iOS)
        UIApplication.shared.isIdleTimerDisabled = true
        #endif
        log += String(format: "t=%d frames=%d worst=%.0fms late=%.0fms ", second, frames, worst, late)
            + [heat, scroller?.note, Perf.drain()].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " ") + "\n"
        second = at
        frames = 0
        worst = 0
        late = 0
        try? log.write(to: URL.documentsDirectory.appending(path: "perf.log"), atomically: true, encoding: .utf8)
    }
}

/// Scrolls the page for the watch: the same swipe each run, with nobody there.
private struct PerfScroller {
    private enum Phase: String { case waiting, down, up, done }

    private let speed: Double
    private var phase = Phase.waiting
    private weak var page: PlatformScrollView?
    private var seen: CFTimeInterval?
    private var began: CFTimeInterval = 0

    init?(speed: Double?) {
        guard let speed, speed > 0 else { return nil }
        self.speed = speed
    }

    var note: String {
        "scroll=\(phase.rawValue)" + (page.map { String(format: " y=%.0f of=%.0f", $0.perfOffset, $0.perfLength) } ?? "")
    }

    mutating func step(_ seconds: CFTimeInterval, at now: CFTimeInterval) {
        switch phase {
        case .waiting:
            // Once there is a page worth scrolling, give it three seconds to finish arriving.
            guard let found = PlatformScrollView.perfLongest() else { seen = nil; return }
            page = found
            if seen == nil { seen = now }
            if let seen, now - seen > 3 { phase = .down; began = now }
        case .down:
            guard let page else { phase = .done; return }
            // A lazy page only guesses its length, so the end is asked for again each step.
            let end = page.perfEnd
            let next = page.perfOffset + speed * seconds
            page.perfScroll(to: min(next, end))
            if next >= end || now - began > 15 { phase = .up }
        case .up:
            guard let page else { phase = .done; return }
            let top = page.perfTop
            let next = page.perfOffset - speed * 3 * seconds
            page.perfScroll(to: max(next, top))
            if next <= top { phase = .done }
        case .done:
            break
        }
    }
}

// What the scroller asks of a page, said once for each kit's scroll view.
#if os(macOS)
private extension NSScrollView {
    var perfOffset: CGFloat { contentView.bounds.origin.y }
    var perfLength: CGFloat { documentView?.frame.height ?? 0 }
    var perfTop: CGFloat { -contentInsets.top }
    var perfEnd: CGFloat { perfLength - contentView.bounds.height + contentInsets.bottom }

    func perfScroll(to y: CGFloat) {
        contentView.scroll(to: NSPoint(x: contentView.bounds.origin.x, y: y))
        reflectScrolledClipView(contentView)
    }

    /// The scroll view in the main window with the furthest to go down.
    static func perfLongest() -> NSScrollView? {
        var best: (page: NSScrollView, room: CGFloat)?
        func look(_ view: NSView) {
            guard !view.isHidden else { return }
            if let page = view as? NSScrollView {
                let room = page.perfLength - page.contentView.bounds.height
                if room > 300, room > best?.room ?? 0 { best = (page, room) }
            }
            view.subviews.forEach(look)
        }
        (NSApplication.shared.mainWindow ?? NSApplication.shared.windows.first(where: \.isVisible))?.contentView.map(look)
        return best?.page
    }
}
#else
private extension UIScrollView {
    var perfOffset: CGFloat { contentOffset.y }
    var perfLength: CGFloat { contentSize.height }
    var perfTop: CGFloat { -adjustedContentInset.top }
    var perfEnd: CGFloat { contentSize.height - bounds.height + adjustedContentInset.bottom }

    func perfScroll(to y: CGFloat) { contentOffset.y = y }

    /// The scroll view on screen with the furthest to go down.
    static func perfLongest() -> UIScrollView? {
        let windows = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }.flatMap(\.windows)
        var best: (page: UIScrollView, room: CGFloat)?
        func look(_ view: UIView) {
            guard !view.isHidden, view.alpha > 0 else { return }
            if let page = view as? UIScrollView {
                let room = page.contentSize.height - page.bounds.height
                if room > 300, room > best?.room ?? 0 { best = (page, room) }
            }
            view.subviews.forEach(look)
        }
        windows.filter(\.isKeyWindow).forEach(look)
        return best?.page
    }
}
#endif
#endif
