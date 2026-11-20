import Foundation
import Testing
import ZIPFoundation
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

// What is done to a downloaded design doc's tree is shown before the instance has heard of
// it, and sent with enough said for the instance to refuse it if its ground has moved.
@Suite struct OfflineTreeTests {
    private let vault = UUID(), folder = UUID(), inner = UUID(), loose = UUID(), filed = UUID(), deep = UUID()
    private let drawing = UUID(), picture = UUID()

    private func note(_ id: UUID, _ name: String, in folder: UUID?, version: UInt32) -> DocNote {
        DocNote(id: id, vaultId: vault, folderId: folder, name: name, markdown: "", seq: "0", headings: [], lastEditedBy: nil,
                lastEditedAt: nil, version: version)
    }

    /// A folder with a folder in it, a note in each and one in neither; a sketch on the deep
    /// note, and a picture.
    private var base: OfflineBase {
        OfflineBase(
            tree: DocTree(vault: DocVault(id: vault, name: "Design", isClosed: false, closedAt: nil, version: 7),
                          folders: [DocFolder(id: folder, parentId: nil, name: "World", version: 1),
                                    DocFolder(id: inner, parentId: folder, name: "Towns", version: 2)],
                          notes: [note(loose, "Loose", in: nil, version: 3), note(filed, "Filed", in: folder, version: 4),
                                  note(deep, "Deep", in: inner, version: 5)]),
            sketches: [DocSketch(id: drawing, noteId: deep, name: "Map", pageSize: "A4", orientation: "portrait", pattern: "empty",
                                 lastEditedBy: nil, lastEditedAt: nil, version: 6, path: "attachments/x/Map.sketch")],
            images: [DocImage(id: picture, path: "attachments/x/Photo.jpg", contentType: "image/jpeg", width: 1, height: 1, bytes: 9,
                              version: 8)],
            canWrite: true, canManage: true, projectWrite: true)
    }

    private func command(_ entity: OfflineCommand.Entity, _ action: OfflineCommand.Action, _ target: UUID, at second: Double,
                         name: String? = nil, parent: UUID? = nil) -> OfflineCommand {
        OfflineCommand(vaultId: vault, created: Date(timeIntervalSince1970: second), entity: entity, action: action, targetId: target,
                       name: name, parentId: parent)
    }

    @Test func whatIsStillToSendIsShownInTheOrderItWasDone() {
        let made = UUID()
        // Given out of order: a note made, then renamed and moved, then a folder renamed.
        let view = OfflineTree.projected(base, [
            command(.folder, .update, folder, at: 3, name: "Places"),
            command(.note, .update, made, at: 2, name: "Second name", parent: inner),
            command(.note, .create, made, at: 1, name: "First name"),
        ])
        #expect(view.tree.notes.first { $0.id == made }?.name == "Second name")
        #expect(view.tree.notes.first { $0.id == made }?.folderId == inner)
        #expect(view.tree.folders.first { $0.id == folder }?.name == "Places")
        #expect(view.tree.notes.count == 4)
    }

    @Test func aFolderTakesWhatIsInItWithIt() {
        let delete = command(.folder, .delete, folder, at: 1)
        let view = OfflineTree.projected(base, [delete])
        #expect(view.tree.folders.isEmpty)
        #expect(view.tree.notes.map(\.id) == [loose])
        #expect(view.sketches.isEmpty)
        // A picture belongs to no folder, and stays.
        #expect(view.images.count == 1)

        // What the instance is told it takes: each row with its version, and the documents among them.
        let removed = OfflineTree.removed(by: delete, from: base)
        #expect(removed.versions == [folder.api: 1, inner.api: 2, filed.api: 4, deep.api: 5, drawing.api: 6])
        #expect(Set(removed.documents) == [filed, deep, drawing])
    }

    // MARK: The recovery archive

    @Test func aNoteIsAFileUnderTheFoldersItIsIn() {
        var view = base
        // One made here with a name the instance has already, in the same folder.
        let twin = UUID()
        view.tree.notes.append(note(twin, "Filed", in: folder, version: 0))
        let files = OfflineRecovery.notes(of: view) { $0.name + " text" }
        #expect(Set(files.keys) == ["Loose.md", "World/Filed.md", "World/Filed-\(twin.api).md", "World/Towns/Deep.md"])
        #expect(files["World/Towns/Deep.md"].map { String(decoding: $0, as: UTF8.self) } == "Deep text")
    }

    @Test func aSketchIsEmbeddedAsThePictureTheArchiveHasOfIt() {
        let sketch = base.sketches[0]
        let text = """
        ![[attachments/x/Map.sketch]] and ![[Map.sketch|300]]
        ![the map](attachments/x/Map.sketch) but not `![[Map.sketch]]`
        ![[attachments/x/Photo.jpg]]
        """
        // A wiki embed is found from the vault's top wherever the note is; a link is relative to the note.
        #expect(OfflineRecovery.exporting(text, sketches: [sketch], notePath: "World/Towns/Deep.md") == """
        ![[attachments/x/Map.sketch.png]] and ![[attachments/x/Map.sketch.png|300]]
        ![the map](../../attachments/x/Map.sketch.png) but not `![[Map.sketch]]`
        ![[attachments/x/Photo.jpg]]
        """)
        #expect(OfflineRecovery.exporting("![m](attachments/x/Map.sketch)", sketches: [sketch], notePath: "Loose.md")
                == "![m](attachments/x/Map.sketch.png)")
    }

    @Test func whatTheInstanceDeletedIsStillInTheArchive() {
        var current = base
        current.tree.notes.removeAll { $0.id == deep }
        current.sketches = []
        let aside = OfflineRecovery.settingAside(base, gone: current, into: nil)
        #expect(aside?.tree.notes.contains { $0.id == deep } == true)
        // Nothing gone, nothing set aside.
        #expect(OfflineRecovery.settingAside(base, gone: base, into: nil) == nil)

        let whole = OfflineRecovery.restoring(current, aside)
        #expect(whole.tree.notes.count == 3)
        #expect(whole.sketches.map(\.id) == [drawing])
        #expect(whole.tree.folders.count == 2)
        #expect(OfflineRecovery.notes(of: whole) { _ in "" }.keys.contains("World/Towns/Deep.md"))
    }

    @Test func theArchiveIsAZipOfItsFiles() throws {
        let files = ["World/Filed.md": Data("# Filed\n".utf8), "solo-recovery.json": Data("{}".utf8),
                     "attachments/x/Photo.jpg": Data((0..<4000).map { UInt8($0 % 251) })]
        let data = try OfflineRecovery.zip(files)
        let archive = try Archive(data: data, accessMode: .read)
        #expect(archive.map(\.path).sorted() == files.keys.sorted())
        for (path, expected) in files {
            var read = Data()
            _ = try archive.extract(try #require(archive[path])) { read.append($0) }
            #expect(read == expected)
        }
        #expect(OfflineRecovery.archiveName("Design") == "Design-recovery.zip")
        #expect(OfflineRecovery.archiveName("") == "vault-recovery.zip")
    }

    /// The names in an archive are the instance's. Whatever it sends, nothing in the archive
    /// is addressed to anywhere but the folder the archive is opened into.
    @Test func nothingInTheArchiveLeadsOutOfIt() throws {
        #expect(OfflineRecovery.entryName("World/Towns/Deep.md") == "World/Towns/Deep.md")
        #expect(OfflineRecovery.entryName("../../Library/Preferences/x.plist") == "Library/Preferences/x.plist")
        #expect(OfflineRecovery.entryName("/etc/hosts") == "etc/hosts")
        #expect(OfflineRecovery.entryName("a/./b/../c") == "a/b/c")
        #expect(OfflineRecovery.entryName("..\\..\\up.md") == "up.md")
        #expect(OfflineRecovery.entryName("Ti\u{0}tle\n.md") == "Title.md")
        #expect(OfflineRecovery.entryName("../..") == "unnamed")

        // Two that come to the same name are both kept.
        let data = try OfflineRecovery.zip(["../Note.md": Data("up".utf8), "Note.md": Data("here".utf8)])
        let archive = try Archive(data: data, accessMode: .read)
        #expect(archive.map(\.path).sorted() == ["2-Note.md", "Note.md"])
    }

    @Test func aDeletedDesignDocTakesEverything() {
        let delete = command(.vault, .delete, vault, at: 1)
        #expect(OfflineTree.projected(base, [delete]).deleted)
        let removed = OfflineTree.removed(by: delete, from: base)
        #expect(removed.versions.count == 7)
        #expect(removed.versions[picture.api] == 8)
        #expect(Set(removed.documents) == [loose, filed, deep, drawing])
    }

    @Test func closingADesignDocStopsWritingAndOpeningItAgainAllowsIt() {
        var close = command(.vault, .update, vault, at: 1, name: "Design")
        close.isClosed = true
        var view = OfflineTree.projected(base, [close])
        #expect(view.tree.vault.isClosed)
        #expect(!view.canWrite)
        view = OfflineTree.projected(base, [close, command(.vault, .update, vault, at: 2, name: "Renamed")])
        #expect(view.canWrite)
        #expect(view.tree.vault.name == "Renamed")
    }

    @Test func whatTheInstanceMadeOfItIsWhatIsKept() {
        var kept = base
        let made = UUID()
        // The name was taken there meanwhile, so the instance gave it another.
        let answer = note(made, "Notes (conflict 2)", in: nil, version: 41)
        OfflineTree.apply(command(.note, .create, made, at: 1, name: "Notes"), to: &kept, result: .note(answer))
        #expect(kept.tree.notes.last?.name == "Notes (conflict 2)")
        #expect(kept.tree.notes.last?.version == 41)
    }

    @Test func aRenamedSketchIsStoredUnderItsNewName() {
        let view = OfflineTree.projected(base, [command(.sketch, .update, drawing, at: 1, name: "Plan")])
        #expect(view.sketches.first?.name == "Plan")
        #expect(view.sketches.first?.path == DocMarkdown.attachmentPath(noteId: deep, name: "Plan.sketch"))
        #expect(view.sketches.first?.noteId == deep)
    }

    @Test func namesAreJudgedAsTheInstanceJudgesThem() {
        #expect(OfflineTree.validName("Level 3 — lighting"))
        for bad in ["", "   ", ".", "..", "a/b", "a\\b", "a:b", "a#b", "[[a]]", "a|b", "tab\there", String(repeating: "x", count: 201)] {
            #expect(!OfflineTree.validName(bad), "\(bad)")
        }
        #expect(!OfflineTree.validName(nil))
    }

    @Test func embedsFollowAThingToWhereItWasStored() {
        let text = """
        ![[attachments/x/Map.sketch]] and ![[attachments/x/Map.sketch|300]]
        ![alt](attachments/x/Map.sketch) but not `![[attachments/x/Map.sketch]]`
        ```
        ![[attachments/x/Map.sketch]]
        ```
        ![[attachments/x/Other.sketch]]
        """
        let doc = SharedDoc()
        doc.replaceText(start: 0, length: 0, with: text)
        let edits = OfflineTree.embedEdits(in: text, from: "attachments/x/Map.sketch", to: "attachments/x/Map (conflict 2).sketch")
        #expect(edits.count == 3)
        edits.forEach { doc.replaceText(start: $0.start, length: $0.length, with: $0.text) }
        #expect(doc.string() == """
        ![[attachments/x/Map (conflict 2).sketch]] and ![[attachments/x/Map (conflict 2).sketch|300]]
        ![alt](attachments/x/Map%20(conflict%202).sketch) but not `![[attachments/x/Map.sketch]]`
        ```
        ![[attachments/x/Map.sketch]]
        ```
        ![[attachments/x/Other.sketch]]
        """)
        #expect(OfflineTree.embedEdits(in: text, from: "attachments/x/Map.sketch", to: "attachments/x/Map.sketch").isEmpty)
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

    // A downloaded design doc keeps each document twice — as it stands here, and as the
    // instance is known to have it — and owes the instance the difference.

    @Test func whatIsOwedIsWhatWasWrittenSinceTheInstanceWasLastInStep() {
        let editor = SharedDoc()
        editor.replaceText(start: 0, length: 0, with: "One line.")
        let server = editor.fullState()
        // Written with the instance out of reach, some of it taken back out again.
        editor.replaceText(start: 9, length: 0, with: " Two lines. Three.")
        editor.replaceText(start: 20, length: 7, with: "")
        let owed = YStates.missing(from: server, in: editor.fullState())
        // Paid, the instance has what is here; and someone who wrote there meanwhile loses nothing.
        let instance = SharedDoc()
        instance.apply([server])
        instance.replaceText(start: 0, length: 0, with: "Zero. ")
        instance.apply([owed])
        #expect(instance.string() == "Zero. One line. Two lines.")
        #expect(YStates.markdown(YStates.merged([server, owed])) == "One line. Two lines.")
    }

    @Test func mergingIsTheSameInAnyOrderAndAnyNumberOfTimes() {
        let editor = SharedDoc()
        editor.replaceText(start: 0, length: 0, with: "a")
        let first = editor.fullState()
        editor.replaceText(start: 1, length: 0, with: "b")
        let second = editor.update(since: SharedDoc().stateVector())
        #expect(YStates.markdown(YStates.merged([first, second])) == "ab")
        // An update that got there and was sent again, and one that arrives before the one it follows.
        #expect(YStates.markdown(YStates.merged([second, first, second, []])) == "ab")
    }

    @Test func anImportedNoteBecomesADocumentOfItsText() {
        #expect(YStates.markdown(YStates.seeded("# Imported\n\nBody 😀")) == "# Imported\n\nBody 😀")
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

/// The web editor's own cases (js/sketch/viewport.test.js, touch-tap.test.js), in its numbers.
@Suite struct SketchViewTests {
    private let view = SketchView(pan: CGPoint(x: 35, y: -20), zoom: 1.2, angle: 13 * .pi / 180)
    private let anchor = CGPoint(x: 245, y: 180)

    private func close(_ a: CGPoint, _ b: CGPoint) -> Bool {
        abs(a.x - b.x) < 1e-8 && abs(a.y - b.y) < 1e-8
    }

    @Test func aPointOfThePageIsFoundAgainInTheView() {
        #expect(close(view.viewPoint(view.pagePoint(anchor)), anchor))
    }

    @Test func zoomingAndTurningKeepThePagePointUnderTheAnchor() {
        let next = view.anchored(at: anchor, zoom: 1.37, angle: 27.25 * .pi / 180)
        #expect(close(view.pagePoint(anchor), next.pagePoint(anchor)))
        #expect(next.zoom == 1.37)
        #expect(abs(next.angle - 27.25 * .pi / 180) < 1e-12)
    }

    @Test func twoFingersPanScaleAndTurnAboutWhereTheyBegan() {
        let pinch = SketchView.Pinch(view: view, CGPoint(x: 50, y: 100), CGPoint(x: 150, y: 100))
        let next = pinch.view(CGPoint(x: 125, y: 100), CGPoint(x: 125, y: 225))
        #expect(abs(next.zoom - 1.5) < 1e-12)
        #expect(abs(next.angle - 103 * .pi / 180) < 1e-12)
        #expect(close(view.pagePoint(CGPoint(x: 100, y: 100)), next.pagePoint(CGPoint(x: 125, y: 162.5))))
    }

    @Test func eachFingerStaysOnItsOwnPointOfThePage() {
        let a = CGPoint(x: 80, y: 300), b = CGPoint(x: 260, y: 340)
        let pinch = SketchView.Pinch(view: view, a, b)
        let movedA = CGPoint(x: 40, y: 190), movedB = CGPoint(x: 310, y: 420)
        let next = pinch.view(movedA, movedB)
        #expect(abs(view.pagePoint(a).x - next.pagePoint(movedA).x) < 1e-6)
        #expect(abs(view.pagePoint(a).y - next.pagePoint(movedA).y) < 1e-6)
        #expect(abs(view.pagePoint(b).x - next.pagePoint(movedB).x) < 1e-6)
        #expect(abs(view.pagePoint(b).y - next.pagePoint(movedB).y) < 1e-6)
    }

    @Test func theZoomStopsAtItsLimitsAndTheAngleWraps() {
        #expect(view.anchored(at: anchor, zoom: 400).zoom == 8)
        #expect(view.anchored(at: anchor, zoom: 0.001).zoom == 0.1)
        #expect(abs(view.anchored(at: anchor, angle: 3 * .pi).angle - .pi) < 1e-9)
    }

    @Test func aPageStopsShortOfTheLimitWhereItWouldBeTooLargeToDraw() {
        // A page a thumb can cross goes all the way in, whatever the screen.
        #expect(SketchView.most(for: CGSize(width: 420, height: 595), screenScale: 3) == 8)
        // A3 on a phone would be 28,584 pixels tall at eight times, and is held to 24,000.
        let a3 = SketchView.most(for: CGSize(width: 842, height: 1191), screenScale: 3)
        #expect(abs(a3 - 24000 / (1191 * 3)) < 1e-9)
        // The same page on an iPad's coarser screen has the room.
        #expect(SketchView.most(for: CGSize(width: 842, height: 1191), screenScale: 2) == 8)

        // And the page's own limit is the one a pinch stops at, with the place held still held.
        let held = view.pagePoint(anchor)
        let next = view.anchored(at: anchor, zoom: 400, most: a3)
        #expect(next.zoom == a3)
        #expect(abs(next.pagePoint(anchor).x - held.x) < 1e-6)
        #expect(abs(next.pagePoint(anchor).y - held.y) < 1e-6)
    }

    @Test func aPageAlreadyPastItsLimitIsNotPulledBackByBeingMoved() {
        var deep = view
        deep.zoom = 7
        // Turned or carried, with no zoom asked for: it stays as far in as it was.
        #expect(deep.anchored(at: anchor, angle: 0.3, most: 4).zoom == 7)
        // Zoomed, it comes back to the limit.
        #expect(deep.anchored(at: anchor, zoom: 7.5, most: 4).zoom == 4)
    }

    @Test func aPictureGoesInUntilItsPixelsAreAsLargeAsTheyAreAnyUse() {
        // A sketch 595 points across, drawn at one pixel to a point and fitted to 372: eight
        // points to a pixel is 12.8 times.
        let sketch = ZoomLimit.most(pixel: ZoomLimit.sketchPixel, fitted: 372.0 / 595, density: 1, longestSide: 527, screenScale: 3)
        #expect(abs(sketch - 8 * 595.0 / 372) < 1e-9)
        // A photograph 2,560 pixels across, fitted to the same: two points to a pixel.
        let photo = ZoomLimit.most(pixel: ZoomLimit.photoPixel, fitted: 372.0 / 2560, density: 1, longestSide: 372, screenScale: 3)
        #expect(abs(photo - 2 * 2560.0 / 372) < 1e-9)
        // One so small it is already past that when fitted can still be looked at closer.
        #expect(ZoomLimit.most(pixel: ZoomLimit.photoPixel, fitted: 6, density: 1, longestSide: 372, screenScale: 3) == ZoomLimit.least)
        // And none is let grow past what can be drawn.
        #expect(ZoomLimit.most(wanted: 50, longestSide: 1000, screenScale: 2) == 12)
    }

    @Test func aFittedPageIsUprightAndInTheMiddle() {
        let room = CGRect(x: 16, y: 80, width: 400, height: 600)
        let fitted = SketchView.fitting(page: CGSize(width: 420, height: 595), in: room)
        #expect(fitted.angle == 0)
        #expect(abs(fitted.zoom - 400.0 / 420) < 1e-12)
        #expect(close(fitted.viewPoint(CGPoint(x: 210, y: 297.5)), CGPoint(x: room.midX, y: room.midY)))
    }

    @Test func twoFingersTappedTogetherUndoAndThreeRedo() {
        final class Finger {}
        let one = Finger(), two = Finger(), three = Finger()
        var tap = SketchTouchTap()
        let first = tap.down(ObjectIdentifier(one), at: CGPoint(x: 300, y: 300), time: 1.000)
        let second = tap.down(ObjectIdentifier(two), at: CGPoint(x: 500, y: 300), time: 1.030)
        #expect(first)
        #expect(!second)
        let early = tap.up(ObjectIdentifier(one), at: CGPoint(x: 304, y: 306), time: 1.100)
        let undo = tap.up(ObjectIdentifier(two), at: CGPoint(x: 493, y: 296), time: 1.110)
        #expect(early == nil)
        #expect(undo == .undo)

        for (index, finger) in [one, two, three].enumerated() {
            _ = tap.down(ObjectIdentifier(finger), at: CGPoint(x: 300 + index * 100, y: 300), time: 2 + Double(index) / 1000)
        }
        let lifts = [one, two, three].enumerated().map { index, finger in
            tap.up(ObjectIdentifier(finger), at: CGPoint(x: 300 + index * 100, y: 300), time: 2.1)
        }
        #expect(lifts == [nil, nil, .redo])
    }

    @Test func aPinchOrALongHoldIsNotATap() {
        final class Finger {}
        let one = Finger(), two = Finger()
        var tap = SketchTouchTap()
        _ = tap.down(ObjectIdentifier(one), at: CGPoint(x: 300, y: 300), time: 1)
        _ = tap.down(ObjectIdentifier(two), at: CGPoint(x: 500, y: 300), time: 1.01)
        tap.move(ObjectIdentifier(two), to: CGPoint(x: 600, y: 300))
        let pinched = [tap.up(ObjectIdentifier(one), at: CGPoint(x: 300, y: 300), time: 1.06),
                       tap.up(ObjectIdentifier(two), at: CGPoint(x: 600, y: 300), time: 1.07)]
        #expect(pinched == [nil, nil])

        _ = tap.down(ObjectIdentifier(one), at: CGPoint(x: 300, y: 300), time: 3)
        _ = tap.down(ObjectIdentifier(two), at: CGPoint(x: 500, y: 300), time: 3.01)
        let held = [tap.up(ObjectIdentifier(one), at: CGPoint(x: 300, y: 300), time: 3.5),
                    tap.up(ObjectIdentifier(two), at: CGPoint(x: 500, y: 300), time: 3.6)]
        #expect(held == [nil, nil])
    }
}

/// Pages of extensions the app has no native half for: which there are, who sees them, and
/// which of the web client's addresses come back to the app.
@Suite struct PluginPageTests {
    private func project(owner: Bool = false, role: ProjectRole = .read) -> ProjectSummary {
        ProjectSummary(id: UUID(), name: "Demo", alias: "DMD", icon: nil, imageId: nil, role: role, isOwner: owner)
    }

    @Test func aRouteWithAProjectInItIsListedPerProject() {
        let page = PluginPage(extensionId: "sprints", label: "Sprints", icon: "Flag", route: "/sprints/{projectAlias}", requires: "Read")
        #expect(page.isProjectLevel)
        #expect(page.path(project: "DMD") == "sprints/DMD")
        // The token is matched as the web's sidebar matches it, whatever its case.
        let shouted = PluginPage(extensionId: "sprints", label: "Sprints", icon: nil, route: "/sprints/{ProjectAlias}/board", requires: nil)
        #expect(shouted.isProjectLevel)
        #expect(shouted.path(project: "DMD") == "sprints/DMD/board")
    }

    @Test func aRouteWithoutOneIsListedOnceForTheInstance() {
        let page = PluginPage(extensionId: "mcp", label: "MCP access", icon: "Robot", route: "/mcp", requires: "Read")
        #expect(!page.isProjectLevel)
        #expect(page.path() == "mcp")
        // The names a manifest takes are the ones a project's icon takes.
        #expect(page.symbol == SoloIcons.symbol("Robot"))
    }

    @Test func anIconTheAppDoesNotKnowIsThePuzzlePiece() {
        let page = PluginPage(extensionId: "x", label: "X", icon: "SomethingNew", route: "/x", requires: nil)
        #expect(page.symbol == "puzzlepiece.extension")
    }

    @Test func aPageAsksForTheAccessItsManifestNames() {
        func page(_ requires: String?) -> PluginPage {
            PluginPage(extensionId: "x", label: "X", icon: nil, route: "/x/{projectAlias}", requires: requires)
        }
        #expect(page("Read").allows(project()))
        #expect(page(nil).allows(project()))
        #expect(!page("Write").allows(project()))
        #expect(page("Write").allows(project(role: .readWrite)))
        #expect(!page("Owner").allows(project(role: .readWrite)))
        #expect(page("Owner").allows(project(owner: true)))
    }

    @Test func coreScreensComeBackToTheApp() {
        let none = HostedRoutes.Baked()
        #expect(HostedRoutes.destination(for: "", none) == .tab(.work))
        #expect(HostedRoutes.destination(for: "projects", none) == .tab(.projects))
        #expect(HostedRoutes.destination(for: "notifications", none) == .tab(.inbox))
        #expect(HostedRoutes.destination(for: "projects/DMD", none) == .route(.project(alias: "DMD")))
        #expect(HostedRoutes.destination(for: "projects/DMD/boards/PW26", none)
            == .route(.board(project: "DMD", board: "PW26", task: nil)))
        #expect(HostedRoutes.destination(for: "projects/DMD/boards/PW26?task=DMD-PW26-12", none)
            == .route(.board(project: "DMD", board: "PW26", task: 12)))
        #expect(HostedRoutes.destination(for: "projects/DMD/boards/PW26?task=7", none)
            == .route(.board(project: "DMD", board: "PW26", task: 7)))
    }

    @Test func anExtensionsOwnPagesStayInThePage() {
        let none = HostedRoutes.Baked()
        #expect(HostedRoutes.destination(for: "sprints/DMD", none) == nil)
        #expect(HostedRoutes.destination(for: "projects/DMD/sprints/4", none) == nil)
        #expect(HostedRoutes.destination(for: "settings", none) == nil)
    }

    @Test func aBakedExtensionsPagesAreTheAppsOnlyWhereItIsInstalled() {
        let id = UUID()
        let roadmap = "projects/DMD/roadmaps/\(id.uuidString.lowercased())"
        let docs = "docs/DMD/\(id.uuidString.lowercased())"
        #expect(HostedRoutes.destination(for: roadmap, .init()) == nil)
        #expect(HostedRoutes.destination(for: docs, .init()) == nil)
        #expect(HostedRoutes.destination(for: "mcp", .init()) == nil)
        let all = HostedRoutes.Baked(roadmap: true, designDocs: true, mcp: true)
        #expect(HostedRoutes.destination(for: roadmap, all) == .route(.roadmap(project: "DMD", roadmapId: id)))
        #expect(HostedRoutes.destination(for: docs, all) == .route(.designDocs(project: "DMD", vaultId: id)))
        #expect(HostedRoutes.destination(for: "mcp", all) == .route(.mcpAccess))
    }

    @Test func thePatternsThePageIsGivenAgreeWithWhereTheAppGoes() {
        // The page tests a path against these in JavaScript; the app decides with the same list.
        let all = HostedRoutes.Baked(roadmap: true, designDocs: true, mcp: true)
        for path in ["", "projects", "projects/DMD", "projects/DMD/boards/PW26?task=3", "notifications", "mcp"] {
            let claimed = HostedRoutes.patterns(all).contains { path.range(of: $0, options: .regularExpression) != nil }
            #expect(claimed == (HostedRoutes.destination(for: path, all) != nil), "\(path)")
        }
    }
}

/// What extensions add to core's screens, as the web client's kit surface describes it and the
/// app keeps it for the views that draw it.
@Suite struct KitSurfaceTests {
    private func update(_ json: String) throws -> KitUpdate {
        try JSONDecoder().decode(KitUpdate.self, from: Data(json.utf8))
    }

    private let badge = #"{"id":7,"kind":"badge","props":{"text":"3 left","tone":"Warning"},"children":[]}"#

    @Test func aTreeIsReadAsThePageWritesIt() throws {
        let sent = try update(#"""
        {"type":"kit","units":[{"mount":"m1","sections":[{"extension":"sprints","title":"Sprints","order":10,"nodes":[
          {"id":3,"kind":"row","props":{"title":"Sprint 4","href":"sprints/DMD/4","press":"1"},"children":[\#(badge)]}]}]}]}
        """#)
        let section = try #require(sent.units.first?.sections.first)
        #expect(section.extensionId == "sprints")
        #expect(section.title == "Sprints")
        let row = try #require(section.nodes.first)
        #expect(row.kind == "row")
        #expect(row["href"] == "sprints/DMD/4")
        #expect(row.flag("press"))
        #expect(!row.flag("disabled"))
        #expect(row.children.first?["text"] == "3 left")
    }

    @Test func aPlaceKeepsWhatWasLastSaidOfIt() throws {
        let host = KitSurfaceHost()
        host.mount("m1", .projectCard, project: UUID())
        let box = host.box(mount: "m1")
        #expect(box.sections.isEmpty)

        host.apply(try update(#"{"units":[{"mount":"m1","sections":[{"extension":"x","title":"T","order":0,"nodes":[\#(badge)]}]}]}"#))
        #expect(box.sections.map(\.title) == ["T"])
        // The same box every time: a view that read it once is the view that is told.
        #expect(host.box(mount: "m1") === box)

        host.apply(try update(#"{"units":[{"mount":"m1","sections":[]}]}"#))
        #expect(box.sections.isEmpty)
    }

    @Test func oneCardChangingIsOneCardToldAndCardsThatLeftAreForgotten() throws {
        let host = KitSurfaceHost()
        let first = UUID(), second = UUID()
        host.mount("board", .taskCard, board: UUID())
        let section = #"{"extension":"x","title":"","order":0,"nodes":[\#(badge)]}"#
        // The page writes ids in lower case; the app's are upper. They are the same card.
        host.apply(try update(#"""
        {"units":[{"mount":"board","sections":[],"items":[
          {"item":"\#(first.uuidString.lowercased())","sections":[\#(section)]},
          {"item":"\#(second.uuidString.lowercased())","sections":[\#(section)]}]}]}
        """#))
        let one = host.box(mount: "board", item: first.uuidString), two = host.box(mount: "board", item: second.uuidString)
        #expect(one.sections.count == 1)
        #expect(two.sections.count == 1)

        host.apply(try update(#"{"units":[{"mount":"board","item":"\#(first.uuidString.lowercased())","sections":[]}]}"#))
        #expect(one.sections.isEmpty)
        #expect(two.sections.count == 1)

        // The whole board again, without the second card: it has left.
        host.apply(try update(#"{"units":[{"mount":"board","sections":[],"items":[{"item":"\#(first.uuidString.lowercased())","sections":[\#(section)]}]}]}"#))
        #expect(one.sections.count == 1)
        #expect(two.sections.isEmpty)
    }

    @Test func aColumnIsAnItemOfItsBoardNamedByItsStatus() throws {
        let host = KitSurfaceHost()
        let board = UUID()
        let columns = BoardKitMount.columns(for: board)
        // Not the cards' mount: a board's cards and its columns are asked for apart.
        #expect(columns != BoardKitMount.id(for: board))
        host.mount(columns, .boardColumn, project: UUID(), board: board)
        host.apply(try update(#"""
        {"units":[{"mount":"\#(columns)","sections":[],"items":[
          {"item":"Unstarted","sections":[]},
          {"item":"InProgress","sections":[{"extension":"x","title":"","order":0,"nodes":[\#(badge)]}]}]}]}
        """#))
        #expect(host.box(mount: columns, item: TaskState.inProgress.rawValue).sections.count == 1)
        #expect(host.box(mount: columns, item: TaskState.unstarted.rawValue).sections.isEmpty)
        #expect(host.box(mount: columns, item: TaskState.finished.rawValue).sections.isEmpty)
    }

    @Test func aMenuItemSaysWhatItIsAndNothingItIsNot() throws {
        let sent = try update(#"""
        {"units":[{"mount":"menu","sections":[{"extension":"x","title":"","order":0,"nodes":[
          {"id":1,"kind":"menuItem","props":{"label":"Link another task…","press":"1"},"children":[]},
          {"id":2,"kind":"menuItem","props":{"label":"Forget its links","danger":"1","separator":"1","press":"1"},"children":[]}]}]}]}
        """#)
        let items = try #require(sent.units.first?.sections.first?.nodes)
        #expect(items.map(\.kind) == ["menuItem", "menuItem"])
        #expect(items[0]["label"] == "Link another task…")
        #expect(!items[0].flag("danger") && !items[0].flag("separator") && !items[0].flag("disabled"))
        #expect(items[1].flag("danger") && items[1].flag("separator"))
    }

    @Test func aPageIsTheAppsToDrawUntilItIsSeenToNeedTheWeb() throws {
        let host = KitSurfaceHost()
        host.mount("p1", .page, path: "sprints/DMD")
        let box = host.box(mount: "p1")
        // Not yet answered: neither the one nor the other.
        #expect(box.page == nil)

        host.apply(try update(#"{"units":[{"mount":"p1","page":"native","extension":"sprints","sections":[{"extension":"sprints","title":"","order":0,"nodes":[\#(badge)]}],"items":[]}]}"#))
        #expect(box.page == .app)
        #expect(box.owner == "sprints")
        #expect(box.sections.count == 1)

        // It drew something the kit has not got. That is not taken back by a later tree without it.
        host.apply(try update(#"{"units":[{"mount":"p1","page":"web","extension":"sprints","sections":[],"items":[]}]}"#))
        #expect(box.page == .web)
        host.apply(try update(#"{"units":[{"mount":"p1","page":"native","extension":"sprints","sections":[],"items":[]}]}"#))
        #expect(box.page == .web)
    }

    @Test func aPlaceThatIsNotAPageSaysNothingOfWhoDrawsIt() throws {
        let host = KitSurfaceHost()
        host.mount("m1", .dashboard)
        host.apply(try update(#"{"units":[{"mount":"m1","sections":[]}]}"#))
        #expect(host.box(mount: "m1").page == nil)
    }

    @Test func nothingIsWaitedForThatCouldNotAnswer() {
        // No session: no instance to ask, so a page is not kept waiting on the surface.
        #expect(!KitSurfaceHost().mayAnswer(.page))
    }

    @Test func aFieldAndAPickerSayWhatTheyHoldAndAPickersOptionsAreItsChildren() throws {
        let sent = try update(#"""
        {"units":[{"mount":"p1","sections":[{"extension":"x","title":"","order":0,"nodes":[
          {"id":1,"kind":"textField","props":{"label":"Name","value":"","placeholder":"Ada","input":"1"},"children":[]},
          {"id":2,"kind":"picker","props":{"label":"Size","input":"1"},"children":[
            {"id":3,"kind":"option","props":{"value":"s","label":"Small"},"children":[]},
            {"id":4,"kind":"option","props":{"value":"l","label":"Large"},"children":[]}]}]}]}]}
        """#)
        let nodes = try #require(sent.units.first?.sections.first?.nodes)
        // Written nothing yet is an empty value, which is not the same as no value.
        #expect(nodes[0]["value"] == "")
        #expect(!nodes[0].flag("multiline") && !nodes[0].flag("disabled"))
        // Nothing picked is no value at all.
        #expect(nodes[1]["value"] == nil)
        #expect(nodes[1].children.map { $0["value"] } == ["s", "l"])
        #expect(nodes[1].children.map { $0["label"] } == ["Small", "Large"])
    }

    @Test func whatIsSaidOfAPlaceNoLongerMountedIsDropped() throws {
        let host = KitSurfaceHost()
        host.mount("m1", .dashboard)
        host.unmount("m1")
        host.apply(try update(#"{"units":[{"mount":"m1","sections":[{"extension":"x","title":"T","order":0,"nodes":[\#(badge)]}]}]}"#))
        #expect(host.box(mount: "m1").sections.isEmpty)
    }

    @Test func nothingStartsWithoutAnExtensionThatCouldContribute() {
        // No session, so no instance and no extensions: mounting is remembered and nothing runs.
        let host = KitSurfaceHost()
        host.mount("m1", .dashboard)
        #expect(host.state == .idle)
        #expect(host.webView == nil)
    }
}
