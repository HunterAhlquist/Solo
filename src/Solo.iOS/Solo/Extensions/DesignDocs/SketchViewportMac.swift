#if os(macOS)
import AppKit
import SwiftUI

// The sketch's page on a Mac. The arithmetic of where the page sits is the iPhone's and the
// web's (`SketchView`); what moves it is a Mac's own hands. A click and drag on the page
// draws, as a mouse does on the web. Two fingers on the trackpad carry the page, a pinch
// zooms it about the pointer and a twist turns it; a wheel scrolls it, and zooms with Command
// or Control held and turns with Option. A drag that starts off the page, or with the
// secondary button anywhere, carries the page too.
//
// It is drawn in layers and not in views: AppKit's views cannot be turned and zoomed about a
// corner, and a layer can.

/// The page under the hand.
struct SketchCanvas: NSViewRepresentable {
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
    /// An iPhone's question; a Mac's pointer always draws.
    var fingerDraws = true
    /// The bars over the canvas: the page is fitted clear of them, and goes under them when moved.
    var bars = EdgeInsets()
    /// Counts up each time the page is asked back to where it fits.
    var fitRequest = 0
    /// The Pencil's, which a Mac has none of.
    var onPencilTap: () -> Void = {}
    /// Told when the page leaves the place it was fitted to, and when it is put back.
    var onMoved: (Bool) -> Void = { _ in }

    final class Coordinator {
        var fitRequest = 0
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    func makeNSView(context: Context) -> SketchViewport {
        let view = SketchViewport(page: CGSize(width: page.width, height: page.height))
        view.page.model = model
        context.coordinator.fitRequest = fitRequest
        return view
    }

    func updateNSView(_ view: SketchViewport, context: Context) {
        view.setPage(CGSize(width: self.page.width, height: self.page.height))
        view.bars = NSEdgeInsets(top: bars.top, left: bars.leading, bottom: bars.bottom, right: bars.trailing)
        view.onMoved = onMoved
        let page = view.page
        page.show(image)
        if page.canDraw != canDraw {
            page.canDraw = canDraw
            view.window?.invalidateCursorRects(for: view)
        }
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

final class SketchViewport: NSView, NSUserInterfaceValidations {
    let page: SketchPageLayer
    private(set) var view = SketchView()
    /// The bars over the view, as SwiftUI knows them.
    var bars = NSEdgeInsets() {
        didSet { if !NSEdgeInsetsEqual(bars, oldValue) { needsLayout = true } }
    }
    var onMoved: (Bool) -> Void = { _ in }

    /// The page is where fitting put it, and is fitted again whenever the room for it changes.
    private var fitted = true
    private var fittedIn: CGRect = .null
    private var size: CGSize = .zero
    /// A drag that is carrying the page: where it began, and where the page was then.
    private var drag: (from: CGPoint, pan: CGPoint)?

    /// How far in this page may go on this screen.
    private var mostZoom: CGFloat {
        SketchView.most(for: page.size, screenScale: window?.backingScaleFactor ?? 2)
    }

    init(page size: CGSize) {
        page = SketchPageLayer(size: size)
        super.init(frame: .zero)
        // The view's own layer, made here so that it counts down from the top as the page's
        // arithmetic does: AppKit's count up from the bottom.
        let root = CALayer()
        root.isGeometryFlipped = true
        root.masksToBounds = true
        layer = root
        wantsLayer = true
        root.addSublayer(page.layer)
        setAccessibilityElement(true)
        setAccessibilityRole(.image)
        setAccessibilityLabel("Sketch page")
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // Undo and Redo in the Edit menu, and their keys, come to whoever has the keyboard.
        window?.makeFirstResponder(self)
        page.scale = window?.backingScaleFactor ?? 2
    }

    override func resetCursorRects() {
        if page.canDraw { addCursorRect(bounds, cursor: .crosshair) }
    }

    // MARK: Where the page is

    /// The paper changed size or turned: lay the new page out and fit it afresh.
    func setPage(_ size: CGSize) {
        guard page.size != size else { return }
        page.size = size
        fitted = true
        fittedIn = .null
        onMoved(false)
        needsLayout = true
    }

    /// The room the page is fitted into: clear of the bars, with a little air around it.
    private var room: CGRect {
        let clear = CGRect(x: bounds.minX + bars.left, y: bounds.minY + bars.top,
                           width: bounds.width - bars.left - bars.right, height: bounds.height - bars.top - bars.bottom)
        let air = min(16, clear.width / 4, clear.height / 4)
        return clear.insetBy(dx: air, dy: air)
    }

    override func layout() {
        super.layout()
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
        view = .fitting(page: page.size, in: room)
        place()
    }

    /// Puts the page back where it fits, upright.
    func fit(animated: Bool) {
        let room = room
        guard room.width > 0, room.height > 0 else { return }
        fitted = true
        fittedIn = room
        view = .fitting(page: page.size, in: room)
        place(animated: animated && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion)
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

    private func place(animated: Bool = false) {
        CATransaction.begin()
        if animated {
            CATransaction.setAnimationDuration(0.35)
            CATransaction.setAnimationTimingFunction(CAMediaTimingFunction(name: .easeOut))
        } else {
            CATransaction.setDisableActions(true)
        }
        page.layer.position = view.pan
        page.layer.setAffineTransform(CGAffineTransform(rotationAngle: view.angle).scaledBy(x: view.zoom, y: view.zoom))
        page.viewChanged(zoom: view.zoom, angle: view.angle)
        CATransaction.commit()
    }

    // MARK: The pointer

    private func spot(_ event: NSEvent) -> CGPoint { convert(event.locationInWindow, from: nil) }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let at = spot(event), onPage = view.pagePoint(at)
        if page.canDraw, page.contains(onPage) {
            page.begin(at: onPage, event: event)
        } else {
            drag = (at, view.pan)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let at = spot(event)
        if page.isDrawing {
            page.extend(to: view.pagePoint(at), event: event)
        } else {
            carry(to: at)
        }
    }

    override func mouseUp(with event: NSEvent) {
        if page.isDrawing { page.finish() }
        drag = nil
    }

    // The secondary button, or a wheel pressed: the page is carried from anywhere, on it or off.
    override func rightMouseDown(with event: NSEvent) { drag = (spot(event), view.pan) }
    override func rightMouseDragged(with event: NSEvent) { carry(to: spot(event)) }
    override func rightMouseUp(with event: NSEvent) { drag = nil }
    override func otherMouseDown(with event: NSEvent) { drag = (spot(event), view.pan) }
    override func otherMouseDragged(with event: NSEvent) { carry(to: spot(event)) }
    override func otherMouseUp(with event: NSEvent) { drag = nil }

    private func carry(to at: CGPoint) {
        guard let drag else { return }
        var next = view
        next.pan = CGPoint(x: drag.pan.x + at.x - drag.from.x, y: drag.pan.y + at.y - drag.from.y)
        move(to: next)
    }

    /// Two fingers on the trackpad, or a wheel: the page moves; with Command or Control held
    /// it zooms about the pointer, and with Option it turns about it.
    override func scrollWheel(with event: NSEvent) {
        // A wheel counts in lines, a trackpad in points.
        let unit: CGFloat = event.hasPreciseScrollingDeltas ? 1 : 10
        let by = CGPoint(x: event.scrollingDeltaX * unit, y: event.scrollingDeltaY * unit), at = spot(event)
        let keys = event.modifierFlags
        if keys.contains(.command) || keys.contains(.control) {
            move(to: view.anchored(at: at, zoom: view.zoom * exp(by.y * 0.005), most: mostZoom))
        } else if keys.contains(.option) {
            move(to: view.anchored(at: at, angle: view.angle - by.y * 0.15 * .pi / 180))
        } else {
            var next = view
            next.pan = CGPoint(x: view.pan.x + by.x, y: view.pan.y + by.y)
            move(to: next)
        }
    }

    override func magnify(with event: NSEvent) {
        move(to: view.anchored(at: spot(event), zoom: view.zoom * (1 + event.magnification), most: mostZoom))
    }

    override func rotate(with event: NSEvent) {
        // AppKit counts a turn anticlockwise, in degrees; the page's angle runs clockwise.
        move(to: view.anchored(at: spot(event), angle: view.angle - CGFloat(event.rotation) * .pi / 180))
    }

    /// A double tap with two fingers, which in other apps zooms to fit.
    override func smartMagnify(with event: NSEvent) {
        fit(animated: true)
    }

    // MARK: Undo and Redo

    @objc func undo(_ sender: Any?) { page.model?.undo() }
    @objc func redo(_ sender: Any?) { page.model?.redo() }

    func validateUserInterfaceItem(_ item: NSValidatedUserInterfaceItem) -> Bool {
        switch item.action {
        case #selector(undo(_:)): page.model?.canUndo ?? false
        case #selector(redo(_:)): page.model?.canRedo ?? false
        default: true
        }
    }
}

/// The page itself: the picture of it, everyone else's pens over it, and the stroke being drawn.
final class SketchPageLayer {
    let layer = CALayer()
    weak var model: SketchModel?
    var canDraw = false
    var tool = "pen"
    var ink: SketchInk = .theme
    var level = 1
    /// The screen's pixels to a point, for the pens' initials to be drawn sharp at.
    var scale: CGFloat = 2

    private var active: SketchOp?
    var isDrawing: Bool { active != nil }
    private var blastLayer: CALayer?
    private var playing: UUID?
    private var markers: [UInt64: SketchPointerLayer] = [:]
    private var zoom: CGFloat = 1
    private var angle: CGFloat = 0

    init(size: CGSize) {
        // The page is placed by its corner, and turned and zoomed about it.
        layer.anchorPoint = .zero
        layer.bounds = CGRect(origin: .zero, size: size)
        // Pixels stay pixels however far in the page is zoomed.
        layer.magnificationFilter = .nearest
        layer.minificationFilter = .trilinear
        layer.shadowColor = NSColor.black.cgColor
        layer.shadowOpacity = 0.18
        layer.shadowRadius = 10
        layer.shadowOffset = CGSize(width: 0, height: 2)
        // Said outright, so that the shadow is not worked out from the page itself (`ZoomLimit`).
        layer.shadowPath = CGPath(rect: layer.bounds, transform: nil)
    }

    var size: CGSize {
        get { layer.bounds.size }
        set {
            CATransaction.begin()
            CATransaction.setDisableActions(true)
            layer.bounds = CGRect(origin: .zero, size: newValue)
            layer.shadowPath = CGPath(rect: layer.bounds, transform: nil)
            blastLayer?.frame = layer.bounds
            CATransaction.commit()
        }
    }

    func contains(_ point: CGPoint) -> Bool { layer.bounds.contains(point) }

    func show(_ image: CGImage?) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        layer.contents = image
        CATransaction.commit()
    }

    /// Draws everyone else's pens where they are on the page.
    func show(_ pointers: [SketchPointer], dark: Bool) {
        var shown = Set<UInt64>()
        for entry in pointers {
            let spot = CGPoint(x: entry.pointer.x, y: entry.pointer.y)
            let color = PresenceColor.color(entry.user.color, dark: dark)
            let marker = markers[entry.client] ?? SketchPointerLayer(scale: scale)
            marker.show(entry, color: color, zoom: zoom, angle: angle)
            if marker.layer.superlayer == nil {
                CATransaction.begin()
                CATransaction.setDisableActions(true)
                marker.layer.position = spot
                layer.addSublayer(marker.layer)
                CATransaction.commit()
                markers[entry.client] = marker
            } else if marker.layer.position != spot {
                // A short glide between the places it is heard from, as the web draws it.
                CATransaction.begin()
                CATransaction.setAnimationDuration(0.08)
                marker.layer.position = spot
                CATransaction.commit()
            }
            shown.insert(entry.client)
        }
        for (client, marker) in markers where !shown.contains(client) {
            marker.layer.removeFromSuperlayer()
            markers[client] = nil
        }
    }

    /// Cursors keep their size on screen, and stay upright, however the page is zoomed and turned.
    func viewChanged(zoom: CGFloat, angle: CGFloat) {
        guard zoom != self.zoom || angle != self.angle else { return }
        self.zoom = zoom
        self.angle = angle
        markers.values.forEach { $0.viewChanged(zoom: zoom, angle: angle) }
    }

    /// Plays the explosion over the page — stretched to it, as on the web, and so zoomed and
    /// moved with it — or takes it away once it is over.
    func play(_ blast: SketchBlast?) {
        guard blast?.token != playing else { return }
        playing = blast?.token
        blastLayer?.removeFromSuperlayer()
        blastLayer = nil
        guard let blast else { return }
        abandon()
        let frames = blast.frames.compactMap(\.bitmap)
        guard !frames.isEmpty else { return }
        let over = CALayer()
        over.frame = layer.bounds
        over.contentsGravity = .resize
        over.magnificationFilter = .nearest
        // One frame after another, each held for its share of the time, and nothing left behind.
        let animation = CAKeyframeAnimation(keyPath: "contents")
        animation.values = frames
        animation.calculationMode = .discrete
        animation.duration = blast.duration
        layer.addSublayer(over)
        over.add(animation, forKey: "blast")
        blastLayer = over
    }

    /// Where the pointer is as a point of a stroke: page pixels, and how hard a tablet's pen
    /// is pressed. A mouse has no pressure, so it draws the chosen width.
    private func point(_ at: CGPoint, _ event: NSEvent) -> [Double] {
        let pressure = event.subtype == .tabletPoint ? Double(min(max(event.pressure, 0), 1)) : 0
        return [Double(at.x), Double(at.y), pressure]
    }

    /// A press starts a stroke.
    func begin(at point: CGPoint, event: NSEvent) {
        guard canDraw, active == nil, let model else { return }
        active = SketchOp(id: UUID().uuidString.lowercased(), tool: tool, ink: ink.rawValue, level: level,
                          seed: Int.random(in: 0..<2_147_483_647), pointerType: event.subtype == .tabletPoint ? "pen" : "mouse",
                          points: [self.point(point, event)])
        model.sync.report(editing: true)
        if let active { model.preview(active) }
    }

    /// The stroke follows the pointer.
    func extend(to point: CGPoint, event: NSEvent) {
        guard active != nil, let model else { return }
        active?.points?.append(self.point(point, event))
        if let active { model.preview(active) }
    }

    /// The button came up: the stroke is on the page.
    func finish() {
        guard let stroke = active, let model else { return }
        active = nil
        model.commit(stroke)
        model.sync.report(editing: false)
    }

    /// The stroke was not one after all.
    func abandon() {
        guard active != nil else { return }
        active = nil
        model?.discardPreview()
        model?.sync.report(editing: false)
    }
}

/// Someone else's pen: an arrow with their initials in its tail, in their colour, and a ring
/// the size of the mark their pen or eraser would leave.
final class SketchPointerLayer {
    let layer = CALayer()
    private let ring = CAShapeLayer()
    private let arrow = CAShapeLayer()
    private let label = CATextLayer()
    private let cursor = CALayer()
    private var width: CGFloat = 1

    // The web's cursor: a 36 by 40 shape whose tip is at (2, 2) and whose tail is a circle about (23, 27).
    private static let tip = CGPoint(x: 2, y: 2)
    private static let middle = CGPoint(x: 23, y: 27)
    private static let radius: CGFloat = 11

    init(scale: CGFloat) {
        ring.fillColor = NSColor.clear.cgColor
        layer.addSublayer(ring)

        cursor.bounds = CGRect(x: 0, y: 0, width: 36, height: 40)
        arrow.path = Self.outline()
        arrow.strokeColor = NSColor.white.withAlphaComponent(0.9).cgColor
        arrow.lineWidth = 1
        arrow.lineJoin = .round
        cursor.addSublayer(arrow)
        label.frame = CGRect(x: Self.middle.x - Self.radius, y: Self.middle.y - 6, width: Self.radius * 2, height: 12)
        label.font = NSFont.systemFont(ofSize: 9, weight: .bold)
        label.fontSize = 9
        label.foregroundColor = NSColor.white.cgColor
        label.alignmentMode = .center
        // The arrow is drawn larger than it is as the page is zoomed out: sharp at twice over.
        label.contentsScale = scale * 2
        cursor.addSublayer(label)
        // Scaled about its tip, which is the point it marks.
        cursor.anchorPoint = CGPoint(x: Self.tip.x / 36, y: Self.tip.y / 40)
        cursor.position = .zero
        layer.addSublayer(cursor)
    }

    /// The arrow's two edges leave its tip and meet the circle where they touch it.
    private static func outline() -> CGPath {
        let vx = tip.x - middle.x, vy = tip.y - middle.y
        let distance = vx * vx + vy * vy
        let along = radius * radius / distance, across = radius * sqrt(distance - radius * radius) / distance
        let first = CGPoint(x: middle.x + along * vx - across * vy, y: middle.y + along * vy + across * vx)
        let second = CGPoint(x: middle.x + along * vx + across * vy, y: middle.y + along * vy - across * vx)
        let path = CGMutablePath()
        path.move(to: tip)
        path.addLine(to: first)
        let start = atan2(first.y - middle.y, first.x - middle.x), end = atan2(second.y - middle.y, second.x - middle.x)
        // The long way round, away from the tip.
        path.addArc(center: middle, radius: radius, startAngle: start, endAngle: end, clockwise: true)
        path.closeSubpath()
        return path
    }

    func show(_ entry: SketchPointer, color: PlatformColor, zoom: CGFloat, angle: CGFloat) {
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        arrow.fillColor = color.cgColor
        ring.strokeColor = color.cgColor
        label.string = entry.user.initials
        ring.isHidden = !["pen", "eraser"].contains(entry.pointer.tool)
        CATransaction.commit()
        width = max(CGFloat(entry.pointer.width), 1)
        viewChanged(zoom: zoom, angle: angle)
    }

    /// Undoes the page's zoom and turn for the arrow, about its tip, so that it reads the same
    /// wherever the page has been taken; the ring is on the page, and goes with it.
    func viewChanged(zoom: CGFloat, angle: CGFloat) {
        let scale = 1 / max(zoom, 0.01)
        CATransaction.begin()
        CATransaction.setDisableActions(true)
        cursor.setAffineTransform(CGAffineTransform(rotationAngle: -angle).scaledBy(x: scale, y: scale))
        ring.lineWidth = scale
        ring.path = CGPath(ellipseIn: CGRect(x: -width / 2, y: -width / 2, width: width, height: width), transform: nil)
        CATransaction.commit()
    }
}
#endif
