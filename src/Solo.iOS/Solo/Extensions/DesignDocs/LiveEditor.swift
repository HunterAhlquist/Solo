import SwiftUI
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

// The display half of live editing: LiveMarkdown says what each run of a note
// is, and this turns that into what TextKit draws. The text view's text is
// always the note's exact Markdown — nothing is ever replaced or removed, only
// styled — which is what lets the same view stay bound to the shared document.

nonisolated extension NSAttributedString.Key {
    /// A decoration the layout manager draws behind a line: a quote's depth, or -1 for a code block.
    static let noteBlock = NSAttributedString.Key("solo.note.block")
    /// Where a run leads when tapped while the note isn't being typed in.
    static let noteLink = NSAttributedString.Key("solo.note.link")
    /// A list marker to draw as a bullet, whatever character it is written with.
    static let noteBullet = NSAttributedString.Key("solo.note.bullet")
    /// The raw embed target a line stands in for, where a picture is laid over it.
    static let noteMedia = NSAttributedString.Key("solo.note.media")
}

/// Draws what attributes can't: the bar down the side of a quote, and a code
/// block's background across the whole line rather than behind its glyphs.
nonisolated final class NoteLayoutManager: NSLayoutManager, NSLayoutManagerDelegate {
    /// Set and read on the main thread only, which is where a text view lays out and draws.
    nonisolated(unsafe) var accent: PlatformColor = .tintColor

    override init() {
        super.init()
        delegate = self
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    /// Swaps the glyph of a list marker for a bullet's. The character stays
    /// what was typed — only what is drawn for it changes — so the text, the
    /// caret and the shared document are untouched.
    func layoutManager(_ layoutManager: NSLayoutManager, shouldGenerateGlyphs glyphs: UnsafePointer<CGGlyph>,
                       properties: UnsafePointer<NSLayoutManager.GlyphProperty>, characterIndexes: UnsafePointer<Int>,
                       font: PlatformFont, forGlyphRange glyphRange: NSRange) -> Int {
        guard let storage = textStorage else { return 0 }
        var swapped: [CGGlyph]?
        for index in 0..<glyphRange.length {
            let character = characterIndexes[index]
            guard character < storage.length, storage.attribute(.noteBullet, at: character, effectiveRange: nil) != nil else { continue }
            var bullet: [UniChar] = [0x2022], glyph: [CGGlyph] = [0]
            guard CTFontGetGlyphsForCharacters(font as CTFont, &bullet, &glyph, 1) else { continue }
            if swapped == nil { swapped = Array(UnsafeBufferPointer(start: glyphs, count: glyphRange.length)) }
            swapped?[index] = glyph[0]
        }
        // Zero leaves the glyphs as they were generated.
        guard let swapped else { return 0 }
        swapped.withUnsafeBufferPointer { buffer in
            guard let base = buffer.baseAddress else { return }
            setGlyphs(base, properties: properties, characterIndexes: characterIndexes, font: font, forGlyphRange: glyphRange)
        }
        return glyphRange.length
    }

    override func drawBackground(forGlyphRange glyphsToShow: NSRange, at origin: CGPoint) {
        super.drawBackground(forGlyphRange: glyphsToShow, at: origin)
        guard let storage = textStorage, let container = textContainers.first, let context = PlatformGraphics.current
        else { return }
        let padding = container.lineFragmentPadding
        let characters = characterRange(forGlyphRange: glyphsToShow, actualGlyphRange: nil)
        storage.enumerateAttribute(.noteBlock, in: characters) { value, range, _ in
            guard let block = (value as? NSNumber)?.intValue, block != 0 else { return }
            let glyphs = glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            enumerateLineFragments(forGlyphRange: glyphs) { rect, _, _, _, _ in
                let row = rect.offsetBy(dx: origin.x, dy: origin.y)
                if block < 0 {
                    context.setFillColor(PlatformColor.tertiarySystemFill.cgColor)
                    context.fill(CGRect(x: row.minX + padding, y: row.minY, width: row.width - padding * 2, height: row.height))
                } else {
                    context.setFillColor(self.accent.withAlphaComponent(0.6).cgColor)
                    for level in 0..<block {
                        context.fill(CGRect(x: row.minX + padding + CGFloat(level) * NoteMetrics.quoteIndent, y: row.minY,
                                            width: 3, height: row.height))
                    }
                }
            }
        }
    }
}

nonisolated enum NoteMetrics {
    /// How far each level of quotation moves its text in, and its bar with it.
    static let quoteIndent: CGFloat = 16
}

/// Turns spans into attributes, and puts them on a text storage changing only what differs.
final class NoteStyler {
    private static let headingScale: [CGFloat] = [1.7, 1.42, 1.22, 1.1, 1, 1]

    var accent: PlatformColor = .tintColor
    private(set) var body = PlatformFont.writing

    /// Dynamic Type changed: the next pass uses the new size.
    func refreshFonts() {
        body = .writing
    }

    private func paragraph() -> NSMutableParagraphStyle {
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 3
        style.paragraphSpacing = 2
        return style
    }

    /// What plain text looks like, and what is typed before the next pass styles it.
    var base: [NSAttributedString.Key: Any] {
        [.font: body, .foregroundColor: PlatformColor.label, .paragraphStyle: paragraph()]
    }

    /// Syntax folded away: too small to see or to take room, without changing the text.
    private var hidden: [NSAttributedString.Key: Any] {
        [.font: PlatformFont.systemFont(ofSize: 0.1), .foregroundColor: PlatformColor.clear]
    }

    private func rank(_ kind: LiveSpan.Kind) -> Int {
        switch kind {
        case .heading, .codeBlock, .table, .quote, .listItem: 0
        case .hidden, .foldedFence, .media: 2
        default: 1
        }
    }

    /// The whole note as it should look. `mediaSize` answers how big an
    /// embed's picture is drawn, or nil when it can't be drawn at all.
    func styled(_ text: String, spans: [LiveSpan], mediaSize: (String) -> CGSize?) -> NSAttributedString {
        let result = NSMutableAttributedString(string: text, attributes: base)
        let source = text as NSString

        func paragraphs(_ range: NSRange, _ change: (NSMutableParagraphStyle) -> Void) {
            result.enumerateAttribute(.paragraphStyle, in: range) { value, part, _ in
                let style = ((value as? NSParagraphStyle)?.mutableCopy() as? NSMutableParagraphStyle) ?? paragraph()
                change(style)
                result.addAttribute(.paragraphStyle, value: style, range: part)
            }
        }
        func traits(_ added: FontTrait, _ range: NSRange) {
            result.enumerateAttribute(.font, in: range) { value, part, _ in
                guard let font = value as? PlatformFont else { return }
                result.addAttribute(.font, value: font.adding(added), range: part)
            }
        }
        func monospaced(_ range: NSRange, scale: CGFloat) {
            result.enumerateAttribute(.font, in: range) { value, part, _ in
                let size = ((value as? PlatformFont)?.pointSize ?? body.pointSize) * scale
                result.addAttribute(.font, value: PlatformFont.monospacedSystemFont(ofSize: size, weight: .regular), range: part)
            }
        }

        // Stable, so spans of one rank keep the order the tree gave them.
        let ordered = spans.enumerated().sorted { (rank($0.element.kind), $0.offset) < (rank($1.element.kind), $1.offset) }
        for (_, span) in ordered {
            let range = span.range
            switch span.kind {
            case .heading(let level):
                let size = body.pointSize * Self.headingScale[min(max(level, 1), 6) - 1]
                result.addAttribute(.font, value: PlatformFont.systemFont(ofSize: size, weight: .bold), range: range)
                paragraphs(range) { $0.paragraphSpacingBefore = self.body.pointSize * 0.35 }
            case .codeBlock:
                monospaced(range, scale: 0.9)
                result.addAttribute(.noteBlock, value: NSNumber(value: -1), range: range)
                paragraphs(range) {
                    $0.firstLineHeadIndent += 10
                    $0.headIndent += 10
                    $0.lineSpacing = 1
                    $0.paragraphSpacing = 0
                }
            case .table:
                monospaced(range, scale: 0.9)
            case .quote(let depth):
                result.addAttribute(.noteBlock, value: NSNumber(value: depth), range: range)
                result.addAttribute(.foregroundColor, value: PlatformColor.secondaryLabel, range: range)
                paragraphs(range) {
                    $0.firstLineHeadIndent = NoteMetrics.quoteIndent * CGFloat(depth)
                    $0.headIndent = NoteMetrics.quoteIndent * CGFloat(depth)
                }
            case .listItem(let prefix):
                // Wrapped rows of an item line up under its text, not under its marker.
                let lead = source.substring(with: NSRange(location: range.location, length: min(prefix, range.length)))
                let width = (lead as NSString).size(withAttributes: [.font: body]).width
                paragraphs(range) { $0.headIndent = $0.firstLineHeadIndent + width }
            case .strong:
                traits(.bold, range)
            case .emphasis:
                traits(.italic, range)
            case .strike:
                result.addAttribute(.strikethroughStyle, value: NSUnderlineStyle.single.rawValue, range: range)
                result.addAttribute(.foregroundColor, value: PlatformColor.secondaryLabel, range: range)
            case .highlight:
                result.addAttribute(.backgroundColor, value: PlatformColor.systemYellow.withAlphaComponent(0.35), range: range)
            case .code:
                monospaced(range, scale: 0.92)
                result.addAttribute(.backgroundColor, value: PlatformColor.tertiarySystemFill, range: range)
            case .link:
                result.addAttribute(.foregroundColor, value: accent, range: range)
                result.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: range)
            case .destination(let address):
                result.addAttribute(.noteLink, value: address as NSString, range: range)
                // A Mac's pointer says what it is over.
                #if os(macOS)
                result.addAttribute(.cursor, value: NSCursor.pointingHand, range: range)
                #endif
            case .bullet:
                result.addAttribute(.foregroundColor, value: accent, range: range)
            case .dot:
                result.addAttribute(.noteBullet, value: true, range: range)
            case .syntax:
                result.addAttribute(.foregroundColor, value: PlatformColor.tertiaryLabel, range: range)
            case .embed:
                monospaced(range, scale: 0.85)
                result.addAttribute(.foregroundColor, value: PlatformColor.secondaryLabel, range: range)
            case .hidden:
                result.removeAttribute(.underlineStyle, range: range)
                result.removeAttribute(.strikethroughStyle, range: range)
                result.removeAttribute(.backgroundColor, range: range)
                result.addAttributes(hidden, range: range)
            case .foldedFence:
                // Folded, the fence's line is the padding at the block's top or bottom edge.
                result.addAttributes(hidden, range: range)
                paragraphs(range) {
                    $0.minimumLineHeight = 8
                    $0.maximumLineHeight = 8
                }
            case .media(let target):
                guard let size = mediaSize(target) else {
                    monospaced(range, scale: 0.85)
                    result.addAttribute(.foregroundColor, value: PlatformColor.secondaryLabel, range: range)
                    continue
                }
                // The row's text folds away and its line is made as tall as
                // the pictures, which the editor then lays over it.
                result.addAttributes(hidden, range: range)
                result.addAttribute(.noteMedia, value: target as NSString, range: range)
                paragraphs(range) {
                    $0.minimumLineHeight = size.height + 8
                    $0.maximumLineHeight = size.height + 8
                }
            }
        }
        return result
    }

    /// Makes the storage look like `desired`, touching only the runs that
    /// differ: an attribute change relays out its lines, and a pass runs on
    /// every keystroke and every move of the caret.
    func apply(_ desired: NSAttributedString, to storage: NSTextStorage) {
        guard desired.length == storage.length else { return }
        storage.beginEditing()
        desired.enumerateAttributes(in: NSRange(location: 0, length: desired.length)) { wanted, range, _ in
            var cursor = range.location
            while cursor < NSMaxRange(range) {
                var run = NSRange()
                let current = storage.attributes(at: cursor, longestEffectiveRange: &run,
                                                 in: NSRange(location: cursor, length: NSMaxRange(range) - cursor))
                if !(current as NSDictionary).isEqual(to: wanted) {
                    storage.setAttributes(wanted, range: run)
                }
                cursor = NSMaxRange(run)
            }
        }
        storage.endEditing()
    }
}

#if os(iOS)
/// A text view that says when its width changes, which is when embeds have
/// to be measured again, and remembers where it was last touched — which is
/// how a tap on a link is told from a tap to start typing.
final class NoteTextView: UITextView {
    var onWidthChange: (() -> Void)?
    private(set) var lastTouch: (point: CGPoint, at: Date)?
    private var lastWidth: CGFloat = 0
    let placeholder = UILabel()

    override func hitTest(_ point: CGPoint, with event: UIEvent?) -> UIView? {
        let hit = super.hitTest(point, with: event)
        if hit === self { lastTouch = (point, Date()) }
        return hit
    }

    func forgetTouch() {
        lastTouch = nil
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        if abs(bounds.width - lastWidth) > 0.5 {
            lastWidth = bounds.width
            onWidthChange?()
            restoreAnchor()
        }
    }

    // MARK: Keeping the place

    // A change of width wraps every line differently, so the same scroll offset shows
    // somewhere else in the note. What the reader was looking at is remembered as the
    // character at the top of the view, and put back there once the text has been laid out
    // at the new width.

    /// The character whose line is at the top of what shows, and how far into that line the top is.
    private var anchor: (character: Int, into: CGFloat)?
    private var restoring = false

    override var contentOffset: CGPoint {
        didSet {
            // Only while the layout is for the width the view has: in between, an offset says
            // nothing about which line is where.
            if !restoring, abs(bounds.width - lastWidth) <= 0.5 { noteAnchor() }
        }
    }

    private func noteAnchor() {
        let top = contentOffset.y + adjustedContentInset.top - textContainerInset.top
        // At the head of the note, where its name is, there is nothing to hold on to: it stays at the top.
        guard textStorage.length > 0, top > 0 else {
            anchor = nil
            return
        }
        let character = layoutManager.characterIndex(for: CGPoint(x: 4, y: top), in: textContainer,
                                                     fractionOfDistanceBetweenInsertionPoints: nil)
        let line = layoutManager.lineFragmentRect(forGlyphAt: layoutManager.glyphIndexForCharacter(at: character), effectiveRange: nil)
        anchor = (character, max(top - line.minY, 0))
    }

    private func restoreAnchor() {
        guard let anchor, anchor.character < textStorage.length else { return }
        layoutManager.ensureLayout(forCharacterRange: NSRange(location: 0, length: anchor.character + 1))
        let line = layoutManager.lineFragmentRect(forGlyphAt: layoutManager.glyphIndexForCharacter(at: anchor.character), effectiveRange: nil)
        let lowest = -adjustedContentInset.top
        let highest = max(lowest, contentSize.height + adjustedContentInset.bottom - bounds.height)
        let wanted = line.minY + min(anchor.into, line.height) + textContainerInset.top - adjustedContentInset.top
        restoring = true
        contentOffset.y = min(max(wanted, lowest), highest)
        restoring = false
    }
}
#endif

/// Where the editor gets a row of pictures from: how much room one takes,
/// and the view that draws it. Both are asked with the row's own text — a
/// line holding nothing but embeds — and answer nil when it can't be drawn.
struct NoteEditorMedia {
    var size: (String) -> CGSize?
    var row: (String) -> AnyView?
}
