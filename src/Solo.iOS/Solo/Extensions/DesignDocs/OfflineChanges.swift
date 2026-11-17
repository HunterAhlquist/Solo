import Foundation

// What is done to a downloaded design doc's tree — a note made, a folder renamed, a sketch
// deleted — is done here first, as the web's download does it (js/offline/store.js,
// `applyCommand` and `projectVault`): written down as a command, shown at once by laying the
// commands still to send over the design doc as the instance last had it, and sent in the
// order they were made (`…/offline/commands`). The instance answers a repeat of a command with
// the answer it gave the first time, and refuses one whose ground has moved — a note renamed
// by someone else, a folder with new work in it — which is then kept for whoever made it to
// decide about.

/// A downloaded design doc as the instance last had it.
nonisolated struct OfflineBase: Codable, Sendable {
    var tree: DocTree
    var sketches: [DocSketch]
    var images: [DocImage]
    var canWrite: Bool
    var canManage: Bool
    /// Whether this person may write in the project at all: what `canWrite` goes back to
    /// when a closed design doc is opened again.
    var projectWrite: Bool
    /// Set only on a design doc as shown, with a deletion still to send laid over it.
    var deleted = false

    private enum CodingKeys: String, CodingKey { case tree, sketches, images, canWrite, canManage, projectWrite }

    init(tree: DocTree, sketches: [DocSketch] = [], images: [DocImage] = [], canWrite: Bool, canManage: Bool, projectWrite: Bool) {
        self.tree = tree
        self.sketches = sketches
        self.images = images
        self.canWrite = canWrite
        self.canManage = canManage
        self.projectWrite = projectWrite
    }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        tree = try container.decode(DocTree.self, forKey: .tree)
        sketches = try container.decode([DocSketch].self, forKey: .sketches)
        images = try container.decode([DocImage].self, forKey: .images)
        canWrite = try container.decode(Bool.self, forKey: .canWrite)
        canManage = try container.decodeIfPresent(Bool.self, forKey: .canManage) ?? false
        projectWrite = try container.decodeIfPresent(Bool.self, forKey: .projectWrite) ?? canWrite
    }
}

/// Something done to a downloaded design doc's tree, to be sent to the instance.
nonisolated struct OfflineCommand: Codable, Identifiable, Sendable {
    nonisolated enum Entity: String, Codable, Sendable { case folder, note, sketch, image, vault }
    nonisolated enum Action: String, Codable, Sendable { case create, update, delete }

    var id = UUID()
    var vaultId: UUID
    var created = Date()
    var entity: Entity
    var action: Action
    var targetId: UUID
    /// The version of the thing this was done to, which the instance checks against its own.
    var version: UInt32 = 0
    var name: String?
    /// The folder a folder or a note is put in; nil is the design doc's root.
    var parentId: UUID?
    /// The note a sketch or a picture belongs to.
    var noteId: UUID?
    var pageSize: String?
    var orientation: String?
    var pattern: String?
    var isClosed = false
    /// What the thing was called and where it was when this was done: a note changed
    /// elsewhere since is still renamed or moved, so long as these still hold.
    var baseName: String?
    var baseParentId: UUID?
    /// For a deletion: every document it takes with it, and how far each had been read — and
    /// every row it takes, with its version. The instance refuses if any has moved on.
    var baseDocuments: [String: String]?
    var baseVersions: [String: UInt32]?

    // Kept here and not sent.

    /// Where a new sketch is expected to be stored, which is what a note embeds it by.
    var path: String?
    /// A new or replaced picture, as it is listed until the instance says what it made of it.
    var image: DocImage?
    /// Whether a picture's bytes are kept beside the command, to go with it.
    var hasData = false
    /// Why the instance refused it, when it did.
    var error: String?

    /// The order things were done in.
    static func order(_ first: OfflineCommand, _ second: OfflineCommand) -> Bool {
        first.created != second.created ? first.created < second.created : first.id.uuidString < second.id.uuidString
    }
}

/// A command as the instance takes it.
nonisolated struct OfflineCommandWire: Encodable, Sendable {
    var id: UUID
    var entity: String
    var action: String
    var targetId: UUID
    var version: UInt32
    var name: String?
    var parentId: UUID?
    var noteId: UUID?
    var pageSize: String?
    var orientation: String?
    var pattern: String?
    var isClosed: Bool
    var data: Data?
    var baseName: String?
    var baseParentId: UUID?
    var baseDocuments: [String: String]?
    var baseVersions: [String: UInt32]?

    init(_ command: OfflineCommand, data: Data?) {
        id = command.id
        entity = command.entity.rawValue
        action = command.action.rawValue
        targetId = command.targetId
        version = command.version
        name = command.name
        parentId = command.parentId
        noteId = command.noteId
        pageSize = command.pageSize
        orientation = command.orientation
        pattern = command.pattern
        isClosed = command.isClosed
        self.data = data
        baseName = command.baseName
        baseParentId = command.baseParentId
        baseDocuments = command.baseDocuments
        baseVersions = command.baseVersions
    }
}

/// What the instance made of a command: the thing as it now is there.
nonisolated enum OfflineResult: Sendable {
    case folder(DocFolder)
    case note(DocNote)
    case sketch(DocSketch)
    case image(DocImage)
    case vault(DocVault)

    var version: UInt32 {
        switch self {
        case .folder(let value): value.version
        case .note(let value): value.version
        case .sketch(let value): value.version
        case .image(let value): value.version
        case .vault(let value): value.version
        }
    }

    var name: String? {
        switch self {
        case .folder(let value): value.name
        case .note(let value): value.name
        case .sketch(let value): value.name
        case .vault(let value): value.name
        case .image: nil
        }
    }

    /// Where the instance stored a sketch or a picture.
    var path: String? {
        switch self {
        case .sketch(let value): value.path
        case .image(let value): value.path
        default: nil
        }
    }

    /// The folder a folder or a note is now in.
    var parentId: UUID? {
        switch self {
        case .folder(let value): value.parentId
        case .note(let value): value.folderId
        default: nil
        }
    }
}

/// Why something could not be done to a downloaded design doc.
nonisolated struct OfflineRefusal: LocalizedError, Sendable {
    var errorDescription: String?

    init(_ message: String) { errorDescription = message }

    static let readOnly = OfflineRefusal("This design doc is read-only. What you changed on this device is kept.")
    static let notManager = OfflineRefusal("Only an administrator can manage a design doc.")
    static let badName = OfflineRefusal("Use a name of up to 200 characters without path separators or Markdown link characters.")
    static let nameTaken = OfflineRefusal("That name is already in use in this folder or note.")
    static let gone = OfflineRefusal("The item no longer exists.")
    static let cycle = OfflineRefusal("A folder cannot contain itself.")
}

nonisolated enum OfflineTree {
    /// A name the instance would take, as it judges one (`VaultsController.ValidName`).
    static func validName(_ name: String?) -> Bool {
        guard let name else { return false }
        let trimmed = name.trimmingCharacters(in: .whitespaces)
        guard !trimmed.isEmpty, name.count <= 200, trimmed != ".", trimmed != ".." else { return false }
        return !name.unicodeScalars.contains { scalar in
            scalar.value < 0x20 || scalar.value == 0x7f || "/\\:#[]|".unicodeScalars.contains(scalar)
        }
    }

    /// The folder and every folder inside it.
    static func folders(under root: UUID, in tree: DocTree) -> Set<UUID> {
        var ids: Set<UUID> = [root]
        var grew = true
        while grew {
            grew = false
            for folder in tree.folders {
                if let parent = folder.parentId, ids.contains(parent), !ids.contains(folder.id) {
                    ids.insert(folder.id)
                    grew = true
                }
            }
        }
        return ids
    }

    /// Does to a design doc what a command says. `result` is what the instance made of it,
    /// once it has; before that the command's own word is taken.
    static func apply(_ command: OfflineCommand, to base: inout OfflineBase, result: OfflineResult? = nil) {
        let delete = command.action == .delete
        switch command.entity {
        case .vault:
            if delete {
                base.deleted = true
                return
            }
            if case .vault(let vault) = result {
                base.tree.vault = vault
            } else {
                base.tree.vault.id = command.targetId
                base.tree.vault.name = command.name ?? base.tree.vault.name
                base.tree.vault.isClosed = command.isClosed
                base.tree.vault.version = command.version
            }
            base.canWrite = base.projectWrite && !base.tree.vault.isClosed

        case .folder:
            if delete {
                let ids = folders(under: command.targetId, in: base.tree)
                base.tree.folders.removeAll { ids.contains($0.id) }
                let notes = Set(base.tree.notes.filter { $0.folderId.map(ids.contains) ?? false }.map(\.id))
                base.tree.notes.removeAll { notes.contains($0.id) }
                base.sketches.removeAll { notes.contains($0.noteId) }
                return
            }
            let index = base.tree.folders.firstIndex { $0.id == command.targetId }
            var next = index.map { base.tree.folders[$0] } ?? DocFolder(id: command.targetId, parentId: nil, name: "", version: 0)
            if case .folder(let folder) = result {
                next = folder
            } else {
                next.name = command.name ?? next.name
                next.parentId = command.parentId
                next.version = command.version
            }
            if let index { base.tree.folders[index] = next } else { base.tree.folders.append(next) }

        case .note:
            if delete {
                base.tree.notes.removeAll { $0.id == command.targetId }
                base.sketches.removeAll { $0.noteId == command.targetId }
                return
            }
            let index = base.tree.notes.firstIndex { $0.id == command.targetId }
            var next = index.map { base.tree.notes[$0] }
                ?? DocNote(id: command.targetId, vaultId: base.tree.vault.id, folderId: nil, name: "", markdown: "", seq: "0",
                           headings: [], lastEditedBy: nil, lastEditedAt: nil, version: 0)
            if case .note(let note) = result {
                next = note
            } else {
                next.name = command.name ?? next.name
                next.folderId = command.parentId
                next.version = command.version
            }
            if let index { base.tree.notes[index] = next } else { base.tree.notes.append(next) }

        case .sketch:
            if delete {
                base.sketches.removeAll { $0.id == command.targetId }
                return
            }
            let index = base.sketches.firstIndex { $0.id == command.targetId }
            let old = index.map { base.sketches[$0] }
            var next: DocSketch
            if case .sketch(let sketch) = result {
                next = sketch
            } else {
                let noteId = command.noteId ?? old?.noteId ?? command.targetId
                let name = command.name ?? old?.name ?? ""
                // A sketch is stored under its name, so a new name is a new place.
                let path = command.path ?? (old.flatMap { $0.name == name ? $0.path : nil }
                    ?? DocMarkdown.attachmentPath(noteId: noteId, name: name + ".sketch"))
                next = DocSketch(id: command.targetId, noteId: noteId, name: name,
                                 pageSize: command.pageSize ?? old?.pageSize ?? "A4",
                                 orientation: command.orientation ?? old?.orientation ?? "portrait",
                                 pattern: command.pattern ?? old?.pattern ?? "empty",
                                 lastEditedBy: old?.lastEditedBy, lastEditedAt: old?.lastEditedAt, version: command.version, path: path)
            }
            if let index { base.sketches[index] = next } else { base.sketches.append(next) }

        case .image:
            if delete {
                base.images.removeAll { $0.id == command.targetId }
                return
            }
            let index = base.images.firstIndex { $0.id == command.targetId }
            var next: DocImage?
            if case .image(let image) = result { next = image } else { next = command.image ?? index.map { base.images[$0] } }
            guard let next else { return }
            if let index { base.images[index] = next } else { base.images.append(next) }
        }
    }

    /// The design doc as it is shown: as the instance last had it, with everything still to
    /// send done to it, in order.
    static func projected(_ base: OfflineBase, _ commands: [OfflineCommand]) -> OfflineBase {
        var view = base
        for command in commands.sorted(by: OfflineCommand.order) {
            apply(command, to: &view)
        }
        return view
    }

    /// What a deletion takes with it: every row that would be gone, with its version, and of
    /// those the notes and sketches. The instance works out the same set and compares.
    static func removed(by command: OfflineCommand, from view: OfflineBase) -> (versions: [String: UInt32], documents: [UUID]) {
        func rows(_ base: OfflineBase) -> [UUID: UInt32] {
            var rows: [UUID: UInt32] = [:]
            base.tree.folders.forEach { rows[$0.id] = $0.version }
            base.tree.notes.forEach { rows[$0.id] = $0.version }
            base.sketches.forEach { rows[$0.id] = $0.version }
            base.images.forEach { rows[$0.id] = $0.version }
            return rows
        }
        var after = view
        apply(command, to: &after)
        // A design doc takes everything; anything else leaves its pictures, which belong to none of it.
        let kept = command.entity == .vault ? [:] : rows(after)
        let gone = rows(view).filter { kept[$0.key] == nil }
        let documents = (view.tree.notes.map(\.id) + view.sketches.map(\.id)).filter { gone[$0] != nil }
        return (Dictionary(uniqueKeysWithValues: gone.map { ($0.key.api, $0.value) }), documents)
    }

    /// Changes every embed of one stored path into another, in a note's text: `![[path]]`
    /// and `![alt](path)`, left alone inside code. Returns the edits, last first, as UTF-16
    /// offsets — the order they can be made in without moving one another.
    static func embedEdits(in markdown: String, from old: String, to new: String) -> [(start: Int, length: Int, text: String)] {
        guard old != new else { return [] }
        var edits: [(Int, Int, String)] = []
        let pattern = /!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/
        let code = codeRanges(markdown)
        for match in markdown.matches(of: pattern) {
            guard let written = match.output.1 ?? match.output.2 ?? match.output.3 else { continue }
            if code.contains(where: { $0.contains(match.range.lowerBound) }) { continue }
            let target = written.split(separator: "|", maxSplits: 1, omittingEmptySubsequences: false).first.map(String.init) ?? ""
            guard (target.removingPercentEncoding ?? target) == old else { continue }
            let start = markdown.utf16.distance(from: markdown.utf16.startIndex, to: written.startIndex)
            let replacement = match.output.1 != nil ? new : (new.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? new)
            edits.append((start, target.utf16.count, replacement))
        }
        return edits.reversed()
    }

    /// Fenced blocks and inline code spans: where an embed is only being written about.
    private static func codeRanges(_ markdown: String) -> [Range<String.Index>] {
        var ranges: [Range<String.Index>] = []
        var fence: String.Index?
        var line = markdown.startIndex
        while line < markdown.endIndex {
            let end = markdown[line...].firstIndex(of: "\n") ?? markdown.endIndex
            let text = markdown[line..<end].drop { $0 == " " }
            if text.hasPrefix("```") || text.hasPrefix("~~~") {
                if let start = fence {
                    ranges.append(start..<end)
                    fence = nil
                } else {
                    fence = line
                }
            } else if fence == nil {
                // Between a pair of backticks on one line.
                var open: String.Index?
                for index in markdown[line..<end].indices where markdown[index] == "`" {
                    if let start = open {
                        ranges.append(start..<markdown.index(after: index))
                        open = nil
                    } else {
                        open = index
                    }
                }
            }
            line = end < markdown.endIndex ? markdown.index(after: end) : end
        }
        if let start = fence { ranges.append(start..<markdown.endIndex) }
        return ranges
    }
}
