import SwiftUI
#if canImport(UIKit)
import UIKit
#endif

// How the page of a sketch sits in the editor, and how hands move it. This is
// the web editor's arithmetic (js/sketch/viewport.js): the page is placed by a
// pan, a zoom and an angle, and every gesture is worked out so that the point
// of the page under the fingers when it began is still under them now. Nothing
// is eased or estimated on the way, which is what makes the page feel held.

/// Where the page is: its top-left corner in the view, how large, and turned how far.
nonisolated struct SketchView: Equatable, Sendable {
    var pan: CGPoint = .zero
    var zoom: CGFloat = 1
    /// In radians, clockwise as the screen shows it, kept between a half turn either way.
    var angle: CGFloat = 0

    /// How far out and in the page goes. Out, as on the web. In, half as far: the web's sixteen
    /// times is a pixel wider than a thumb, and the page is lost behind it (`ZoomLimit`). A
    /// large page on a dense screen may stop short even of this: see `most(for:screenScale:)`.
    static let zoomRange: ClosedRange<CGFloat> = 0.1...ZoomLimit.sketchPixel

    /// How far in a page of this size may go on a screen of this scale.
    static func most(for page: CGSize, screenScale: CGFloat) -> CGFloat {
        ZoomLimit.most(wanted: zoomRange.upperBound, longestSide: max(page.width, page.height), screenScale: screenScale)
    }

    /// The point of the page under a point of the view.
    func pagePoint(_ point: CGPoint) -> CGPoint {
        let x = (point.x - pan.x) / zoom, y = (point.y - pan.y) / zoom
        return CGPoint(x: cos(-angle) * x - sin(-angle) * y, y: sin(-angle) * x + cos(-angle) * y)
    }

    /// Where in the view a point of the page is.
    func viewPoint(_ point: CGPoint) -> CGPoint {
        CGPoint(x: pan.x + (cos(angle) * point.x - sin(angle) * point.y) * zoom,
                y: pan.y + (sin(angle) * point.x + cos(angle) * point.y) * zoom)
    }

    /// This view zoomed or turned, with whatever of the page is at `anchor` now ending up at
    /// `target` — the same place, unless the fingers that hold it have moved.
    func anchored(at anchor: CGPoint, zoom: CGFloat? = nil, angle: CGFloat? = nil, movedTo target: CGPoint? = nil,
                  most: CGFloat = SketchView.zoomRange.upperBound) -> SketchView {
        let held = pagePoint(anchor), target = target ?? anchor
        // A page already further in than it may now go — the limit is the page's, and pages
        // change size — is left where it is and not pulled back by a pan.
        let ceiling = max(min(most, Self.zoomRange.upperBound), zoom == nil ? self.zoom : 0)
        var next = SketchView(pan: .zero, zoom: min(max(zoom ?? self.zoom, Self.zoomRange.lowerBound), ceiling),
                              angle: Self.wrapped(angle ?? self.angle))
        let offset = next.viewPoint(held)
        next.pan = CGPoint(x: target.x - offset.x, y: target.y - offset.y)
        return next
    }

    /// The whole page, upright, in the middle of the room there is for it.
    static func fitting(page: CGSize, in room: CGRect) -> SketchView {
        guard page.width > 0, page.height > 0 else { return SketchView(pan: room.origin) }
        let zoom = max(0.01, min(room.width / page.width, room.height / page.height))
        return SketchView(pan: CGPoint(x: room.midX - page.width * zoom / 2, y: room.midY - page.height * zoom / 2), zoom: zoom)
    }

    private static func wrapped(_ angle: CGFloat) -> CGFloat { atan2(sin(angle), cos(angle)) }

    /// Two fingers on the page: the view as they found it, and where they were.
    struct Pinch: Sendable {
        var view: SketchView
        var middle: CGPoint
        var distance: CGFloat
        var rotation: CGFloat

        init(view: SketchView, _ a: CGPoint, _ b: CGPoint) {
            self.view = view
            middle = CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2)
            distance = hypot(a.x - b.x, a.y - b.y)
            rotation = atan2(b.y - a.y, b.x - a.x)
        }

        /// The view with the fingers where they are now: the page goes with their middle,
        /// grows as they part and turns as they turn.
        func view(_ a: CGPoint, _ b: CGPoint, most: CGFloat = SketchView.zoomRange.upperBound) -> SketchView {
            let now = CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2)
            let turned = atan2(b.y - a.y, b.x - a.x) - rotation
            return view.anchored(at: middle, zoom: view.zoom * hypot(a.x - b.x, a.y - b.y) / max(1, distance),
                                 angle: view.angle + atan2(sin(turned), cos(turned)), movedTo: now, most: most)
        }
    }
}

/// Two fingers tapped together undo, three redo, as in the web editor. Anything longer or
/// looser was the start of a pinch or a pan, or a palm, and is left to those.
nonisolated struct SketchTouchTap {
    enum Action: Sendable { case undo, redo }

    static let slop: CGFloat = 10
    static let maxDuration: TimeInterval = 0.35

    private var starts: [ObjectIdentifier: CGPoint] = [:]
    private var startedAt: TimeInterval = 0
    private var peak = 0
    private var spoiled = false

    /// True when this finger starts a new tap, so that the view can be kept to put back.
    mutating func down(_ finger: ObjectIdentifier, at point: CGPoint, time: TimeInterval) -> Bool {
        let starting = starts.isEmpty
        if starting {
            startedAt = time
            peak = 0
            spoiled = false
        }
        starts[finger] = point
        peak = max(peak, starts.count)
        return starting
    }

    mutating func move(_ finger: ObjectIdentifier, to point: CGPoint) {
        guard let start = starts[finger] else { return }
        if hypot(point.x - start.x, point.y - start.y) > Self.slop { spoiled = true }
    }

    mutating func up(_ finger: ObjectIdentifier, at point: CGPoint, time: TimeInterval, cancelled: Bool = false) -> Action? {
        guard starts[finger] != nil else { return nil }
        if cancelled { spoiled = true } else { move(finger, to: point) }
        starts[finger] = nil
        guard starts.isEmpty, !spoiled, time - startedAt <= Self.maxDuration else { return nil }
        return peak == 2 ? .undo : peak == 3 ? .redo : nil
    }

    /// The Pencil came down: whatever fingers were up to, it was not a tap.
    mutating func reset() {
        starts = [:]
    }
}

#if os(iOS)
/// The page under the hand. The Pencil draws, and a finger does too where
/// fingers are for drawing; otherwise one finger moves the page, and two move,
/// zoom and turn it. A trackpad scrolls, pinches and turns it the same way.
struct SketchCanvas: UIViewRepresentable {
    var model: SketchModel
    var image: CGImage?
    var page: SketchPage
    var canDraw: Bool
    var tool: String
    var ink: SketchInk
    var level: Int
    var blast: SketchBlast?
    var pointers: [SketchPointer] = []
    var dark = false
    var fingerDraws = true
    /// The bars over the canvas: the page is fitted clear of them, and goes under them when moved.
    var bars = EdgeInsets()
    /// Counts up each time the page is asked back to where it fits.
    var fitRequest = 0
    var onPencilTap: () -> Void = {}
    /// Told when the page leaves the place it was fitted to, and when it is put back.
    var onMoved: (Bool) -> Void = { _ in }

    final class Coordinator {
        var fitRequest = 0
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeUIView(context: Context) -> SketchViewport {
        let view = SketchViewport(page: CGSize(width: page.width, height: page.height))
        view.pageView.model = model
        context.coordinator.fitRequest = fitRequest
        return view
    }

    func updateUIView(_ view: SketchViewport, context: Context) {
        view.setPage(CGSize(width: self.page.width, height: self.page.height))
        view.fingerDraws = fingerDraws
        view.bars = UIEdgeInsets(top: bars.top, left: bars.leading, bottom: bars.bottom, right: bars.trailing)
        view.onPencilTap = onPencilTap
        view.onMoved = onMoved
        let page = view.pageView
        page.layer.contents = image
        page.canDraw = canDraw
        page.tool = tool
        page.ink = ink
        page.level = level
        page.play(blast)
        page.show(pointers, dark: dark)
        if context.coordinator.fitRequest != fitRequest {
            context.coordinator.fitRequest = fitRequest
            view.fit(animated: true)
        }
    }
}

final class SketchViewport: UIView, UIPencilInteractionDelegate, UIGestureRecognizerDelegate {
    let pageView: SketchPageView
    private(set) var view = SketchView()
    var fingerDraws = true
    /// The bars over the view, as SwiftUI knows them — which is more than the view is told
    /// itself: a bar put there with a safe-area inset is not in its own insets.
    var bars: UIEdgeInsets = .zero {
        didSet { if bars != oldValue { setNeedsLayout() } }
    }
    var onPencilTap: () -> Void = {}
    var onMoved: (Bool) -> Void = { _ in }

    /// The page is where fitting put it, and is fitted again whenever the room for it changes.
    private var fitted = true
    private var fittedIn: CGRect = .null
    private var size: CGSize = .zero

    /// The fingers on the view, in the order they came down. The first two hold the page.
    private var fingers: [UITouch] = []
    /// Fingers that came down under a Pencil, or were down when it arrived: a resting palm.
    private var ignored: Set<UITouch> = []
    private var pinch: SketchView.Pinch?
    /// How far in this page may go on this screen.
    private var mostZoom: CGFloat {
        SketchView.most(for: pageView.bounds.size, screenScale: window?.screen.scale ?? traitCollection.displayScale)
    }
    private var drag: (touch: UITouch, from: CGPoint, pan: CGPoint)?
    private var tap = SketchTouchTap()
    private var beforeTap: (view: SketchView, fitted: Bool)?

    init(page: CGSize) {
        pageView = SketchPageView(frame: CGRect(origin: .zero, size: page))
        super.init(frame: .zero)
        backgroundColor = .clear
        clipsToBounds = true
        isMultipleTouchEnabled = true
        // The page is placed by its corner, and turned and zoomed about it.
        pageView.layer.anchorPoint = .zero
        pageView.isUserInteractionEnabled = false
        addSubview(pageView)

        let pencil = UIPencilInteraction()
        pencil.delegate = self
        addInteraction(pencil)

        // A trackpad or a mouse wheel. Fingers on the glass never reach these: they are
        // followed touch by touch below, with no recognizer's threshold to wait out.
        let scroll = UIPanGestureRecognizer(target: self, action: #selector(scrolled))
        scroll.allowedScrollTypesMask = .all
        scroll.allowedTouchTypes = []
        let pointer = [NSNumber(value: UITouch.TouchType.indirectPointer.rawValue)]
        let zoom = UIPinchGestureRecognizer(target: self, action: #selector(pinched))
        zoom.allowedTouchTypes = pointer
        let turn = UIRotationGestureRecognizer(target: self, action: #selector(turned))
        turn.allowedTouchTypes = pointer
        for recognizer in [scroll, zoom, turn] {
            recognizer.delegate = self
            addGestureRecognizer(recognizer)
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    // MARK: Where the page is

    /// The paper changed size or turned: lay the new page out and fit it afresh.
    func setPage(_ size: CGSize) {
        guard pageView.bounds.size != size else { return }
        pageView.bounds = CGRect(origin: .zero, size: size)
        fitted = true
        fittedIn = .null
        onMoved(false)
        setNeedsLayout()
    }

    /// The room the page is fitted into: clear of the bars, with a little air around it.
    private var room: CGRect {
        let clear = bounds.inset(by: bars)
        let air = min(16, clear.width / 4, clear.height / 4)
        return clear.insetBy(dx: air, dy: air)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard bounds.width > 0, bounds.height > 0 else { return }
        if fitted {
            refit()
        } else if size != bounds.size, size != .zero {
            // The view changed shape under a page that had been moved: what was in its middle stays there.
            let before = CGPoint(x: size.width / 2, y: size.height / 2)
            view = view.anchored(at: before, movedTo: CGPoint(x: bounds.midX, y: bounds.midY))
            place()
        }
        size = bounds.size
    }

    private func refit() {
        let room = room
        guard room.width > 0, room.height > 0, room != fittedIn else { return }
        fittedIn = room
        view = .fitting(page: pageView.bounds.size, in: room)
        place()
    }

    /// Puts the page back where it fits, upright.
    func fit(animated: Bool) {
        let room = room
        guard room.width > 0, room.height > 0 else { return }
        fitted = true
        fittedIn = room
        view = .fitting(page: pageView.bounds.size, in: room)
        if animated, !UIAccessibility.isReduceMotionEnabled {
            UIView.animate(withDuration: 0.4, delay: 0, usingSpringWithDamping: 0.9, initialSpringVelocity: 0,
                           options: [.beginFromCurrentState, .allowUserInteraction]) { self.place() }
        } else {
            place()
        }
        onMoved(false)
    }

    /// A hand moved the page.
    private func move(to next: SketchView) {
        view = next
        place()
        if fitted {
            fitted = false
            onMoved(true)
        }
    }

    private func place() {
        pageView.center = view.pan
        pageView.transform = CGAffineTransform(rotationAngle: view.angle).scaledBy(x: view.zoom, y: view.zoom)
        pageView.viewChanged(zoom: view.zoom, angle: view.angle)
    }

    // MARK: Fingers and the Pencil

    private func onPage(_ touch: UITouch) -> Bool {
        pageView.bounds.contains(touch.location(in: pageView))
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            switch touch.type {
            case .pencil:
                // The Pencil has the page to itself: fingers down now, or landing while it draws, are a palm.
                tap.reset()
                ignored.formUnion(fingers)
                fingers = []
                pinch = nil
                drag = nil
                pageView.abandon()
                start(touch, draws: true)
            case .direct:
                if pageView.drawingWithPencil {
                    ignored.insert(touch)
                    continue
                }
                if tap.down(ObjectIdentifier(touch), at: touch.location(in: self), time: touch.timestamp) {
                    beforeTap = (view, fitted)
                }
                fingers.append(touch)
                if fingers.count >= 2 {
                    // A second finger: whatever the first had begun, they are holding the page now.
                    pageView.abandon()
                    drag = nil
                    hold()
                } else if pinch == nil {
                    start(touch, draws: fingerDraws)
                }
            default:
                // A click and drag with a pointer draws, as a mouse does on the web.
                start(touch, draws: true)
            }
        }
    }

    /// One touch alone: it draws where it may and is on the page, and carries the page otherwise.
    private func start(_ touch: UITouch, draws: Bool) {
        guard pageView.drawing == nil, drag == nil else { return }
        if draws, pageView.canDraw, onPage(touch) {
            pageView.begin(touch)
        } else {
            drag = (touch, touch.location(in: self), view.pan)
        }
    }

    /// Takes the first two fingers' places, and the view, as what the pinch is measured from.
    private func hold() {
        guard fingers.count >= 2 else { return }
        pinch = SketchView.Pinch(view: view, fingers[0].location(in: self), fingers[1].location(in: self))
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches where touch.type == .direct {
            tap.move(ObjectIdentifier(touch), to: touch.location(in: self))
        }
        if let pinch, fingers.count >= 2 {
            guard touches.contains(fingers[0]) || touches.contains(fingers[1]) else { return }
            move(to: pinch.view(fingers[0].location(in: self), fingers[1].location(in: self), most: mostZoom))
            return
        }
        if let drag, touches.contains(drag.touch) {
            let now = drag.touch.location(in: self)
            var next = view
            next.pan = CGPoint(x: drag.pan.x + now.x - drag.from.x, y: drag.pan.y + now.y - drag.from.y)
            move(to: next)
        }
        if let touch = pageView.drawing, touches.contains(touch) {
            pageView.extend(touch, with: event)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        lifted(touches, cancelled: false)
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        lifted(touches, cancelled: true)
    }

    private func lifted(_ touches: Set<UITouch>, cancelled: Bool) {
        var action: SketchTouchTap.Action?
        for touch in touches {
            if ignored.remove(touch) != nil { continue }
            if touch === pageView.drawing {
                if cancelled { pageView.abandon() } else { pageView.finish() }
            }
            if drag?.touch === touch { drag = nil }
            guard let index = fingers.firstIndex(of: touch) else { continue }
            fingers.remove(at: index)
            action = tap.up(ObjectIdentifier(touch), at: touch.location(in: self), time: touch.timestamp, cancelled: cancelled) ?? action
            if pinch != nil {
                // Fewer fingers than two hold nothing, but the one left does not start drawing either;
                // with two still down, they are the pair the page follows from here.
                if fingers.isEmpty { pinch = nil } else { hold() }
            }
        }
        guard let action, let before = beforeTap else { return }
        // The tap's own jitter nudged the view: put it back, then do what the tap asked.
        view = before.view
        place()
        if fitted != before.fitted {
            fitted = before.fitted
            onMoved(!fitted)
        }
        switch action {
        case .undo: pageView.model?.undo()
        case .redo: pageView.model?.redo()
        }
    }

    // MARK: A trackpad

    func gestureRecognizer(_ recognizer: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        true
    }

    /// Two fingers on a trackpad, or a wheel: the page moves; with Command or Control held it
    /// zooms about the pointer, and with Option it turns about it.
    @objc private func scrolled(_ recognizer: UIPanGestureRecognizer) {
        guard recognizer.state == .changed else { return }
        let by = recognizer.translation(in: self), at = recognizer.location(in: self)
        recognizer.setTranslation(.zero, in: self)
        let keys = recognizer.modifierFlags
        if keys.contains(.command) || keys.contains(.control) {
            move(to: view.anchored(at: at, zoom: view.zoom * exp(by.y * 0.005), most: mostZoom))
        } else if keys.contains(.alternate) {
            move(to: view.anchored(at: at, angle: view.angle - by.y * 0.15 * .pi / 180))
        } else {
            var next = view
            next.pan = CGPoint(x: view.pan.x + by.x, y: view.pan.y + by.y)
            move(to: next)
        }
    }

    // A recognizer reports how far the whole gesture has come. Each step here is the change
    // since the step before, kept by hand: setting a trackpad pinch's scale back to one does
    // not start it over, so every step would apply the whole pinch again and the zoom run away.
    private var lastScale: CGFloat = 1
    private var lastRotation: CGFloat = 0

    @objc private func pinched(_ recognizer: UIPinchGestureRecognizer) {
        switch recognizer.state {
        case .began:
            lastScale = 1
            fallthrough
        case .changed:
            let scale = recognizer.scale
            guard scale > 0, lastScale > 0 else { return }
            move(to: view.anchored(at: recognizer.location(in: self), zoom: view.zoom * scale / lastScale, most: mostZoom))
            lastScale = scale
        default:
            break
        }
    }

    @objc private func turned(_ recognizer: UIRotationGestureRecognizer) {
        switch recognizer.state {
        case .began:
            lastRotation = 0
            fallthrough
        case .changed:
            let rotation = recognizer.rotation
            move(to: view.anchored(at: recognizer.location(in: self), angle: view.angle + rotation - lastRotation))
            lastRotation = rotation
        default:
            break
        }
    }

    // MARK: The Pencil's double-tap

    // iPadOS 17.5 and later call this one and skip the older method below.
    func pencilInteraction(_ interaction: UIPencilInteraction, didReceiveTap tap: UIPencilInteraction.Tap) {
        pencilTapped()
    }

    func pencilInteractionDidTap(_ interaction: UIPencilInteraction) {
        pencilTapped()
    }

    /// The Pencil's double-tap does what Settings says it does. Either of its tool-switching
    /// choices swaps pen and eraser, the two tools there are; anything else is left to the system.
    private func pencilTapped() {
        guard pageView.canDraw else { return }
        switch UIPencilInteraction.preferredTapAction {
        case .switchEraser, .switchPrevious: onPencilTap()
        default: break
        }
    }
}
#endif
