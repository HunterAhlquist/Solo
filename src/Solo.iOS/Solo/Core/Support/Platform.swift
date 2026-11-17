import ImageIO
import SwiftUI
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

// The app is one set of sources for iPhone, iPad and Mac. A Mac runs the iPad's layout — the
// sidebar, the stack beside it, the task panel — on AppKit, which has its own names for the
// few things SwiftUI leaves to the platform. This file is where the two meet: one name for
// each, so that a page says what it wants and not which kit it is on.

#if canImport(UIKit)
typealias PlatformImage = UIImage
typealias PlatformColor = UIColor
typealias PlatformFont = UIFont
typealias PlatformView = UIView
typealias PlatformScrollView = UIScrollView
#else
typealias PlatformImage = NSImage
typealias PlatformColor = NSColor
typealias PlatformFont = NSFont
typealias PlatformView = NSView
typealias PlatformScrollView = NSScrollView
#endif

enum Platform {
    #if os(macOS)
    static let isMac = true
    #else
    static let isMac = false
    #endif

    /// What the device is called where a sign-in is listed: "Solo for iPad".
    static var deviceKind: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        #endif
    }

    /// Hands an address to whatever the system opens it with: the browser, Mail.
    static func open(_ url: URL) {
        #if os(macOS)
        NSWorkspace.shared.open(url)
        #else
        UIApplication.shared.open(url)
        #endif
    }

    static func copy(_ text: String) {
        #if os(macOS)
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
        #else
        UIPasteboard.general.string = text
        #endif
    }

    /// Whether the app is the one in front. An alert of the system's takes that from it.
    static var isActive: Bool {
        #if os(macOS)
        NSApplication.shared.isActive
        #else
        UIApplication.shared.applicationState == .active
        #endif
    }

    /// The app's own page of the system's settings.
    static var settingsURL: URL? {
        #if os(macOS)
        URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork")
        #else
        URL(string: UIApplication.openSettingsURLString)
        #endif
    }

    /// Where the system keeps whether the app may send notifications.
    static var notificationSettingsURL: URL? {
        #if os(macOS)
        URL(string: "x-apple.systempreferences:com.apple.Notifications-Settings.extension")
        #else
        URL(string: UIApplication.openNotificationSettingsURLString)
        #endif
    }
}

// MARK: - Pictures

extension PlatformImage {
    static func picture(_ image: CGImage) -> PlatformImage {
        #if os(macOS)
        NSImage(cgImage: image, size: CGSize(width: image.width, height: image.height))
        #else
        UIImage(cgImage: image)
        #endif
    }

    /// The picture as Core Graphics has it, whatever it was made from.
    var bitmap: CGImage? {
        #if os(macOS)
        cgImage(forProposedRect: nil, context: nil, hints: nil)
        #else
        cgImage
        #endif
    }

    /// The picture's size in its own pixels. A Mac measures a picture read from a file by the
    /// resolution the file claims, so one saved at 144 dots an inch says it is half as large.
    var pixelSize: CGSize {
        #if os(macOS)
        if let rep = representations.first, rep.pixelsWide > 0, rep.pixelsHigh > 0 {
            return CGSize(width: rep.pixelsWide, height: rep.pixelsHigh)
        }
        return size
        #else
        CGSize(width: size.width * scale, height: size.height * scale)
        #endif
    }

    /// Draws a picture of `size` pixels and gives it back as PNG.
    static func png(size: CGSize, opaque: Bool = false, _ draw: (CGContext) -> Void) -> Data? {
        render(size: size, opaque: opaque, draw).flatMap(\.pngRepresentation)
    }

    /// Draws a picture of `size` pixels, with the origin at the top left as UIKit has it.
    static func render(size: CGSize, opaque: Bool = false, _ draw: (CGContext) -> Void) -> PlatformImage? {
        let width = Int(size.width.rounded()), height = Int(size.height.rounded())
        guard width > 0, height > 0,
              let context = CGContext(data: nil, width: width, height: height, bitsPerComponent: 8, bytesPerRow: 0,
                                      space: CGColorSpace(name: CGColorSpace.sRGB)!,
                                      bitmapInfo: (opaque ? CGImageAlphaInfo.noneSkipLast : .premultipliedLast).rawValue)
        else { return nil }
        context.translateBy(x: 0, y: CGFloat(height))
        context.scaleBy(x: 1, y: -1)
        draw(context)
        return context.makeImage().map(picture)
    }

    /// Draws the picture into `rect` of a context whose origin is at the top left.
    func draw(in rect: CGRect, of context: CGContext) {
        #if os(macOS)
        guard let bitmap else { return }
        context.saveGState()
        // Core Graphics draws a picture bottom-up; turned over in place, it stands in a top-down context.
        context.translateBy(x: rect.minX, y: rect.maxY)
        context.scaleBy(x: 1, y: -1)
        context.draw(bitmap, in: CGRect(origin: .zero, size: rect.size))
        context.restoreGState()
        #else
        // UIKit's own drawing, which turns a photo the way its camera held it.
        UIGraphicsPushContext(context)
        draw(in: rect)
        UIGraphicsPopContext()
        #endif
    }

    /// Reads a photo from a file's bytes, the right way up. A camera writes the picture as
    /// its sensor lay and says beside it which way was up; UIKit carries that with the
    /// picture, and AppKit's bitmap does not, so there it is turned as it is read.
    static func photo(_ data: Data) -> PlatformImage? {
        #if os(macOS)
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int,
              let turned = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: max(width, height),
              ] as CFDictionary)
        else { return nil }
        return picture(turned)
        #else
        UIImage(data: data)
        #endif
    }

    func jpegRepresentation(quality: CGFloat) -> Data? {
        #if os(macOS)
        bitmap.flatMap { NSBitmapImageRep(cgImage: $0).representation(using: .jpeg, properties: [.compressionFactor: quality]) }
        #else
        jpegData(compressionQuality: quality)
        #endif
    }

    var pngRepresentation: Data? {
        #if os(macOS)
        bitmap.flatMap { NSBitmapImageRep(cgImage: $0).representation(using: .png, properties: [:]) }
        #else
        pngData()
        #endif
    }
}

extension Image {
    init(platform image: PlatformImage) {
        #if os(macOS)
        self.init(nsImage: image)
        #else
        self.init(uiImage: image)
        #endif
    }
}

// MARK: - Colours

nonisolated extension Color {
    init(platform color: PlatformColor) {
        #if os(macOS)
        self.init(nsColor: color)
        #else
        self.init(uiColor: color)
        #endif
    }

    /// The system's own colours, by what they are for. A Mac has a window where an iPhone has
    /// a page, and names them accordingly.
    static var pageBackground: Color {
        #if os(macOS)
        Color(nsColor: .windowBackgroundColor)
        #else
        Color(uiColor: .systemBackground)
        #endif
    }

    static var groupedBackground: Color {
        #if os(macOS)
        Color(nsColor: .windowBackgroundColor)
        #else
        Color(uiColor: .systemGroupedBackground)
        #endif
    }

    static var groupedRowBackground: Color {
        #if os(macOS)
        Color(nsColor: .controlBackgroundColor)
        #else
        Color(uiColor: .secondarySystemGroupedBackground)
        #endif
    }

    static var tertiaryLabel: Color {
        #if os(macOS)
        Color(nsColor: .tertiaryLabelColor)
        #else
        Color(uiColor: .tertiaryLabel)
        #endif
    }

    static var separator: Color {
        #if os(macOS)
        Color(nsColor: .separatorColor)
        #else
        Color(uiColor: .separator)
        #endif
    }

    static var tertiaryFill: Color {
        #if os(macOS)
        Color(nsColor: .tertiarySystemFill)
        #else
        Color(uiColor: .tertiarySystemFill)
        #endif
    }

    static var quaternaryFill: Color {
        #if os(macOS)
        Color(nsColor: .quaternarySystemFill)
        #else
        Color(uiColor: .quaternarySystemFill)
        #endif
    }
}

// MARK: - Type and drawing

#if os(macOS)
// UIKit's names for the colours text is written in, so that what styles a note says them once.
nonisolated extension NSColor {
    static var label: NSColor { .labelColor }
    static var secondaryLabel: NSColor { .secondaryLabelColor }
    static var tertiaryLabel: NSColor { .tertiaryLabelColor }
    static var tintColor: NSColor { .controlAccentColor }
}
#endif

nonisolated enum FontTrait { case bold, italic }

nonisolated extension PlatformFont {
    /// The same face, bold or italic as well as whatever it already is.
    func adding(_ trait: FontTrait) -> PlatformFont {
        #if os(macOS)
        let wanted = fontDescriptor.symbolicTraits.union(trait == .bold ? .bold : .italic)
        return NSFont(descriptor: fontDescriptor.withSymbolicTraits(wanted), size: pointSize) ?? self
        #else
        let wanted = fontDescriptor.symbolicTraits.union(trait == .bold ? .traitBold : .traitItalic)
        return UIFont(descriptor: fontDescriptor.withSymbolicTraits(wanted) ?? fontDescriptor, size: pointSize)
        #endif
    }

    /// The size text is read and written at: the system's body, which on a Mac is a size
    /// for labels, so a little over it there.
    static var writing: PlatformFont {
        #if os(macOS)
        .systemFont(ofSize: NSFont.systemFontSize + 1)
        #else
        .preferredFont(forTextStyle: .body)
        #endif
    }
}

nonisolated enum PlatformGraphics {
    /// The context being drawn into, inside a view's or a layout manager's drawing.
    static var current: CGContext? {
        #if os(macOS)
        NSGraphicsContext.current?.cgContext
        #else
        UIGraphicsGetCurrentContext()
        #endif
    }
}

// MARK: - Views of the kit's

/// A view of UIKit's or AppKit's in SwiftUI, written once: the two protocols ask for the same
/// two things under different names.
#if canImport(UIKit)
protocol PlatformViewRepresentable: UIViewRepresentable where UIViewType == ViewType {
    associatedtype ViewType: UIView
    @MainActor func makeView(context: Context) -> ViewType
    @MainActor func updateView(_ view: ViewType, context: Context)
}

extension PlatformViewRepresentable {
    func makeUIView(context: Context) -> ViewType { makeView(context: context) }
    func updateUIView(_ view: ViewType, context: Context) { updateView(view, context: context) }
}
#else
protocol PlatformViewRepresentable: NSViewRepresentable where NSViewType == ViewType {
    associatedtype ViewType: NSView
    @MainActor func makeView(context: Context) -> ViewType
    @MainActor func updateView(_ view: ViewType, context: Context)
}

extension PlatformViewRepresentable {
    func makeNSView(context: Context) -> ViewType { makeView(context: context) }
    func updateNSView(_ view: ViewType, context: Context) { updateView(view, context: context) }
}
#endif

extension PlatformView {
    /// The scroll view this view is in, if it is in one.
    var enclosingPlatformScrollView: PlatformScrollView? {
        var view = superview
        while let current = view {
            if let scroll = current as? PlatformScrollView { return scroll }
            view = current.superview
        }
        return nil
    }
}

extension View {
    /// The app's lists and panels were drawn for an iPad, where a button is its label in the
    /// accent and a menu's label says for itself that it opens. A Mac gives a button a bezel,
    /// and a menu an arrow of its own beside the one its label already draws; this has the Mac
    /// leave both off. The menu keeps the Mac's own style: told to be a plain button, it draws
    /// its label as a title and a picture and nothing else — a chip as bare text, a face at
    /// the size of its file. It is put on a list or a panel and not on the whole app: said
    /// over the window, it reaches the window's bar too, whose buttons are the system's to draw.
    func macControls() -> some View {
        #if os(macOS)
        buttonStyle(.borderless).menuIndicator(.hidden)
        #else
        self
        #endif
    }

    /// On a page that draws its own title in the middle of the bar — an icon beside the name.
    /// An iPhone's bar shows that in place of the page's title; a Mac's window shows both,
    /// unless told the title is taken care of.
    func titleIsInBar() -> some View {
        #if os(macOS)
        toolbar(removing: .title)
        #else
        self
        #endif
    }
}

extension View {
    /// On a window's content. A Mac's bar has no backing of its own: a page scrolls up under
    /// it only faintly dimmed, and is given something behind it when the pointer comes over
    /// the bar — so a title or a button with a card behind it was hard to read until then.
    /// The hard edge is the system's other way of drawing it: what is under the bar is
    /// frosted, down to a line, whenever anything is there. Asking the bar itself to show a
    /// background does nothing here; the edge is the scroll view's to draw.
    func windowBarBacked() -> some View {
        #if os(macOS)
        scrollEdgeEffectStyle(.hard, for: .top)
        #else
        self
        #endif
    }
}

extension ToolbarContent {
    /// On a page's own title in the middle of the bar. A Mac's bar puts glass behind each of
    /// its items, as behind a button; a title is not something to press, and stands bare.
    func bareOnMac() -> some ToolbarContent {
        #if os(macOS)
        sharedBackgroundVisibility(.hidden)
        #else
        self
        #endif
    }
}

extension View {
    /// On a small control drawn as its glyph alone — the add and the more of a lane's header,
    /// the tag of a sub-task. An iPad draws a button and a menu that way unasked; a Mac
    /// gives each a bezel, and a menu an arrow, that the glyph was not drawn to sit in.
    func bareControl() -> some View {
        #if os(macOS)
        buttonStyle(.plain).menuStyle(.button).menuIndicator(.hidden).foregroundStyle(.tint)
        #else
        self
        #endif
    }

    /// On a picker that is the whole of a menu. An iPad lists its choices in the menu; a Mac
    /// makes of it a menu within the menu, under the picker's name, for the one thing there
    /// is to choose.
    func listedInMenu() -> some View {
        #if os(macOS)
        pickerStyle(.inline).labelsHidden()
        #else
        self
        #endif
    }
}

// MARK: - Reading a page again

/// The pages that are read once when they are opened, and how each reads itself again. A
/// touch screen pulls such a page down to have it do so; a Mac has no pull, and has Reload
/// in its View menu instead, which asks every page showing.
@Observable
final class ReloadCenter {
    static let shared = ReloadCenter()
    @ObservationIgnored private var pages: [UUID: @MainActor () async -> Void] = [:]

    func add(_ id: UUID, _ action: @escaping @MainActor () async -> Void) { pages[id] = action }
    func remove(_ id: UUID) { pages[id] = nil }

    func reload() async {
        for action in pages.values { await action() }
    }
}

private struct Reloads: ViewModifier {
    var action: @MainActor () async -> Void
    @State private var id = UUID()

    func body(content: Content) -> some View {
        content
            .refreshable { await action() }
            #if os(macOS)
            .onAppear { ReloadCenter.shared.add(id, action) }
            .onDisappear { ReloadCenter.shared.remove(id) }
            #endif
    }
}

extension View {
    /// How this page reads itself again: by a pull on a touch screen, by Reload on a Mac.
    func reloads(_ action: @escaping @MainActor () async -> Void) -> some View {
        modifier(Reloads(action: action))
    }
}

// MARK: - What only a touch screen has

// An iPhone's pages say how their bar holds its title, which keyboard a field wants and how
// it capitalises. A Mac has no such bar and one keyboard, so there those are said to nobody:
// declared here under the same names, the pages need not say them twice.
#if os(macOS)
enum BarTitleDisplayMode { case automatic, inline, large }
enum TouchKeyboardType { case `default`, URL, emailAddress, numberPad, decimalPad, numbersAndPunctuation, asciiCapable }
enum TouchAutocapitalization { case never, words, sentences, characters }

extension View {
    func navigationBarTitleDisplayMode(_ mode: BarTitleDisplayMode) -> some View { self }
    func keyboardType(_ type: TouchKeyboardType) -> some View { self }
    func textInputAutocapitalization(_ autocapitalization: TouchAutocapitalization?) -> some View { self }
}

extension ToolbarItemPlacement {
    /// The bar's trailing end: on a Mac, where a window's main actions go.
    static var topBarTrailing: ToolbarItemPlacement { .primaryAction }
    static var topBarLeading: ToolbarItemPlacement { .navigation }
    /// A window has one bar, at its top.
    static var bottomBar: ToolbarItemPlacement { .automatic }
}

extension View {
    /// What covers the whole screen on an iPhone — a sketch being drawn, a picture being looked
    /// at — is a window of its own on a Mac (`CoverWindow`).
    func fullScreenCover<Item: Identifiable, Content: View>(item: Binding<Item?>, onDismiss: (() -> Void)? = nil,
                                                             @ViewBuilder content: @escaping (Item) -> Content) -> some View {
        modifier(WindowCover(item: item, onDismiss: onDismiss, cover: content))
    }
}

extension View {
    // An iPhone's sheet is the size the screen gives it. A Mac's is the size of what is in it,
    // and what is in most of these is a list or a page of text, which is as small as it is
    // let be: a sheet one line tall. So on a Mac a sheet is given room to start from.
    //
    // SwiftUI's own `sheet` takes an `onDismiss` too. A call that says none matches these
    // more closely than it matches SwiftUI's, which would have to fill one in — so the app's
    // sheets come through here without saying so, and SwiftUI's is reached by naming it.
    func sheet<Content: View>(isPresented: Binding<Bool>, @ViewBuilder content: @escaping () -> Content) -> some View {
        sheet(isPresented: isPresented, onDismiss: nil) { content().sheetSized() }
    }

    func sheet<Item: Identifiable, Content: View>(item: Binding<Item?>,
                                                 @ViewBuilder content: @escaping (Item) -> Content) -> some View {
        sheet(item: item, onDismiss: nil) { content($0).sheetSized() }
    }

    private func sheetSized() -> some View {
        frame(minWidth: 540, minHeight: 480)
    }
}

extension SearchFieldPlacement {
    /// A Mac's search field is in the window's toolbar and is always showing.
    enum DrawerDisplayMode { case automatic, always }
    static func navigationBarDrawer(displayMode: DrawerDisplayMode) -> SearchFieldPlacement { .toolbar }
}
#else
typealias TouchKeyboardType = UIKeyboardType
#endif
