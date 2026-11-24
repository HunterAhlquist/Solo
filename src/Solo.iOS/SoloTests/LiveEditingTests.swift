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

    /// A line can carry on a list item, a quote or an indented paragraph without that block's
    /// indent or mark. The parser places what is on such a line too far right; the spans must not.
    @Test(arguments: [
        "Line one\n**bold** before [DMD-PW26-30](/docs/task/0123abcd)",
        "- item\n**bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "- item\n  **bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "1. item\n**bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "- a\n  - b\n**bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "> quoted\n**bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "> quoted\n> **bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "> - a\n**bold** [DMD-PW26-30](/docs/task/0123abcd)",
        " lead\n   **bold** [DMD-PW26-30](/docs/task/0123abcd)",
        "- é item\n**bold** ü [DMD-PW26-30](/docs/task/0123abcd)",
    ])
    func aLineContinuingABlockIsFormattedWhereItIs(text: String) {
        let away = spans(text)
        #expect(has(away, .strong, "**bold**", in: text))
        #expect(has(away, .link, "DMD-PW26-30", in: text))
        #expect(has(away, .destination("/docs/task/0123abcd"), "DMD-PW26-30", in: text))
        #expect(has(away, .hidden, "](/docs/task/0123abcd)", in: text))
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

    /// What a tap on a link opens, carried on the same run that is drawn as one.
    @Test func linksKnowWhereTheyLead() {
        let text = "[label](https://x.y/z) and [[Folder/Note#Part|shown]] and [[Plain]] and <https://a.b>"
        let found = spans(text)
        #expect(has(found, .destination("https://x.y/z"), "label", in: text))
        #expect(has(found, .destination("wiki:Folder/Note#Part"), "shown", in: text))
        #expect(has(found, .destination("wiki:Plain"), "Plain", in: text))
        #expect(has(found, .destination("https://a.b"), "<https://a.b>", in: text))
        // Touched, the whole of a wiki link is on show, and still leads to the same place.
        #expect(has(spans(text, caret: 30), .destination("wiki:Folder/Note#Part"), "[[Folder/Note#Part|shown]]", in: text))
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

    @Test func aCodeBlockShowsItsFencesWhileTheCaretIsAnywhereInIt() {
        let text = "before\n```swift\nlet x = 1\nlet y = 2\n```\nafter"
        let open = NSRange(location: 7, length: 8), close = NSRange(location: 36, length: 3)
        let away = spans(text, caret: 2)
        #expect(away.contains { $0.kind == .foldedFence && $0.range == open })
        #expect(away.contains { $0.kind == .foldedFence && $0.range == close })
        // On a line of code, not on either fence: both are on show.
        let inside = spans(text, caret: 20)
        #expect(inside.contains { $0.kind == .syntax && $0.range == open })
        #expect(inside.contains { $0.kind == .syntax && $0.range == close })
        #expect(!inside.contains { $0.kind == .foldedFence })
        // And on a fence itself.
        #expect(spans(text, caret: 37).contains { $0.kind == .syntax && $0.range == open })
    }

    @Test func listsAndQuotesKeepTheirShape() {
        let text = "- one\n- [ ] two\n\n> quoted\n> more"
        let found = spans(text)
        #expect(has(found, .bullet, "- ", in: text))
        // A task's mark is its box (`aTaskIsDrawnAsABoxAwayFromTheCaret`).
        #expect(has(found, .checkbox(false), "[ ]", in: text))
        #expect(has(found, .listItem(prefix: 2), "- one", in: text))
        #expect(has(found, .listItem(prefix: 6), "- [ ] two", in: text))
        #expect(has(found, .quote(1), "> quoted\n> more", in: text))
        // The two quote marks, and what the task's list is written with.
        #expect(found.filter { $0.kind == .hidden }.count == 3)
    }

    @Test func anUnorderedMarkerIsDrawnAsABulletAwayFromTheCaret() {
        let text = "- one\n* two\n1. three"
        let away = spans(text)
        #expect(away.filter { $0.kind == .dot }.map(\.range) == [NSRange(location: 0, length: 1), NSRange(location: 6, length: 1)])
        // Next to the caret it is the character that was typed, so it can be changed.
        #expect(spans(text, caret: 1).filter { $0.kind == .dot }.map(\.range) == [NSRange(location: 6, length: 1)])
    }

    /// The tree counts columns in UTF-8 bytes; the text view counts UTF-16 units.
    @Test func aTaskIsDrawnAsABoxAwayFromTheCaret() {
        let text = "- [ ] buy milk\n- [x] feed cat\n1. [X] numbered\n- [ ]not a task"
        let away = spans(text)
        #expect(has(away, .checkbox(false), "[ ]", in: text))
        #expect(has(away, .checkbox(true), "[x]", in: text))
        #expect(has(away, .checkbox(true), "[X]", in: text))
        // The box is the bullet: what the list is written with folds away, but a number stays.
        #expect(has(away, .hidden, "- ", in: text))
        #expect(has(away, .bullet, "1. ", in: text))
        #expect(has(away, .taskLine(shown: 0), "- [ ] buy milk", in: text))
        #expect(has(away, .taskLine(shown: 3), "1. [X] numbered", in: text))
        // Nothing but a space after the box makes one of it.
        #expect(away.filter { if case .checkbox = $0.kind { true } else { false } }.count == 3)
    }

    @Test func aTaskIsItsTextWhileTheCaretIsInItsBox() {
        let text = "- [ ] buy milk"
        // Anywhere from the list's mark to the end of the box: `- [ ]`, before the space after it.
        for caret in 0...5 {
            let onIt = spans(text, caret: caret)
            #expect(!onIt.contains { if case .checkbox = $0.kind { true } else { false } }, "caret at \(caret)")
            #expect(has(onIt, .bullet, "- [ ] ", in: text), "caret at \(caret)")
        }
        // At the start of what the task says, and anywhere in it, the box is a box.
        for caret in [6, 9, 14] {
            #expect(has(spans(text, caret: caret), .checkbox(false), "[ ]", in: text), "caret at \(caret)")
        }
    }

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

@Suite struct NoteLayoutTests {
    @Test func whatWasNeverAChoiceIsTheUsual() {
        // As the web reads what it kept (`viewPreferences`).
        #expect(NoteLayout(scale: 125, margin: 10) == NoteLayout(scale: 125, margin: 10))
        #expect(NoteLayout(scale: 120, margin: 7) == NoteLayout())
        #expect(NoteLayout(scale: 0, margin: -3) == NoteLayout(scale: 100, margin: NoteLayout.automatic))
        #expect(NoteLayout().factor == 1)
        #expect(NoteLayout(scale: 175).factor == 1.75)
    }

    @Test func aKeyStepsThroughTheMenusSizes() {
        #expect(NoteLayout(scale: 100, margin: 5).stepped(1) == NoteLayout(scale: 110, margin: 5))
        #expect(NoteLayout(scale: 100).stepped(-1) == NoteLayout(scale: 90))
        #expect(NoteLayout(scale: 200).stepped(1) == nil)
        #expect(NoteLayout(scale: 75).stepped(-1) == nil)
    }

    @Test func marginsAreAShareOfTheRoomOrWhatKeepsTheMeasure() {
        // Left to itself the column is its measure, centred; in less room than that it has it all.
        #expect(NoteColumn.margin(room: 1220, NoteLayout()) == 200)
        #expect(NoteColumn.margin(room: 400, NoteLayout()) == 0)
        // A margin asked for is that much of the room on each side, wide or narrow.
        #expect(NoteColumn.margin(room: 1220, NoteLayout(margin: 0)) == 0)
        #expect(NoteColumn.margin(room: 1000, NoteLayout(margin: 15)) == 150)
        #expect(NoteColumn.margin(room: 400, NoteLayout(margin: 20)) == 80)
        // The room is what can be seen: between whatever stands over the view's sides.
        #expect(NoteColumn.span(width: 1400, leading: 300, trailing: 100, layout: NoteLayout(margin: 10)) == 400...1200)
        #expect(NoteColumn.span(width: 1400, leading: 300, trailing: 100) == 390...1210)
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

/// What Return does in a note: a list or a quote carries on, and ends on an empty line of it.
@Suite struct NoteReturnTests {
    /// The text after Return is pressed with the caret where `|` is, the caret shown the same way.
    private func pressed(_ marked: String, inCode: Bool = false) -> String {
        let caret = (marked as NSString).range(of: "|").location
        let text = marked.replacingOccurrences(of: "|", with: "") as NSString
        guard let edit = NoteReturn.edit(in: text, selection: NSRange(location: caret, length: 0), inCode: inCode) else {
            // Only a new line, as the text view puts one down itself.
            return text.replacingCharacters(in: NSRange(location: caret, length: 0), with: "\n|")
        }
        let after = text.replacingCharacters(in: edit.range, with: edit.text) as NSString
        return after.replacingCharacters(in: NSRange(location: edit.caret, length: 0), with: "|")
    }

    @Test func aBulletCarriesOnWithItsOwnMarker() {
        #expect(pressed("- one|") == "- one\n- |")
        #expect(pressed("* one|") == "* one\n* |")
        #expect(pressed("  + one|") == "  + one\n  + |")
        // In the middle of an item, what is after the caret goes to the new one.
        #expect(pressed("- one| two") == "- one\n- | two")
    }

    @Test func aNumberedListCountsOn() {
        #expect(pressed("1. one|") == "1. one\n2. |")
        #expect(pressed("9) nine|") == "9) nine\n10) |")
        // And the items after it count on from the new one.
        #expect(pressed("1. one|\n2. two\n3. three\n\nafter") == "1. one\n2. |\n3. two\n4. three\n\nafter")
        // What belongs to an item is passed over on the way.
        #expect(pressed("1. one|\n   - inside\n2. two") == "1. one\n2. |\n   - inside\n3. two")
        // A list numbered all alike was not counting, and is left as it is.
        #expect(pressed("1. one|\n1. two") == "1. one\n2. |\n1. two")
    }

    @Test func aTaskCarriesOnWithAnEmptyBox() {
        #expect(pressed("- [x] done|") == "- [x] done\n- [ ] |")
        #expect(pressed("1. [ ] one|") == "1. [ ] one\n2. [ ] |")
    }

    @Test func aQuoteCarriesOn() {
        #expect(pressed("> said|") == "> said\n> |")
        #expect(pressed("> > twice|") == "> > twice\n> > |")
        #expect(pressed("> - item|") == "> - item\n> - |")
    }

    @Test func returnOnAnEmptyOneEndsIt() {
        // The marker goes and the caret stays on its line.
        #expect(pressed("- one\n- |") == "- one\n|")
        #expect(pressed("1. one\n2. |") == "1. one\n|")
        #expect(pressed("> said\n> |") == "> said\n|")
        #expect(pressed("- one\n- |\nafter") == "- one\n|\nafter")
        #expect(pressed("- [ ] |") == "|")
        // And Return again, on the line that is now empty, is only a new line.
        #expect(pressed("- one\n|") == "- one\n\n|")
        // One level at a time: an item in a quote is left the quote, a quote in a quote the outer one.
        #expect(pressed("> - |") == "> |")
        #expect(pressed("> > |") == "> |")
    }

    @Test func anEmptyItemInsideAnotherGoesOutALevel() {
        #expect(pressed("- one\n  - two\n  - |") == "- one\n  - two\n- |")
        #expect(pressed("1. one\n   - two\n   - |") == "1. one\n   - two\n2. |")
        // With no list further out to go to, it is only ended.
        #expect(pressed("  - |") == "|")
    }

    @Test func elsewhereReturnIsOnlyANewLine() {
        #expect(pressed("plain|") == "plain\n|")
        // In front of the marker the whole line goes down.
        #expect(pressed("|- one") == "\n|- one")
        // A dash is a dash in code, and a word that starts with one is a word.
        #expect(pressed("- one|", inCode: true) == "- one\n|")
        #expect(pressed("-one|") == "-one\n|")
        #expect(pressed("1.5 metres|") == "1.5 metres\n|")
    }

    @Test func aPlaceInACodeBlockIsKnown() {
        let text = "- a\n\n```\n- b\n```\n\n- c"
        let markdown = LiveMarkdown(text)
        #expect(!markdown.isCode(at: 3))
        #expect(markdown.isCode(at: 12))
        #expect(!markdown.isCode(at: (text as NSString).length))
    }
}
