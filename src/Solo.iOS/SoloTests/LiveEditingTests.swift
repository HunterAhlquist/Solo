import Foundation
import Testing
@testable import Solo

/// What the live editor shows for a piece of Markdown, and what it folds away.
@Suite struct LiveMarkdownTests {
    private func spans(_ text: String, caret: Int? = nil) -> [LiveSpan] {
        LiveMarkdown(text).spans(selection: caret.map { NSRange(location: $0, length: 0) })
    }

    /// Whether some span of `kind` covers exactly the first `piece` of `text`.
    private func has(_ spans: [LiveSpan], _ kind: LiveSpan.Kind, _ piece: String, in text: String) -> Bool {
        let range = (text as NSString).range(of: piece)
        return range.location != NSNotFound && spans.contains { $0.kind == kind && $0.range == range }
    }

    @Test func aHeadingFoldsItsMarkerAwayFromTheCaret() {
        let text = "# Title\n\nbody"
        let away = spans(text)
        #expect(has(away, .heading(1), "# Title", in: text))
        #expect(has(away, .hidden, "# ", in: text))
        let onIt = spans(text, caret: 4)
        #expect(has(onIt, .syntax, "# ", in: text))
        #expect(!onIt.contains { $0.kind == .hidden })
        // The caret on another line leaves it folded.
        #expect(has(spans(text, caret: 10), .hidden, "# ", in: text))
    }

    @Test func inlineFormattingShowsItsMarkersOnlyWhenTouched() {
        let text = "a **bold** and *em* and `code` and ~~gone~~ and ==mark=="
        let away = spans(text)
        #expect(has(away, .strong, "**bold**", in: text))
        #expect(has(away, .emphasis, "*em*", in: text))
        #expect(has(away, .code, "`code`", in: text))
        #expect(has(away, .strike, "~~gone~~", in: text))
        #expect(has(away, .highlight, "==mark==", in: text))
        #expect(away.filter { $0.kind == .hidden }.count == 10)
        // Inside "bold": its own markers show, everyone else's stay folded.
        let inBold = spans(text, caret: 5)
        #expect(inBold.filter { $0.kind == .syntax }.map(\.range) == [NSRange(location: 2, length: 2), NSRange(location: 8, length: 2)])
        #expect(inBold.filter { $0.kind == .hidden }.count == 8)
    }

    @Test func aLinkReadsAsItsLabel() {
        let text = "see [label](https://x.y) end"
        let away = spans(text)
        #expect(has(away, .link, "label", in: text))
        #expect(has(away, .hidden, "[", in: text))
        #expect(has(away, .hidden, "](https://x.y)", in: text))
        #expect(has(spans(text, caret: 6), .syntax, "](https://x.y)", in: text))
    }

    @Test func aWikiLinkReadsAsItsAliasOrItsTarget() {
        let text = "see [[Other note|shown]] and [[Plain]]"
        let away = spans(text)
        #expect(has(away, .link, "shown", in: text))
        #expect(has(away, .hidden, "[[Other note|", in: text))
        #expect(has(away, .link, "Plain", in: text))
        // Touched, the whole of it is on show.
        #expect(has(spans(text, caret: 8), .link, "[[Other note|shown]]", in: text))
    }

    @Test func aLineOfEmbedsIsARowOfPictures() {
        let text = "![[a.png]] ![[b.sketch]]\ntext ![[c.png]] more"
        let away = spans(text)
        #expect(has(away, .media("![[a.png]] ![[b.sketch]]"), "![[a.png]] ![[b.sketch]]", in: text))
        #expect(has(away, .embed, "![[c.png]]", in: text))
        #expect(away.filter { if case .media = $0.kind { true } else { false } }.count == 1)
        // With the caret on the row it is text again, to be edited.
        let onRow = spans(text, caret: 3)
        #expect(has(onRow, .embed, "![[a.png]]", in: text))
        #expect(has(onRow, .embed, "![[b.sketch]]", in: text))
        #expect(!onRow.contains { if case .media = $0.kind { true } else { false } })
    }

    @Test func codeIsNeverFormatted() {
        let text = "`==x==` and `[[n]]`\n\n```\n**b** [[n]] ![[a.png]]\n```"
        let found = spans(text)
        #expect(!found.contains { [.highlight, .link, .strong, .embed].contains($0.kind) })
        #expect(has(found, .codeBlock, "```\n**b** [[n]] ![[a.png]]\n```", in: text))
    }

    @Test func listsAndQuotesKeepTheirShape() {
        let text = "- one\n- [ ] two\n\n> quoted\n> more"
        let found = spans(text)
        #expect(has(found, .bullet, "- ", in: text))
        #expect(has(found, .bullet, "- [ ] ", in: text))
        #expect(has(found, .listItem(prefix: 2), "- one", in: text))
        #expect(has(found, .listItem(prefix: 6), "- [ ] two", in: text))
        #expect(has(found, .quote(1), "> quoted\n> more", in: text))
        #expect(found.filter { $0.kind == .hidden }.count == 2)
    }

    /// The tree counts columns in UTF-8 bytes; the text view counts UTF-16 units.
    @Test func positionsSurviveNonASCIIText() {
        let text = "é **b** 😀 *c* – ==d=="
        let found = spans(text)
        #expect(has(found, .strong, "**b**", in: text))
        #expect(has(found, .emphasis, "*c*", in: text))
        #expect(has(found, .highlight, "==d==", in: text))
    }

    @Test func windowsLineEndingsCountAsOneBreak() {
        let text = "# A\r\n\r\ntext **b**\r\n- item"
        let found = spans(text)
        #expect(has(found, .heading(1), "# A", in: text))
        #expect(has(found, .strong, "**b**", in: text))
        #expect(has(found, .bullet, "- ", in: text))
    }

    @Test func nothingReachesPastTheText() {
        for text in ["", "#", "**a", "[[", "![[", "==", "> ", "- ", "```", "| a |\n|", "a\r", "😀", "# \n===\n", "[x](", "`"] {
            for caret in [nil, 0, (text as NSString).length] {
                let found = spans(text, caret: caret)
                #expect(found.allSatisfy { $0.range.length > 0 && NSMaxRange($0.range) <= (text as NSString).length }, "\(text.debugDescription)")
            }
        }
    }
}

@Suite struct NotePictureTests {
    @Test func aRowIsALineOfEmbedsAndNothingElse() {
        #expect(DocMarkdown.mediaRow("![[a.png]]  ![alt](b.jpg)")?.map(\.target) == ["a.png", "b.jpg"])
        #expect(DocMarkdown.mediaRow("  ![[a.png|200]]")?.first == DocEmbedRef(source: "![[a.png|200]]", target: "a.png|200"))
        #expect(DocMarkdown.mediaRow("see ![[a.png]]") == nil)
        #expect(DocMarkdown.mediaRow("no pictures") == nil)
        #expect(DocMarkdown.mediaRow("") == nil)
    }

    @Test func aNoteIsCutIntoTextAndRows() {
        let note = "intro\n![[a.png]] ![[b.sketch]]\nmiddle with ![[c.png]] inline\n```\n![[code.png]]\n```\n![[d.png]]"
        let pieces = DocMarkdown.pieces(note)
        #expect(pieces.count == 4)
        #expect(pieces[0] == .text("intro"))
        #expect(pieces[1] == .media([DocEmbedRef(source: "![[a.png]]", target: "a.png"), DocEmbedRef(source: "![[b.sketch]]", target: "b.sketch")]))
        #expect(pieces[2] == .text("middle with ![[c.png]] inline\n```\n![[code.png]]\n```"))
        #expect(pieces[3] == .media([DocEmbedRef(source: "![[d.png]]", target: "d.png")]))
        #expect(DocMarkdown.pieces("\n\n").isEmpty)
    }

    /// Values from the web's own `imageAngle`: a print leans the same way on every client.
    @MainActor @Test func printsTiltAsTheyDoOnTheWeb() {
        let expected: [(String, Double)] = [
            ("attachments/AaHXbx2XdZqG965Ml5glaw/Photo.jpg", -3.06), ("attachments/AaHXbx2XdZqG965Ml5glaw/Arena.sketch", -3.3),
            ("a.png", 0.14), ("über/straße – 図.webp", 2.4),
        ]
        for (path, angle) in expected {
            #expect(abs(NoteMediaStack.angle(path) - angle) < 0.0001, "\(path)")
        }
        #expect(NoteMediaStack.size(count: 1).width == 148)
        #expect(NoteMediaStack.size(count: 3).width == 196)
        #expect(NoteMediaStack.size(count: 40).width == 316)
    }
}
