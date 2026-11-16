import SwiftUI
import UIKit

/// How far in a picture or a page may be zoomed.
///
/// Two things set it. The first is use: past the point where one pixel of the picture is a
/// fingertip across, there is nothing more to see, and the picture is only easier to get lost
/// in. A drawing is made of its pixels and is let in until each is `sketchPixel` points; a
/// photograph's are not for looking at and it stops at `photoPixel`.
///
/// The second is what can be drawn. Zooming scales a layer, and its shadow used to be worked
/// out from the layer itself, drawn aside at the size it was zoomed to: past about 10,000
/// pixels that gave out, and took the picture with it — a sketch in the slideshow left a black
/// screen. The shadows are given their shape outright now and that no longer happens, as far
/// in as was tried, which was 40,000 pixels on a simulator. `pixels` keeps well short of that.
nonisolated enum ZoomLimit {
    /// How large one pixel of a drawing may be on screen, in points.
    static let sketchPixel: CGFloat = 8
    /// How large one pixel of a photograph may be.
    static let photoPixel: CGFloat = 2
    /// The longest a zoomed picture's longest side may be on screen, in pixels.
    static let pixels: CGFloat = 24000
    /// However large the picture already is, it can be zoomed at least this far.
    static let least: CGFloat = 2

    /// The most a picture may be zoomed: `wanted`, or less where that would make it too large.
    /// `longestSide` is its longest side in points before any zoom.
    static func most(wanted: CGFloat, longestSide: CGFloat, screenScale: CGFloat) -> CGFloat {
        let room = pixels / max(longestSide * max(screenScale, 1), 1)
        return max(least, min(wanted, room))
    }

    /// The most a picture laid out at `fitted` points to each of its own may be zoomed, for
    /// one of its pixels to come to `pixel` points; `density` is its pixels to a point.
    static func most(pixel: CGFloat, fitted: CGFloat, density: CGFloat, longestSide: CGFloat, screenScale: CGFloat) -> CGFloat {
        most(wanted: pixel * max(density, 1) / max(fitted, 0.001), longestSide: longestSide, screenScale: screenScale)
    }
}

/// A picture to look closer at. Two fingers zoom it about the place between
/// them and carry it along, and a double tap zooms in on the place tapped —
/// which is a scroll view's zooming, the same the Photos app has, rather than
/// a size worked out from a pinch: a size alone grows from a corner, and
/// cannot know where the fingers are.
struct ZoomablePicture: UIViewRepresentable {
    var picture: UIImage
    /// Pixels stay pixels when zoomed, for a sketch; a photograph is smoothed.
    var pixelated = false
    /// Room left around the picture while it is fitted.
    var margin: CGFloat = 0
    var cornerRadius: CGFloat = 0
    var shadow = false
    /// The bars over the view. The picture is fitted clear of them and, zoomed, passes under
    /// them — they are glass. Handed in, because a page of a paged tab view is not told of them.
    var bars = EdgeInsets()

    func makeUIView(context: Context) -> ZoomScrollView { ZoomScrollView() }

    func updateUIView(_ view: ZoomScrollView, context: Context) {
        view.margin = margin
        let bars = UIEdgeInsets(top: bars.top, left: bars.leading, bottom: bars.bottom, right: bars.trailing)
        if view.contentInset != bars { view.contentInset = bars }
        view.pictureView.layer.cornerRadius = cornerRadius
        view.pictureView.layer.cornerCurve = .continuous
        view.pictureView.layer.masksToBounds = cornerRadius > 0
        view.pictureView.layer.magnificationFilter = pixelated ? .nearest : .linear
        view.holder.layer.shadowOpacity = shadow ? 0.25 : 0
        view.show(picture)
    }
}

final class ZoomScrollView: UIScrollView, UIScrollViewDelegate {
    /// What is zoomed: the picture, and the shadow under it.
    let holder = UIView()
    let pictureView = UIImageView()
    var margin: CGFloat = 0 {
        didSet { if margin != oldValue { fittedIn = .zero } }
    }

    /// The room the picture was last fitted to: the view, less the bars over it.
    private var fittedIn: CGSize = .zero
    private var pictureSize: CGSize = .zero

    /// What of the view is clear of the bars: the picture is fitted and centred in this.
    private var clear: CGSize { bounds.inset(by: contentInset).size }

    init() {
        super.init(frame: .zero)
        delegate = self
        backgroundColor = .clear
        showsVerticalScrollIndicator = false
        showsHorizontalScrollIndicator = false
        contentInsetAdjustmentBehavior = .never
        decelerationRate = .fast
        bouncesZoom = true

        pictureView.contentMode = .scaleToFill
        pictureView.autoresizingMask = [.flexibleWidth, .flexibleHeight]
        holder.layer.shadowColor = UIColor.black.cgColor
        holder.layer.shadowRadius = 16
        holder.layer.shadowOffset = CGSize(width: 0, height: 6)
        holder.layer.shadowOpacity = 0
        holder.addSubview(pictureView)
        addSubview(holder)

        let twice = UITapGestureRecognizer(target: self, action: #selector(tappedTwice))
        twice.numberOfTapsRequired = 2
        addGestureRecognizer(twice)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    func show(_ picture: UIImage) {
        guard pictureView.image !== picture else { return }
        pictureView.image = picture
        // A sketch being drawn on arrives as a new picture of the same page: the zoom is kept.
        if picture.size != pictureSize {
            pictureSize = picture.size
            fittedIn = .zero
            setNeedsLayout()
        }
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        guard clear.width > 0, clear.height > 0 else { return }
        if fittedIn != clear { fit() }
        settle()
    }

    override var contentInset: UIEdgeInsets {
        didSet { if contentInset != oldValue { setNeedsLayout() } }
    }

    /// Lays the picture out as large as it fits, which is what a zoom of one means.
    private func fit() {
        fittedIn = clear
        zoomScale = 1
        let room = CGSize(width: max(1, clear.width - margin * 2), height: max(1, clear.height - margin * 2))
        let scale = pictureSize.width > 0 && pictureSize.height > 0
            ? min(room.width / pictureSize.width, room.height / pictureSize.height) : 1
        let size = CGSize(width: max(1, pictureSize.width * scale), height: max(1, pictureSize.height * scale))
        holder.frame = CGRect(origin: .zero, size: size)
        pictureView.frame = holder.bounds
        contentSize = size
        contentOffset = CGPoint(x: -contentInset.left, y: -contentInset.top)
        minimumZoomScale = 1
        // Far enough in to see a photograph's own pixels, and further for a drawing made of
        // them, and no further (`ZoomLimit`).
        let pixelated = pictureView.layer.magnificationFilter == .nearest
        maximumZoomScale = ZoomLimit.most(pixel: pixelated ? ZoomLimit.sketchPixel : ZoomLimit.photoPixel,
                                          fitted: scale, density: pictureView.image?.scale ?? 1,
                                          longestSide: max(size.width, size.height),
                                          screenScale: window?.screen.scale ?? traitCollection.displayScale)
        // Said outright, so that the shadow is not worked out from the picture itself: that is
        // done by drawing the picture aside at the size it is zoomed to, which is the first
        // thing to give out as it grows.
        holder.layer.shadowPath = UIBezierPath(roundedRect: holder.bounds, cornerRadius: pictureView.layer.cornerRadius).cgPath
    }

    /// A picture smaller than the view sits in the middle of it. Moving the picture, and not the
    /// scroll view's insets, leaves the zoom's own arithmetic about the fingers undisturbed.
    private func settle() {
        var frame = holder.frame
        let clear = clear
        frame.origin.x = frame.width < clear.width ? (clear.width - frame.width) / 2 : 0
        frame.origin.y = frame.height < clear.height ? (clear.height - frame.height) / 2 : 0
        if frame.origin != holder.frame.origin { holder.frame = frame }
    }

    func viewForZooming(in scrollView: UIScrollView) -> UIView? { holder }

    func scrollViewDidZoom(_ scrollView: UIScrollView) { settle() }

    /// In on the place tapped, or back out to the whole picture.
    @objc private func tappedTwice(_ recognizer: UITapGestureRecognizer) {
        if zoomScale > minimumZoomScale * 1.01 {
            setZoomScale(minimumZoomScale, animated: true)
            return
        }
        let at = recognizer.location(in: holder), scale: CGFloat = 2.5
        let size = CGSize(width: clear.width / scale, height: clear.height / scale)
        zoom(to: CGRect(x: at.x - size.width / 2, y: at.y - size.height / 2, width: size.width, height: size.height), animated: true)
    }
}
