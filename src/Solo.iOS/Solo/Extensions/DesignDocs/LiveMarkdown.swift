import Foundation
import Markdown

// What a note's Markdown looks like while it is being edited: which runs are
// bold, which lines are headings, and which pieces of syntax step out of the
// way until the caret comes near — the editor's "live preview".
//
// The structure comes from swift-markdown's syntax tree, the same CommonMark
// and GFM grammar the web editor parses with, so the two agree on what is a
// heading or a list. The three things a note adds to Markdown — `[[links]]`,
// `![[embeds]]` and `==highlights==` — are found on top of it.

/// One styled run of a note's source.
nonisolated struct LiveSpan: Equatable, Sendable {
    enum Kind: Equatable, Sendable {
        case heading(Int)
        case strong, emphasis, strike, highlight
        /// Inline code, its backticks included.
        case code
        /// The lines of a fenced or indented block, fences included.
        case codeBlock
        case table
        case link
        /// Where a link leads, over the same run as its `.link`: an address, or `wiki:` and a note's name.
        case destination(String)
        /// The lines of a block quote, with how deeply it is nested.
        case quote(Int)
        /// A list item's first line; `prefix` is how far in its text starts, for wrapped lines to hang from.
        case listItem(prefix: Int)
        case bullet
        /// An unordered item's `-`, `*` or `+`, drawn as a bullet while the caret is elsewhere.
        case dot
        /// A task item's `[ ]` or `[x]`, drawn as a checkbox while the caret is elsewhere.
        case checkbox(Bool)
        /// The first line of a task item whose box is drawn; `shown` is how much of the line
        /// still shows before the box, for wrapped lines to hang from the text after it.
        case taskLine(shown: Int)
        /// Syntax left on show, dimmed.
        case syntax
        /// Syntax not shown while the caret is elsewhere.
        case hidden
        /// A code block's fence while the caret is outside the block: not shown, but its line stays as the block's edge.
        case foldedFence
        /// An embed's own text.
        case embed
        /// A line that is nothing but embeds, which can be drawn in its place: the line's own text.
        case media(String)
    }

    var range: NSRange
    var kind: Kind
}

/// Turns the tree's line-and-column positions, which count UTF-8 bytes, into
/// the UTF-16 offsets a text view counts in.
nonisolated struct SourceMap {
    let text: NSString
    /// Each line's start and the end of its contents, before the line break.
    private(set) var lines: [(start: Int, end: Int, ascii: Bool)] = []

    init(_ text: NSString) {
        self.text = text
        var start = 0, index = 0, ascii = true
        let length = text.length
        // Split as cmark splits: on \n, \r\n and \r, and on nothing else.
        while index < length {
            let unit = text.character(at: index)
            if unit == 10 || unit == 13 {
                lines.append((start, index, ascii))
                index += unit == 13 && index + 1 < length && text.character(at: index + 1) == 10 ? 2 : 1
                start = index
                ascii = true
            } else {
                if unit >= 0x80 { ascii = false }
                index += 1
            }
        }
        lines.append((start, length, ascii))
    }

    func offset(_ location: SourceLocation) -> Int {
        guard location.line >= 1 else { return 0 }
        guard location.line <= lines.count else { return text.length }
        let line = lines[location.line - 1]
        let wanted = max(location.column - 1, 0)
        if line.ascii { return min(line.start + wanted, line.end) }
        var bytes = 0, index = line.start
        while index < line.end, bytes < wanted {
            let unit = text.character(at: index)
            if UTF16.isLeadSurrogate(unit), index + 1 < line.end {
                bytes += 4
                index += 2
            } else {
                bytes += unit < 0x80 ? 1 : unit < 0x800 ? 2 : 3
                index += 1
            }
        }
        return index
    }

    func range(_ node: Markup) -> NSRange? {
        guard let range = node.range else { return nil }
        let start = offset(range.lowerBound), end = offset(range.upperBound)
        return end >= start ? NSRange(location: start, length: end - start) : nil
    }

    /// The correction for where the parser says a paragraph's inline nodes are.
    ///
    /// On every line after a paragraph's first the parser counts columns from
    /// where that line's own text begins — past any quote marks and indent —
    /// and then adds how far in the *first* line's text began. The two agree
    /// when every line is indented alike, and not otherwise: most often on a
    /// line that continues a list item or a quote without its indent or its
    /// `>`, where everything is reported that much to the right of where it is.
    final class InlineFix {
        let firstLine: Int
        /// How far in the paragraph's first line its text begins.
        let columnOffset: Int
        /// How many block quotes the paragraph is inside.
        let quotes: Int
        fileprivate var prefixes: [Int: Int] = [:]

        init?(_ block: Markup, quotes: Int) {
            guard let start = block.range?.lowerBound else { return nil }
            firstLine = start.line
            columnOffset = start.column - 1
            self.quotes = quotes
        }
    }

    /// How many characters of a line come before the paragraph's own text on
    /// it: the marks of the quotes it is in, and the white space it is indented by.
    private func prefix(ofLine number: Int, fix: InlineFix) -> Int {
        if let known = fix.prefixes[number] { return known }
        let line = lines[number - 1]
        var cursor = line.start
        func skipSpace() {
            while cursor < line.end, [32, 9].contains(text.character(at: cursor)) { cursor += 1 }
        }
        // A line may leave off the marks of the quotes it continues, but only from the inside out.
        for _ in 0..<fix.quotes {
            skipSpace()
            guard cursor < line.end, text.character(at: cursor) == 62 else { break }
            cursor += 1
        }
        skipSpace()
        fix.prefixes[number] = cursor - line.start
        return cursor - line.start
    }

    private func corrected(_ location: SourceLocation, fix: InlineFix) -> SourceLocation {
        guard location.line > fix.firstLine, location.line <= lines.count else { return location }
        var moved = location
        moved.column = max(location.column - fix.columnOffset + prefix(ofLine: location.line, fix: fix), 1)
        return moved
    }

    /// An inline node's range, corrected for the block it is in.
    func range(_ node: Markup, fix: InlineFix?) -> NSRange? {
        guard let fix else { return range(node) }
        guard let range = node.range else { return nil }
        let start = offset(corrected(range.lowerBound, fix: fix)), end = offset(corrected(range.upperBound, fix: fix))
        return end >= start ? NSRange(location: start, length: end - start) : nil
    }

    private func index(at offset: Int) -> Int {
        var low = 0, high = lines.count - 1
        while low < high {
            let middle = (low + high + 1) / 2
            if lines[middle].start <= offset { low = middle } else { high = middle - 1 }
        }
        return low
    }

    /// The contents of the line an offset is on.
    func line(at offset: Int) -> NSRange {
        let line = lines[index(at: offset)]
        return NSRange(location: line.start, length: line.end - line.start)
    }

    /// The contents of every line a range touches.
    func lines(in range: NSRange) -> [NSRange] {
        let first = index(at: range.location), last = index(at: max(range.location, NSMaxRange(range) - 1))
        return lines[first...last].map { NSRange(location: $0.start, length: $0.end - $0.start) }
    }
}

/// A note's source, parsed once, from which the spans for any caret position are read.
nonisolated struct LiveMarkdown {
    let source: NSString
    private let string: String
    private let map: SourceMap
    private let document: Document

    init(_ source: String) {
        string = source
        self.source = source as NSString
        map = SourceMap(source as NSString)
        document = Document(parsing: source, options: [])
    }

    private static func regex(_ pattern: String) -> NSRegularExpression {
        // The patterns are literals below; one that didn't compile would be a mistake in this file.
        (try? NSRegularExpression(pattern: pattern)) ?? NSRegularExpression()
    }

    nonisolated(unsafe) private static let atx = regex(#"^ {0,3}#{1,6}(?:[ \t]+|$)"#)
    nonisolated(unsafe) private static let quoteMark = regex(#"^ {0,3}(?:>[ \t]?)+"#)
    nonisolated(unsafe) private static let wikiEmbed = regex(#"!\[\[([^\]\n]+)\]\]"#)
    nonisolated(unsafe) private static let wiki = regex(#"(?<!!)\[\[([^\]\n]+)\]\]"#)
    nonisolated(unsafe) private static let highlight = regex(#"(?<!=)==(?=[^\s=])([^\n]+?)(?<=[^\s=])==(?!=)"#)
    /// A task item's marker: the list's own, then the box.
    nonisolated(unsafe) private static let task = regex(#"^(?:[-*+]|\d{1,9}[.)])[ \t]+(\[[ xX]\])(?=[ \t])"#)

    /// The spans for the whole note. `selection` is the caret or the selected
    /// range; nil — a note that isn't being typed in — leaves all syntax hidden.
    func spans(selection: NSRange?) -> [LiveSpan] {
        var out: [LiveSpan] = []
        var code: [NSRange] = []
        var embeds: [(range: NSRange, target: String)] = []
        let full = NSRange(location: 0, length: source.length)

        func touches(_ range: NSRange) -> Bool {
            guard let selection else { return false }
            return selection.location <= NSMaxRange(range) && NSMaxRange(selection) >= range.location
        }
        /// A block's range without the line break that ends it.
        func trimmed(_ range: NSRange) -> NSRange {
            var end = NSMaxRange(range)
            while end > range.location, [10, 13].contains(source.character(at: end - 1)) { end -= 1 }
            return NSRange(location: range.location, length: end - range.location)
        }
        func marker(_ range: NSRange, shown: Bool) {
            guard range.length > 0 else { return }
            out.append(LiveSpan(range: range, kind: shown ? .syntax : .hidden))
        }
        // The correction for the paragraph the walk is in.
        var fix: SourceMap.InlineFix?
        /// An inline node's place in the source.
        func inline(_ node: Markup) -> NSRange? { map.range(node, fix: fix) }

        /// The markers either side of an inline span, measured from where its contents begin and end.
        func delimiters(of node: Markup, _ range: NSRange, fallback: Int) {
            let children = node.children.compactMap { inline($0) }
            let open = (children.first?.location ?? range.location + fallback) - range.location
            let close = NSMaxRange(range) - (children.last.map(NSMaxRange) ?? NSMaxRange(range) - fallback)
            guard open > 0, close > 0, open + close <= range.length else { return }
            let shown = touches(range)
            marker(NSRange(location: range.location, length: open), shown: shown)
            marker(NSRange(location: NSMaxRange(range) - close, length: close), shown: shown)
        }

        func walk(_ node: Markup, quoteDepth: Int) {
            var depth = quoteDepth
            switch node {
            case let heading as Heading:
                guard let whole = map.range(heading).map(trimmed), whole.length > 0 else { break }
                let lines = map.lines(in: whole)
                let shown = touches(whole)
                if let first = lines.first, let match = Self.atx.firstMatch(in: string, range: first) {
                    out.append(LiveSpan(range: whole, kind: .heading(heading.level)))
                    marker(match.range, shown: shown)
                } else if lines.count > 1, let underline = lines.last {
                    // A setext heading: the text, then a line of = or - under it.
                    let above = lines[lines.count - 2]
                    let title = NSRange(location: whole.location, length: NSMaxRange(above) - whole.location)
                    out.append(LiveSpan(range: title, kind: .heading(heading.level)))
                    marker(underline, shown: shown)
                }

            case is Strong:
                guard let range = inline(node) else { break }
                out.append(LiveSpan(range: range, kind: .strong))
                delimiters(of: node, range, fallback: 2)

            case is Emphasis:
                guard let range = inline(node) else { break }
                out.append(LiveSpan(range: range, kind: .emphasis))
                delimiters(of: node, range, fallback: 1)

            case is Strikethrough:
                guard let range = inline(node) else { break }
                out.append(LiveSpan(range: range, kind: .strike))
                delimiters(of: node, range, fallback: 2)

            case is InlineCode:
                guard let range = inline(node), range.length > 1 else { break }
                out.append(LiveSpan(range: range, kind: .code))
                code.append(range)
                var ticks = 0
                while ticks < range.length / 2, source.character(at: range.location + ticks) == 96 { ticks += 1 }
                if ticks > 0, ticks * 2 < range.length {
                    let shown = touches(range)
                    marker(NSRange(location: range.location, length: ticks), shown: shown)
                    marker(NSRange(location: NSMaxRange(range) - ticks, length: ticks), shown: shown)
                }
                return

            case let link as Link:
                guard let range = inline(node), range.length > 0 else { break }
                let children = node.children.compactMap { inline($0) }
                let address = link.destination ?? ""
                // `[label](address)` shows as its label. A bare address has nothing to fold away.
                if source.character(at: range.location) == 91, let first = children.first, let last = children.last {
                    let label = NSRange(location: first.location, length: NSMaxRange(last) - first.location)
                    out.append(LiveSpan(range: label, kind: .link))
                    out.append(LiveSpan(range: label, kind: .destination(address)))
                    let shown = touches(range)
                    marker(NSRange(location: range.location, length: label.location - range.location), shown: shown)
                    marker(NSRange(location: NSMaxRange(label), length: NSMaxRange(range) - NSMaxRange(label)), shown: shown)
                } else {
                    out.append(LiveSpan(range: range, kind: .link))
                    out.append(LiveSpan(range: range, kind: .destination(address)))
                }

            case let image as Image:
                if let range = inline(image) {
                    embeds.append((range, image.source ?? ""))
                }
                return

            case is ListItem:
                guard let range = map.range(node) else { break }
                let line = map.line(at: range.location)
                // The marker runs up to the item's text: "- ", "12. ", "- [ ] ".
                var text = node.child(at: 0).flatMap { map.range($0) }?.location ?? NSMaxRange(line)
                if text > NSMaxRange(line) || text <= range.location { text = NSMaxRange(line) }
                out.append(LiveSpan(range: line, kind: .listItem(prefix: text - line.location)))
                let lead = NSRange(location: range.location, length: text - range.location)
                let unordered = [45, 42, 43].contains(source.character(at: range.location))
                // A task: `- [ ] ` is a box, and the box is all of it that shows. With the caret
                // anywhere in it — up to the box's own end, not the space after — it is the
                // text it is written as, to be changed or taken out by hand.
                let rest = NSRange(location: range.location, length: NSMaxRange(line) - range.location)
                if (node as? ListItem)?.checkbox != nil,
                   let match = Self.task.firstMatch(in: string, options: .anchored, range: rest) {
                    let box = match.range(at: 1)
                    let written = NSRange(location: range.location, length: NSMaxRange(box) - range.location)
                    if !touches(written) {
                        let before = NSRange(location: range.location, length: box.location - range.location)
                        // A numbered task keeps its number; a bulleted one has the box for a bullet.
                        if unordered {
                            marker(before, shown: false)
                        } else {
                            out.append(LiveSpan(range: before, kind: .bullet))
                        }
                        out.append(LiveSpan(range: line, kind: .taskLine(shown: (unordered ? range.location : box.location) - line.location)))
                        out.append(LiveSpan(range: box, kind: .checkbox(source.character(at: box.location + 1) != 32)))
                        break
                    }
                }
                out.append(LiveSpan(range: lead, kind: .bullet))
                let mark = NSRange(location: range.location, length: 1)
                if unordered, !touches(mark) {
                    out.append(LiveSpan(range: mark, kind: .dot))
                }

            case is BlockQuote:
                guard let whole = map.range(node).map(trimmed), whole.length > 0 else { break }
                depth += 1
                out.append(LiveSpan(range: whole, kind: .quote(depth)))
                if depth == 1 {
                    for line in map.lines(in: whole) {
                        if let match = Self.quoteMark.firstMatch(in: string, range: line) {
                            marker(match.range, shown: touches(line))
                        }
                    }
                }

            case is CodeBlock:
                guard let whole = map.range(node).map(trimmed), whole.length > 0 else { return }
                out.append(LiveSpan(range: whole, kind: .codeBlock))
                code.append(whole)
                let lines = map.lines(in: whole)
                if let first = lines.first, lines.count > 1 {
                    let opener = source.substring(with: first).trimmingCharacters(in: .whitespaces)
                    // Both fences show while the caret is anywhere in the block — the code
                    // being worked on is seen whole — and fold to a sliver of its background otherwise.
                    if opener.hasPrefix("```") || opener.hasPrefix("~~~") {
                        let kind: LiveSpan.Kind = touches(whole) ? .syntax : .foldedFence
                        out.append(LiveSpan(range: first, kind: kind))
                        if let last = lines.last, last.location != first.location {
                            let closer = source.substring(with: last).trimmingCharacters(in: .whitespaces)
                            if closer.hasPrefix("```") || closer.hasPrefix("~~~") {
                                out.append(LiveSpan(range: last, kind: kind))
                            }
                        }
                    }
                }
                return

            case is ThematicBreak:
                if let range = map.range(node).map(trimmed), range.length > 0 {
                    out.append(LiveSpan(range: range, kind: .syntax))
                }
                return

            case is Table:
                if let range = map.range(node).map(trimmed), range.length > 0 {
                    out.append(LiveSpan(range: range, kind: .table))
                }
                return

            case is HTMLBlock, is InlineHTML:
                if let range = inline(node) { code.append(range) }
                return

            default:
                break
            }

            let outer = fix
            if node is Paragraph || node is Heading {
                fix = SourceMap.InlineFix(node, quotes: depth)
            }
            for child in node.children { walk(child, quoteDepth: depth) }
            fix = outer
        }
        walk(document, quoteDepth: 0)

        func inCode(_ range: NSRange) -> Bool {
            code.contains { NSIntersectionRange($0, range).length > 0 }
        }

        // What a note adds to Markdown, outside code.
        for match in Self.wikiEmbed.matches(in: string, range: full) where !inCode(match.range) {
            embeds.append((match.range, source.substring(with: match.range(at: 1))))
        }
        // A line holding only embeds is a row of pictures; anywhere else an embed is text.
        var rows = Set<Int>()
        for embed in embeds {
            let line = map.line(at: embed.range.location)
            let text = source.substring(with: line)
            if DocMarkdown.mediaRow(text) != nil, !touches(line) {
                if rows.insert(line.location).inserted {
                    out.append(LiveSpan(range: line, kind: .media(text.trimmingCharacters(in: .whitespaces))))
                }
            } else {
                out.append(LiveSpan(range: embed.range, kind: .embed))
            }
        }
        let taken = embeds.map(\.range)
        func free(_ range: NSRange) -> Bool {
            !inCode(range) && !taken.contains { NSIntersectionRange($0, range).length > 0 }
        }

        for match in Self.wiki.matches(in: string, range: full) where free(match.range) {
            let inner = match.range(at: 1)
            // `[[Note|shown as]]` reads as "shown as"; `[[Note]]` as "Note".
            let pipe = source.range(of: "|", options: [], range: inner)
            let named = pipe.location == NSNotFound ? inner : NSRange(location: inner.location, length: pipe.location - inner.location)
            let leads = LiveSpan.Kind.destination("wiki:" + source.substring(with: named))
            guard !touches(match.range) else {
                out.append(LiveSpan(range: match.range, kind: .link))
                out.append(LiveSpan(range: match.range, kind: leads))
                continue
            }
            let label = pipe.location == NSNotFound
                ? inner : NSRange(location: NSMaxRange(pipe), length: NSMaxRange(inner) - NSMaxRange(pipe))
            guard label.length > 0 else {
                out.append(LiveSpan(range: match.range, kind: .link))
                out.append(LiveSpan(range: match.range, kind: leads))
                continue
            }
            out.append(LiveSpan(range: label, kind: .link))
            out.append(LiveSpan(range: label, kind: leads))
            marker(NSRange(location: match.range.location, length: label.location - match.range.location), shown: false)
            marker(NSRange(location: NSMaxRange(label), length: NSMaxRange(match.range) - NSMaxRange(label)), shown: false)
        }

        for match in Self.highlight.matches(in: string, range: full) where free(match.range) {
            out.append(LiveSpan(range: match.range, kind: .highlight))
            let shown = touches(match.range)
            marker(NSRange(location: match.range.location, length: 2), shown: shown)
            marker(NSRange(location: NSMaxRange(match.range) - 2, length: 2), shown: shown)
        }

        return out.filter { $0.range.length > 0 && NSMaxRange($0.range) <= source.length }
    }
}
