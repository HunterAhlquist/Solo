import Foundation
import Testing
@testable import Solo

@Suite struct RoadmapRuleTests {
    @Test func aTimelineAlwaysHasSpareRows() {
        #expect(TimelineRules.rows(forLines: 0) == TimelineRules.minimumRows)
        #expect(TimelineRules.rows(forLines: 20) == 20 + TimelineRules.spareRows)
    }

    @Test func aTaskAddedInThePastRunsToToday() {
        let today = Day(year: 2026, month: 11, day: 13)
        let past = TimelineRules.scheduleOnAdd(today.adding(days: -4), today: today)
        #expect(past.start == today.adding(days: -4) && past.end == today)
        let future = TimelineRules.scheduleOnAdd(today.adding(days: 3), today: today)
        #expect(future.start == future.end)
    }

    @Test func sprintsStartOnTheChosenDay() {
        let friday = Day(year: 2026, month: 11, day: 13)
        #expect(RoadmapSprints.sprintStart(friday, startDay: .monday, weeks: 1) == Day(year: 2026, month: 11, day: 9))
        // Two-week sprints: the one holding 13 Nov 2026 began on the 9th.
        #expect(RoadmapSprints.sprintStart(friday, startDay: .monday, weeks: 2) == Day(year: 2026, month: 11, day: 9))
    }

    @Test func sprintsRepeatEveryLength() {
        let day = Day(year: 2026, month: 11, day: 13)
        for weeks in [1, 2, 3, 6] {
            let start = RoadmapSprints.sprintStart(day, startDay: .wednesday, weeks: weeks)
            #expect(start <= day && day.days(until: start.adding(days: weeks * 7)) > 0)
            #expect(RoadmapSprints.sprintStart(day.adding(days: weeks * 7), startDay: .wednesday, weeks: weeks)
                == start.adding(days: weeks * 7))
            // Every day of a sprint belongs to the same sprint.
            #expect(RoadmapSprints.sprintStart(start.adding(days: weeks * 7 - 1), startDay: .wednesday, weeks: weeks) == start)
        }
    }
}

@Suite struct NoteAddressTests {
    private let id = UUID(uuidString: "01a1d76f-1d97-759a-86f7-ae4c9798256b")!

    /// The key the server wrote for this note's attachments.
    @Test func matchesTheServersEncoding() {
        #expect(NoteAddress.encode(id) == "AaHXbx2XdZqG965Ml5glaw")
        #expect(NoteAddress.decode("AaHXbx2XdZqG965Ml5glaw") == id)
    }

    @Test func roundTripsAnyId() {
        for _ in 0..<50 {
            let random = UUID()
            let key = NoteAddress.encode(random)
            #expect(key.count == 22)
            #expect(NoteAddress.decode(key) == random)
        }
    }

    @Test func readsLinks() throws {
        let plain = try #require(NoteAddress.read("/docs/note/AaHXbx2XdZqG965Ml5glaw"))
        #expect(plain.id == id && plain.heading == nil)
        let anchored = try #require(NoteAddress.read("see /docs/note/AaHXbx2XdZqG965Ml5glaw#stances-2 here"))
        #expect(anchored.id == id && anchored.heading == "stances-2")
        let spelled = try #require(NoteAddress.read("/docs/note/01a1d76f-1d97-759a-86f7-ae4c9798256b"))
        #expect(spelled.id == id)
        #expect(NoteAddress.read("/docs/DMD/01a1d76f-1d97-759a-86f7-ae4c9798256b") == nil)
        #expect(NoteAddress.url(id, heading: "intro") == "/docs/note/AaHXbx2XdZqG965Ml5glaw#intro")
    }
}

@Suite struct DocMarkdownTests {
    @Test func headingsGetTheWebEditorsSlugs() {
        let note = """
        # Combat

        ## Stances
        ## Stances
        ```
        # not a heading
        ```
        ### A *bold* [link](https://example.com) ==mark==
        """
        let headings = DocMarkdown.headings(note)
        #expect(headings.map(\.slug) == ["combat", "stances", "stances-2", "a-bold-link-mark"])
        #expect(headings.map(\.level) == [1, 2, 2, 3])
        #expect(headings.last?.text == "A bold link mark")
    }

    @Test func embedsAreFoundOutsideCode() {
        let note = "![[a.png]] `![[code.png]]` ![alt](b.jpg) ![[drawings/c.sketch|200]]\n```\n![[fenced.png]]\n```"
        #expect(DocMarkdown.embedTargets(note) == ["a.png", "b.jpg", "drawings/c.sketch|200"])
        #expect(DocMarkdown.target("drawings/c.sketch|200") == "drawings/c.sketch")
        #expect(DocMarkdown.target("My%20File.png") == "My File.png")
    }

    @Test func attachmentsRememberTheirNote() {
        let id = UUID()
        let path = DocMarkdown.attachmentPath(noteId: id, name: "Arena.sketch")
        #expect(path.hasPrefix("attachments/") && path.hasSuffix("/Arena.sketch"))
        #expect(DocMarkdown.owner(ofPath: path) == id)
        #expect(DocMarkdown.owner(ofPath: "imported/photo.png") == nil)
    }
}

@Suite struct DocSyncTests {
    private func packed(_ updates: [[UInt8]]) -> Data {
        func int(_ value: Int) -> [UInt8] { (0..<4).map { UInt8((value >> ($0 * 8)) & 0xff) } }
        return Data(int(updates.count) + updates.flatMap { int($0.count) + $0 })
    }

    @Test func unpacksABatch() {
        #expect(DocSync.unpack(packed([[1, 2, 3], [9]])) == [[1, 2, 3], [9]])
        #expect(DocSync.unpack(packed([])) == [])
    }

    @Test func refusesADamagedBatch() {
        var short = packed([[1, 2, 3]])
        short.removeLast()
        #expect(DocSync.unpack(short) == [])
        #expect(DocSync.unpack(packed([[1, 2, 3]]) + Data([7])) == [])
        #expect(DocSync.unpack(Data([1, 0])) == [])
    }

    @Test func aSharedDocumentCarriesEditsBetweenCopies() {
        let first = SharedDoc(), second = SharedDoc()
        first.replaceText(start: 0, length: 0, with: "Hello world")
        #expect(second.apply([first.update(since: second.stateVector())]))
        #expect(second.string() == "Hello world")
        // Two people typing at once: both edits survive, in the same order for both.
        first.replaceText(start: 0, length: 5, with: "Howdy")
        second.replaceText(start: 11, length: 0, with: "!")
        let fromFirst = first.update(since: second.stateVector()), fromSecond = second.update(since: first.stateVector())
        first.apply([fromSecond])
        second.apply([fromFirst])
        #expect(first.string() == "Howdy world!")
        #expect(second.string() == first.string())
    }

    @Test func offsetsAreUTF16() {
        let doc = SharedDoc()
        doc.replaceText(start: 0, length: 0, with: "a😀b")
        doc.replaceText(start: 3, length: 1, with: "c")
        #expect(doc.string() == "a😀c")
    }
}

@Suite struct TextChangeTests {
    private func units(_ text: String) -> [UInt16] { Array(text.utf16) }

    private func apply(_ change: TextChange, to old: [UInt16]) -> [UInt16] {
        Array(old[..<change.start]) + change.inserted + Array(old[(change.start + change.removed)...])
    }

    @Test func findsTheSmallestEdit() throws {
        let change = try #require(TextChange.between(units("hello world"), units("help world")))
        #expect(change == TextChange(start: 3, removed: 2, inserted: units("p")))
        #expect(TextChange.between(units("same"), units("same")) == nil)
        let typed = try #require(TextChange.between(units("ab"), units("aXb")))
        #expect(typed == TextChange(start: 1, removed: 0, inserted: units("X")))
    }

    @Test func turnsOneTextIntoTheOther() throws {
        let pairs = [("", "abc"), ("abc", ""), ("aaa", "aa"), ("abcabc", "abcXabc"), ("line\nline", "line\nnew\nline"),
                     ("a😀b", "a😁b"), ("😀", "😀😀"), ("x😀", "x")]
        for (old, new) in pairs {
            let change = try #require(TextChange.between(units(old), units(new)))
            #expect(apply(change, to: units(old)) == units(new))
        }
    }

    /// Half an emoji in the shared document would corrupt it for everyone.
    @Test func neverSplitsACharacter() throws {
        let change = try #require(TextChange.between(units("a😀b"), units("a😁b")))
        #expect(change.start == 1)
        #expect(change.inserted == units("😁"))
        #expect(change.removed == 2)
    }

    @Test func carriesTheCaretAcrossAnEdit() {
        let change = TextChange(start: 5, removed: 3, inserted: Array("12345".utf16))
        #expect(change.shift(2) == 2)
        #expect(change.shift(5) == 5)
        #expect(change.shift(6) == 10)
        #expect(change.shift(8) == 10)
        #expect(change.shift(12) == 14)
    }
}

/// The structure read back out of a Yjs document, checked against the document itself.
@Suite struct YjsStructureTests {
    private func structure(_ doc: SharedDoc) -> YjsTextStructure? {
        YjsTextStructure(update: doc.fullState())
    }

    @Test func aTypedTextReadsBackAsItself() throws {
        let doc = SharedDoc()
        doc.replaceText(start: 0, length: 0, with: "Hello world")
        doc.replaceText(start: 5, length: 0, with: ",")
        doc.replaceText(start: 0, length: 5, with: "Goodbye")
        let read = try #require(structure(doc))
        #expect(read.text == doc.string())
        #expect(read.length == doc.string().utf16.count)
    }

    @Test func anEmptyDocumentHasNoCharacters() throws {
        let read = try #require(structure(SharedDoc()))
        #expect(read.text.isEmpty)
        #expect(read.id(at: 0) == nil)
    }

    /// Two people typing at once, at the same places, exchanging their edits now and then:
    /// the order Yjs settles on is the order read back, every time.
    @Test func concurrentEditsSettleInTheSameOrder() throws {
        var generator = SystemRandomNumberGenerator()
        for round in 0..<40 {
            let docs = [SharedDoc(), SharedDoc(), SharedDoc()]
            for step in 0..<60 {
                let doc = docs[Int.random(in: 0..<docs.count, using: &generator)]
                let length = doc.string().utf16.count
                if length > 0, Int.random(in: 0..<4, using: &generator) == 0 {
                    let start = Int.random(in: 0..<length, using: &generator)
                    doc.replaceText(start: start, length: Int.random(in: 1...min(4, length - start), using: &generator), with: "")
                } else {
                    let piece = ["a", "bc", "déf", "\n", "- x", "**y**"][Int.random(in: 0..<6, using: &generator)]
                    doc.replaceText(start: Int.random(in: 0...length, using: &generator), length: 0, with: piece + "\(step % 10)")
                }
                if step % 7 == 6 {
                    // Everyone catches up with everyone.
                    let states = docs.map { $0.fullState() }
                    for doc in docs { doc.apply(states) }
                }
            }
            let states = docs.map { $0.fullState() }
            for doc in docs { doc.apply(states) }
            let expected = docs[0].string()
            for doc in docs {
                #expect(doc.string() == expected)
                let read = try #require(structure(doc), "round \(round)")
                #expect(read.text == expected, "round \(round)")
            }
        }
    }

    @Test func aCharacterKeepsItsIdentityWhileTextMovesAroundIt() throws {
        let doc = SharedDoc(), other = SharedDoc()
        doc.replaceText(start: 0, length: 0, with: "one two three")
        other.apply([doc.fullState()])
        // The "t" of "two".
        let marked = try #require(structure(doc)?.id(at: 4))

        other.replaceText(start: 0, length: 0, with: "zero ")
        doc.replaceText(start: 13, length: 0, with: " four")
        let states = [doc.fullState(), other.fullState()]
        doc.apply(states)
        other.apply(states)
        #expect(doc.string() == "zero one two three four")

        for copy in [doc, other] {
            let read = try #require(structure(copy))
            #expect(read.index(of: marked) == 9)
            #expect(read.id(at: 9) == marked)
        }
        // Deleted, it is where it was: the cursor before it is now before what followed it.
        doc.replaceText(start: 9, length: 4, with: "")
        #expect(doc.string() == "zero one three four")
        #expect(structure(doc)?.index(of: marked) == 9)
    }

    @Test func aRelativePositionRoundTripsThroughJSON() throws {
        let id = YjsID(client: 3_000_000_001, clock: 42)
        let data = try JSONSerialization.data(withJSONObject: YjsRelativePosition(item: id).json)
        let back = YjsRelativePosition(json: try JSONSerialization.jsonObject(with: data))
        #expect(back?.item == id)
        let end = try JSONSerialization.data(withJSONObject: YjsRelativePosition(item: nil).json)
        #expect(YjsRelativePosition(json: try JSONSerialization.jsonObject(with: end)) == YjsRelativePosition(item: nil))
        #expect(YjsRelativePosition(json: ["nonsense": 1]) == nil)
    }

    /// A sketch's document holds arrays and maps of other things; reading it must not trip.
    @Test func aDocumentOfOtherThingsStillReads() throws {
        let doc = SharedDoc()
        doc.appendSketchOp(SketchOp(id: "a", tool: "pen", ink: "blue", level: 1, seed: 1, pointerType: "pen", points: [[1, 2, 0.5]]))
        doc.setSketchMeta(["pageSize": "A4"])
        doc.replaceText(start: 0, length: 0, with: "text too")
        #expect(structure(doc)?.text == "text too")
    }
}

/// The presence channel: who is where, as Yjs's awareness protocol carries it.
@Suite(.serialized) @MainActor struct AwarenessTests {
    private func state(_ fields: [String: Any]) -> String {
        var object = fields
        object["user"] = ["userId": "0190a000-0000-7000-8000-000000000001", "name": "Ada Lovelace", "color": "#ff0000"]
        return String(decoding: try! JSONSerialization.data(withJSONObject: object), as: UTF8.self)
    }

    @Test func aMessageRoundTrips() {
        let bytes = AwarenessCodec.encode(client: 3_000_000_123, clock: 300, state: #"{"a":"é"}"#)
        let back = AwarenessCodec.decode(bytes)
        #expect(back.count == 1)
        #expect(back.first?.client == 3_000_000_123)
        #expect(back.first?.clock == 300)
        #expect(back.first?.state == #"{"a":"é"}"#)
        // Cut short, it is nothing rather than half of something.
        #expect(AwarenessCodec.decode(Array(bytes.dropLast(3))).isEmpty)
    }

    @Test func aNewerStateReplacesAnOlderOneAndNotTheReverse() {
        let awareness = Awareness()
        awareness.apply(AwarenessCodec.encode(client: 7, clock: 2, state: state(["pointer": ["x": 10, "y": 20, "tool": "pen", "width": 3]])))
        #expect(awareness.peers[7]?.pointer?.x == 10)
        #expect(awareness.peers[7]?.user.initials == "AL")

        awareness.apply(AwarenessCodec.encode(client: 7, clock: 1, state: state(["pointer": ["x": 99, "y": 99, "tool": "pen", "width": 3]])))
        #expect(awareness.peers[7]?.pointer?.x == 10)

        awareness.apply(AwarenessCodec.encode(client: 7, clock: 3, state: state(["pointer": NSNull()])))
        #expect(awareness.peers[7] != nil && awareness.peers[7]?.pointer == nil)

        // Leaving is the same number again, with no state.
        awareness.apply(AwarenessCodec.encode(client: 7, clock: 3, state: "null"))
        #expect(awareness.peers[7] == nil)
    }

    @Test func thisClientsOwnMessagesAreNotItsPeers() {
        let awareness = Awareness()
        awareness.setUser(["userId": "x", "name": "Me"])
        awareness.apply(awareness.message())
        #expect(awareness.peers.isEmpty)
        // And what it sends is what another client hears.
        let other = Awareness()
        other.apply(awareness.message())
        #expect(other.peers[awareness.client]?.user.name == "Me")
        awareness.setUser(nil)
        other.apply(awareness.message())
        #expect(other.peers.isEmpty)
    }

    /// A stroke in progress arrives a piece at a time; the pieces are fitted together by where they start.
    @Test func aStrokeInProgressIsPutTogetherFromItsPieces() {
        let awareness = Awareness()
        func piece(_ clock: UInt64, id: String, base: Int, _ points: [[Double]]) {
            let stroke: [String: Any] = ["id": id, "tool": "pen", "ink": "red", "level": 1, "seed": 5, "base": base, "points": points]
            awareness.apply(AwarenessCodec.encode(client: 9, clock: clock, state: state(["stroke": stroke])))
        }
        piece(1, id: "s", base: 0, [[1, 1, 0], [2, 2, 0]])
        piece(2, id: "s", base: 2, [[3, 3, 0]])
        #expect(awareness.peers[9]?.stroke?.op.points?.count == 3)
        #expect(awareness.peers[9]?.stroke?.op.ink == "red")
        // A piece from further on leaves a gap: only what is whole so far is drawn.
        piece(3, id: "s", base: 6, [[7, 7, 0]])
        #expect(awareness.peers[9]?.stroke?.op.points?.count == 3)
        // A new stroke starts afresh; one joined part-way has nothing to draw until its start arrives.
        piece(4, id: "t", base: 4, [[5, 5, 0]])
        #expect(awareness.peers[9]?.stroke?.op.points?.isEmpty == true)
        piece(5, id: "t", base: 0, [[1, 1, 0], [2, 2, 0], [3, 3, 0], [4, 4, 0]])
        #expect(awareness.peers[9]?.stroke?.op.points?.count == 5)
    }

    @Test func someoneWhoLeftTheDocumentIsForgotten() {
        let awareness = Awareness()
        awareness.apply(AwarenessCodec.encode(client: 7, clock: 1, state: state([:])))
        let present = UUID(uuidString: "0190a000-0000-7000-8000-000000000001")!
        awareness.keep(userIds: [present])
        #expect(awareness.peers[7] != nil)
        awareness.keep(userIds: [])
        #expect(awareness.peers.isEmpty)
    }

    @Test func aColourIsReadBackFromWhatTheWebSends() {
        #expect(PresenceColor.css(slot: 2).hasPrefix("oklch(var(--design-docs-presence-lightness,65%) .15 275."))
        let red = PresenceColor.components("#ff0000", dark: false)
        #expect(red.red == 1 && red.green == 0 && red.blue == 0)
        // Hue 0 at this chroma is a pink; on a dark page it is the lighter of the two.
        let light = PresenceColor.components(PresenceColor.css(slot: 0), dark: false)
        let dark = PresenceColor.components(PresenceColor.css(slot: 0), dark: true)
        #expect(light.red > light.green && light.red > light.blue)
        #expect(dark.red > light.red && dark.green > light.green)
        for value in [light.red, light.green, light.blue, dark.red, dark.green, dark.blue] {
            #expect((0...1).contains(value))
        }
        // Nonsense falls back to the web's default rather than to black.
        let fallback = PresenceColor.components("tomato-ish", dark: true)
        #expect(fallback.blue > fallback.green)
    }
}

@Suite struct SketchHistoryTests {
    private func stroke(_ id: String) -> SketchOp {
        SketchOp(id: id, tool: "pen", ink: "blue", level: 1, seed: 1, pointerType: "pen", points: [[1, 2, 0.5]])
    }

    private func ids(_ doc: SharedDoc) -> [String] { doc.sketchStrokes().map(\.id) }

    @Test func aStrokeIsTakenBackAndPutBackOneAtATime() {
        let doc = SharedDoc()
        let history = doc.sketchHistory()
        for id in ["a", "b", "c"] {
            doc.appendSketchOp(stroke(id))
            history.closeStep()
        }
        #expect(history.undo())
        #expect(ids(doc) == ["a", "b"])
        #expect(history.undo())
        #expect(ids(doc) == ["a"])
        #expect(history.redo())
        #expect(ids(doc) == ["a", "b"])
        #expect(history.redo())
        #expect(ids(doc) == ["a", "b", "c"])
        // Nothing left to put back.
        #expect(!history.redo())
    }

    @Test func aClearedPageComesBackWhole() {
        let doc = SharedDoc()
        let history = doc.sketchHistory()
        for id in ["a", "b"] {
            doc.appendSketchOp(stroke(id))
            history.closeStep()
        }
        doc.clearSketch()
        history.closeStep()
        #expect(ids(doc).isEmpty)
        #expect(history.undo())
        #expect(ids(doc) == ["a", "b"])
        #expect(history.redo())
        #expect(ids(doc).isEmpty)
    }

    /// Undo is for what was done here: a stroke that arrived from someone else stays.
    @Test func someoneElsesStrokeIsNotUndone() {
        let theirs = SharedDoc()
        theirs.appendSketchOp(stroke("theirs"))

        let doc = SharedDoc()
        let history = doc.sketchHistory()
        doc.appendSketchOp(stroke("mine"))
        history.closeStep()
        doc.apply([theirs.fullState()])
        #expect(Set(ids(doc)) == ["mine", "theirs"])

        #expect(history.undo())
        #expect(ids(doc) == ["theirs"])
        #expect(!history.undo())
        #expect(ids(doc) == ["theirs"])
    }

    /// What is undone here reaches everyone else as an ordinary update.
    @Test func anUndoTravels() {
        let doc = SharedDoc(), other = SharedDoc()
        let history = doc.sketchHistory()
        doc.appendSketchOp(stroke("a"))
        history.closeStep()
        other.apply([doc.fullState()])
        #expect(ids(other) == ["a"])
        let before = other.stateVector()
        #expect(history.undo())
        other.apply([doc.update(since: before)])
        #expect(ids(other).isEmpty)
    }
}

@Suite struct SketchTests {
    /// Values from the web editor's own `jitter` and `pressureWidth`: a
    /// sketch has to rasterize to the same pixels on every client.
    @Test func jitterMatchesTheWebRasterizer() {
        let expected: [(Int, Int, (Int, Int))] = [
            (0, 0, (-1, -1)), (1, 1, (0, 1)), (12345, 7, (-1, -1)), (2_147_483_646, 999, (-1, 0)),
            (987_654_321, 123_456, (0, 1)), (42, 3, (-1, 0)),
        ]
        for (seed, index, wobble) in expected {
            let result = SketchRaster.jitter(seed: seed, index: index)
            #expect(result == wobble, "seed \(seed), sample \(index)")
        }
    }

    @Test func pressureWidthsMatchTheWebRasterizer() {
        #expect(SketchRaster.pressureWidth(level: 0, pressure: 0, pointerType: "touch") == 1)
        #expect(SketchRaster.pressureWidth(level: 1, pressure: 0.5, pointerType: "pen") == 5)
        #expect(SketchRaster.pressureWidth(level: 4, pressure: 1, pointerType: "pen") == 8)
        #expect(SketchRaster.pressureWidth(level: 2, pressure: 0.2, pointerType: "mouse") == 3)
        #expect(SketchRaster.pressureWidth(level: 0, pressure: 1, pointerType: "pen") == 8)
        #expect(SketchRaster.pressureWidth(level: 3, pressure: 0.35, pointerType: "touch") == 6)
    }

    private var page: SketchPage {
        SketchPage(DocSketch(id: UUID(), noteId: UUID(), name: "Test", pageSize: "A5", orientation: "landscape", pattern: "empty",
                             lastEditedBy: nil, lastEditedAt: nil, version: 1, path: "attachments/x/Test.sketch"))
    }

    @Test func pagesTurnWithTheirOrientation() {
        #expect(page.width == 595 && page.height == 420)
    }

    @Test func aStrokeInksThePageAndAnEraserClearsIt() {
        var raster = SketchRaster(page: page)
        let points: [[Double]] = [[100, 100, 0], [140, 100, 0]]
        raster.paint(SketchOp(id: "a", tool: "pen", ink: "red", level: 2, seed: 7, pointerType: "touch", points: points))
        let inked = raster.pixels.filter { $0 != 0 }
        #expect(!inked.isEmpty)
        #expect(Set(inked) == [SketchInk.red.index])
        raster.paint(SketchOp(id: "b", tool: "eraser", ink: "red", level: 4, seed: 7, pointerType: "touch", points: points))
        #expect(raster.pixels.allSatisfy { $0 == 0 })
    }

    @Test func aStrokeOffThePageIsHarmless() {
        var raster = SketchRaster(page: page)
        raster.paint(SketchOp(id: "a", tool: "pen", ink: "theme", level: 4, seed: 1, pointerType: "pen",
                              points: [[-50, -50, 1], [5000, 5000, 1], [.infinity, 0, 1]]))
        #expect(raster.pixels.count == page.width * page.height)
    }

    /// The Yjs binding traps when a value won't decode, and a sketch holds
    /// whatever any client wrote: nothing in it may be able to fail.
    @Test func nothingInASketchCanFailToDecode() throws {
        let decoder = JSONDecoder()
        for json in ["42", "\"text\"", "null", "{}", "[1,2]", "{\"id\":7,\"points\":\"nope\"}", "{\"points\":[[\"a\"]]}"] {
            let op = try decoder.decode(SketchOp.self, from: Data(json.utf8))
            var raster = SketchRaster(page: page)
            raster.paint(op)
            #expect(raster.pixels.allSatisfy { $0 == 0 }, "\(json)")
            _ = try decoder.decode(SketchCut.self, from: Data(json.utf8))
            _ = try decoder.decode(LooseString.self, from: Data(json.utf8))
        }
        let odd = try decoder.decode(SketchOp.self, from: Data(
            "{\"id\":\"a\",\"points\":[[1,2,null],[3,4]],\"seed\":1.5e3,\"level\":\"thick\",\"shift\":true}".utf8))
        #expect(odd.points == [[1, 2, 0], [3, 4]])
        #expect(odd.seed == 1500 && odd.level == nil)
        let cut = try decoder.decode(SketchCut.self, from: Data("[{\"id\":\"piece\"}]".utf8))
        #expect(cut.stationary.map(\.id) == ["piece"] && cut.moved.isEmpty)
    }

    @Test func aSketchCarriesItsOwnPage() {
        let first = SharedDoc(), second = SharedDoc()
        first.setSketchMeta(["pageSize": "A3", "orientation": "landscape", "pattern": "grid"])
        second.apply([first.fullState()])
        let sketch = DocSketch(id: UUID(), noteId: UUID(), name: "Test", pageSize: "A5", orientation: "portrait", pattern: "empty",
                               lastEditedBy: nil, lastEditedAt: nil, version: 1, path: "attachments/x/Test.sketch")
        let carried = SketchPage(sketch, meta: second.sketchMeta())
        #expect(carried.size == "A3" && carried.width == 1191 && carried.height == 842 && carried.pattern == "grid")
        // What it doesn't say comes from the instance's record; what it says wrongly is ignored.
        let partial = SketchPage(sketch, meta: ["pattern": "dots"])
        #expect(partial.size == "A5" && partial.orientation == "portrait" && partial.pattern == "dots")
        let nonsense = SketchPage(sketch, meta: ["pageSize": "B5", "orientation": "sideways", "pattern": "plaid"])
        #expect(nonsense.size == "A4" && nonsense.orientation == "portrait" && nonsense.pattern == "empty")
    }

    @Test func strokesSurviveTheSharedDocument() {
        let first = SharedDoc(), second = SharedDoc()
        let stroke = SketchOp(id: "one", tool: "pen", ink: "blue", level: 1, seed: 99, pointerType: "pen", points: [[1, 2, 0.5]])
        first.appendSketchOp(stroke)
        second.apply([first.fullState()])
        #expect(second.sketchStrokes() == [stroke])
        second.clearSketch()
        #expect(second.sketchStrokes().isEmpty)
        // And the clearing travels back.
        first.apply([second.fullState()])
        #expect(first.sketchStrokes().isEmpty)
    }
}
