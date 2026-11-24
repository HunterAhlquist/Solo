import Foundation

// What the keyboard does in a note beyond putting down the key that was pressed.
//
// Return carries a list or a quote on to the next line, the way Markdown editors do: the
// next item's marker is written for you, a numbered list counts on, a task gets an empty
// box. Return on an item with nothing in it means the list is over, and takes the marker
// away again. Worked out from the text alone and handed back as one edit, so that the two
// editors — a phone's and a Mac's — make it the same way and Undo takes it back whole.

nonisolated enum NoteReturn {
    /// One change to a note's text, and where the caret goes after it.
    struct Edit: Equatable {
        var range: NSRange
        var text: String
        var caret: Int
    }

    private static func regex(_ pattern: String) -> NSRegularExpression {
        // The pattern is a literal below; one that didn't compile would be a mistake in this file.
        (try? NSRegularExpression(pattern: pattern)) ?? NSRegularExpression()
    }

    /// A line's start: the marks of the quotes it is in, its indent, and a list marker if it
    /// has one — a bullet or a number, the space after it, and a task's box.
    /// 1 quotes, 2 indent, 3 bullet, 4 number, 5 the number's `.` or `)`, 6 space, 7 box, 8 space.
    nonisolated(unsafe) private static let start = regex(
        #"^((?:[ \t]{0,3}>[ \t]?)*)([ \t]*)(?:(?:([-*+])|(\d{1,9})([.)]))([ \t]+|$)(?:(\[[ xX]\])([ \t]+|$))?)?"#)

    private struct Line {
        var range: NSRange
        var quotes: String
        var indent: String
        var bullet: String?
        var number: Int?
        var delimiter: String
        var space: String
        var box: Bool
        /// Where the line's own text begins, past all of the above.
        var text: Int

        var isItem: Bool { bullet != nil || number != nil }
        var isEmpty: Bool { text >= NSMaxRange(range) }
    }

    private static func line(at offset: Int, in text: NSString) -> Line {
        var range = text.lineRange(for: NSRange(location: offset, length: 0))
        while range.length > 0, [10, 13].contains(text.character(at: NSMaxRange(range) - 1)) { range.length -= 1 }
        var line = Line(range: range, quotes: "", indent: "", bullet: nil, number: nil, delimiter: "", space: "", box: false,
                        text: range.location)
        guard let match = start.firstMatch(in: text as String, options: .anchored, range: range) else { return line }
        func part(_ index: Int) -> String? {
            let found = match.range(at: index)
            return found.location == NSNotFound ? nil : text.substring(with: found)
        }
        line.quotes = part(1) ?? ""
        line.indent = part(2) ?? ""
        line.bullet = part(3)
        line.number = part(4).flatMap { Int($0) }
        line.delimiter = part(5) ?? ""
        line.space = part(6) ?? ""
        line.box = part(7) != nil
        line.text = NSMaxRange(match.range)
        // A marker with no space after it is only a marker at the end of its line: "-x" is a word.
        if line.isItem, line.space.isEmpty, line.text < NSMaxRange(range) {
            line.bullet = nil
            line.number = nil
            line.box = false
            line.text = range.location + (line.quotes + line.indent).utf16.count
        }
        return line
    }

    /// How wide an indent is, a tab counting to the next stop of four as Markdown has it.
    private static func width(_ indent: String) -> Int {
        indent.reduce(0) { $1 == "\t" ? $0 + 4 - $0 % 4 : $0 + 1 }
    }

    /// What Return does with the selection where it is, or nil where it is only a new line.
    /// `inCode` is whether the caret is in a block of code, where a dash is a dash.
    static func edit(in text: NSString, selection: NSRange, inCode: Bool) -> Edit? {
        guard !inCode, selection.location <= text.length, NSMaxRange(selection) <= text.length else { return nil }
        let line = line(at: selection.location, in: text)
        let quoted = line.quotes.contains(">")
        guard line.isItem || quoted else { return nil }
        // In front of the marker, Return pushes the whole line down as it always did.
        guard selection.location >= line.text else { return nil }

        if line.isEmpty {
            return ended(line, in: text)
        }

        var marker = ""
        if let bullet = line.bullet {
            marker = bullet + line.space
        } else if let number = line.number {
            marker = "\(number + 1)" + line.delimiter + line.space
        }
        if line.box { marker += "[ ] " }
        let lead = line.quotes + line.indent + marker
        var inserted = "\n" + lead
        var end = NSMaxRange(selection)
        let caret = selection.location + inserted.utf16.count

        // The items after it count on from the new one, where they were counting at all.
        if let number = line.number {
            var expected = number + 1
            var cursor = NSMaxRange(text.lineRange(for: NSRange(location: end, length: 0)))
            var rest = text.substring(with: NSRange(location: end, length: cursor - end))
            var renumbered = false
            while cursor < text.length {
                let next = self.line(at: cursor, in: text)
                let whole = text.lineRange(for: NSRange(location: cursor, length: 0))
                if next.quotes == line.quotes, next.indent == line.indent, let had = next.number, next.delimiter == line.delimiter {
                    guard had == expected else { break }
                    expected += 1
                    let numberLength = "\(had)".utf16.count
                    let numberAt = next.range.location + (next.quotes + next.indent).utf16.count
                    rest += text.substring(with: NSRange(location: whole.location, length: numberAt - whole.location))
                    rest += "\(expected)"
                    rest += text.substring(with: NSRange(location: numberAt + numberLength, length: NSMaxRange(whole) - numberAt - numberLength))
                    renumbered = true
                } else if next.quotes == line.quotes, width(next.indent) > width(line.indent), !next.isEmpty || next.isItem {
                    // What belongs to an item — its own items, its further lines — is passed over.
                    rest += text.substring(with: whole)
                } else {
                    break
                }
                cursor = NSMaxRange(whole)
            }
            if renumbered {
                // Only as far as the last line that changed: what was passed over after it is left out.
                inserted += rest
                end = cursor
            }
        }
        return Edit(range: NSRange(location: selection.location, length: end - selection.location), text: inserted, caret: caret)
    }

    /// Return on an item or a quoted line with nothing in it: the list or the quote is over.
    /// An item inside another goes out a level, to carry on the list it was in; otherwise the
    /// marker goes, and from a quote in a quote, the innermost mark.
    ///
    /// Out of the last of them, the caret is put a line further down, with the emptied line
    /// left blank above it. Markdown reads a line written straight under a list or a quote as
    /// more of it, so what was typed next would still have been in what had just been ended.
    private static func ended(_ line: Line, in text: NSString) -> Edit {
        let start = line.range.location
        func replacing(with lead: String) -> Edit {
            Edit(range: NSRange(location: start, length: line.range.length), text: lead, caret: start + lead.utf16.count)
        }
        func out() -> Edit {
            guard start > 0 else { return replacing(with: "") }
            let above = self.line(at: start - 1, in: text)
            let blank = text.substring(with: above.range).trimmingCharacters(in: .whitespaces).isEmpty
            return blank ? replacing(with: "") : Edit(range: NSRange(location: start, length: line.range.length), text: "\n", caret: start + 1)
        }
        if line.isItem {
            // The nearest item above that is further out, in the same quote, is the list it was in.
            var cursor = start
            while cursor > 0, !line.indent.isEmpty {
                let above = self.line(at: cursor - 1, in: text)
                cursor = above.range.location
                guard above.quotes == line.quotes else { break }
                if above.isEmpty, !above.isItem { break }
                guard above.isItem, width(above.indent) < width(line.indent) else { continue }
                var marker = above.bullet.map { $0 + above.space } ?? ""
                if let number = above.number { marker = "\(number + 1)" + above.delimiter + above.space }
                if above.box { marker += "[ ] " }
                return replacing(with: line.quotes + above.indent + marker)
            }
            return line.quotes.contains(">") ? replacing(with: line.quotes) : out()
        }
        // A quote: the last of its marks goes, with the space after it.
        var marks = line.quotes
        if let last = marks.lastIndex(of: ">") {
            marks = String(marks[..<last])
        }
        return marks.contains(">") ? replacing(with: marks) : out()
    }
}
