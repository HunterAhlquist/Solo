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
