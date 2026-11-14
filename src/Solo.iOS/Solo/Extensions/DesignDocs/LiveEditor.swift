import SwiftUI
import UIKit

// The display half of live editing: LiveMarkdown says what each run of a note
// is, and this turns that into what TextKit draws. The text view's text is
// always the note's exact Markdown — nothing is ever replaced or removed, only
// styled — which is what lets the same view stay bound to the shared document.

nonisolated extension NSAttributedString.Key {
    /// A decoration the layout manager draws behind a line: a quote's depth, or -1 for a code block.
    static let noteBlock = NSAttributedString.Key("solo.note.block")
    /// The raw embed target a line stands in for, where a picture is laid over it.
    static let noteMedia = NSAttributedString.Key("solo.note.media")
}

/// Draws what attributes can't: the bar down the side of a quote, and a code
/// block's background across the whole line rather than behind its glyphs.
nonisolated final class NoteLayoutManager: NSLayoutManager {
    /// Set and read on the main thread only, which is where a text view lays out and draws.
    nonisolated(unsafe) var accent: UIColor = .tintColor

    override func drawBackground(forGlyphRange glyphsToShow: NSRange, at origin: CGPoint) {
        super.drawBackground(forGlyphRange: glyphsToShow, at: origin)
        guard let storage = textStorage, let container = textContainers.first, let context = UIGraphicsGetCurrentContext()
        else { return }
        let padding = container.lineFragmentPadding
        let characters = characterRange(forGlyphRange: glyphsToShow, actualGlyphRange: nil)
        storage.enumerateAttribute(.noteBlock, in: characters) { value, range, _ in
            guard let block = (value as? NSNumber)?.intValue, block != 0 else { return }
            let glyphs = glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            enumerateLineFragments(forGlyphRange: glyphs) { rect, _, _, _, _ in
                let row = rect.offsetBy(dx: origin.x, dy: origin.y)
                if block < 0 {
                    context.setFillColor(UIColor.tertiarySystemFill.cgColor)
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

    var accent: UIColor = .tintColor
    private(set) var body = UIFont.preferredFont(forTextStyle: .body)

    /// Dynamic Type changed: the next pass uses the new size.
    func refreshFonts() {
        body = .preferredFont(forTextStyle: .body)
    }

    private func paragraph() -> NSMutableParagraphStyle {
        let style = NSMutableParagraphStyle()
        style.lineSpacing = 3
        style.paragraphSpacing = 2
        return style
    }

    /// What plain text looks like, and what is typed before the next pass styles it.
    var base: [NSAttributedString.Key: Any] {
        [.font: body, .foregroundColor: UIColor.label, .paragraphStyle: paragraph()]
    }

    /// Syntax folded away: too small to see or to take room, without changing the text.
    private var hidden: [NSAttributedString.Key: Any] {
        [.font: UIFont.systemFont(ofSize: 0.1), .foregroundColor: UIColor.clear]
    }

    private func rank(_ kind: LiveSpan.Kind) -> Int {
        switch kind {
        case .heading, .codeBlock, .table, .quote, .listItem: 0
        case .hidden, .media: 2
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
        func traits(_ added: UIFontDescriptor.SymbolicTraits, _ range: NSRange) {
            result.enumerateAttribute(.font, in: range) { value, part, _ in
                guard let font = value as? UIFont else { return }
                let wanted = font.fontDescriptor.symbolicTraits.union(added)
                let descriptor = font.fontDescriptor.withSymbolicTraits(wanted) ?? font.fontDescriptor
                result.addAttribute(.font, value: UIFont(descriptor: descriptor, size: font.pointSize), range: part)
            }
        }
        func monospaced(_ range: NSRange, scale: CGFloat) {
            result.enumerateAttribute(.font, in: range) { value, part, _ in
                let size = ((value as? UIFont)?.pointSize ?? body.pointSize) * scale
                result.addAttribute(.font, value: UIFont.monospacedSystemFont(ofSize: size, weight: .regular), range: part)
            }
        }

        // Stable, so spans of one rank keep the order the tree gave them.
        let ordered = spans.enumerated().sorted { (rank($0.element.kind), $0.offset) < (rank($1.element.kind), $1.offset) }
        for (_, span) in ordered {
            let range = span.range
            switch span.kind {
            case .heading(let level):
                let size = body.pointSize * Self.headingScale[min(max(level, 1), 6) - 1]
                result.addAttribute(.font, value: UIFont.systemFont(ofSize: size, weight: .bold), range: range)
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
                result.addAttribute(.foregroundColor, value: UIColor.secondaryLabel, range: range)
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
                traits(.traitBold, range)
            case .emphasis:
                traits(.traitItalic, range)
            case .strike:
                result.addAttribute(.strikethroughStyle, value: NSUnderlineStyle.single.rawValue, range: range)
                result.addAttribute(.foregroundColor, value: UIColor.secondaryLabel, range: range)
            case .highlight:
                result.addAttribute(.backgroundColor, value: UIColor.systemYellow.withAlphaComponent(0.35), range: range)
            case .code:
                monospaced(range, scale: 0.92)
                result.addAttribute(.backgroundColor, value: UIColor.tertiarySystemFill, range: range)
            case .link:
                result.addAttribute(.foregroundColor, value: accent, range: range)
                result.addAttribute(.underlineStyle, value: NSUnderlineStyle.single.rawValue, range: range)
            case .bullet:
                result.addAttribute(.foregroundColor, value: accent, range: range)
            case .syntax:
                result.addAttribute(.foregroundColor, value: UIColor.tertiaryLabel, range: range)
            case .embed:
                monospaced(range, scale: 0.85)
                result.addAttribute(.foregroundColor, value: UIColor.secondaryLabel, range: range)
            case .hidden:
                result.removeAttribute(.underlineStyle, range: range)
                result.removeAttribute(.strikethroughStyle, range: range)
                result.removeAttribute(.backgroundColor, range: range)
                result.addAttributes(hidden, range: range)
            case .media(let target):
                guard let size = mediaSize(target) else {
                    monospaced(range, scale: 0.85)
                    result.addAttribute(.foregroundColor, value: UIColor.secondaryLabel, range: range)
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

/// A text view that says when its width changes, which is when embeds have to be measured again.
final class NoteTextView: UITextView {
    var onWidthChange: (() -> Void)?
    private var lastWidth: CGFloat = 0

    override func layoutSubviews() {
        super.layoutSubviews()
        if abs(bounds.width - lastWidth) > 0.5 {
            lastWidth = bounds.width
            onWidthChange?()
        }
    }
}

/// Where the editor gets a row of pictures from: how much room one takes,
/// and the view that draws it. Both are asked with the row's own text — a
/// line holding nothing but embeds — and answer nil when it can't be drawn.
struct NoteEditorMedia {
    var size: (String) -> CGSize?
    var row: (String) -> AnyView?
}
