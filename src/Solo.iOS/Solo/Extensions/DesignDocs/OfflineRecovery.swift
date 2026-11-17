import Foundation
import ZIPFoundation

// A downloaded design doc can be taken off the device as a zip, as the web's can
// (js/offline/recovery.js): every note as a Markdown file in its folder, every picture where
// it is stored, every sketch as a picture beside where it is stored, and one file of
// everything else that is kept — each document as it stands here, and what was still to
// send. It is for work the instance refused or never got: the notes read as an Obsidian vault
// would, and nothing in it needs the instance to make sense of.

/// A file of the archive that says what the rest cannot: the documents as Yjs holds them, and
/// what was still owed to the instance.
nonisolated struct OfflineRecoveryRecord: Encodable, Sendable {
    var format = "solo-offline-recovery-v1"
    var vault: OfflineVault
    var documents: [Document]
    var queue: [Queued]

    nonisolated struct Document: Encodable, Sendable {
        var id: UUID
        var vaultId: UUID
        var seq: String
        var deleted: Bool
        /// The document as it stands on the device, as a Yjs update.
        var state: Data
    }

    /// Something that was to be sent: a document's changes, or something done to the tree.
    nonisolated struct Queued: Encodable, Sendable {
        var id: UUID
        var vaultId: UUID
        var created: Date?
        var documentId: UUID?
        /// A document's changes, as a Yjs update.
        var data: Data?
        var command: OfflineCommandWire?
        var error: String?
    }
}

nonisolated enum OfflineRecovery {
    /// A design doc's name as a file's: without what a file's name can't have.
    static func archiveName(_ vault: String) -> String {
        let name = vault.components(separatedBy: CharacterSet(charactersIn: "/\\:")).joined(separator: "-")
        return (name.isEmpty ? "vault" : name) + "-recovery.zip"
    }

    /// The names of the folders a note is in, outermost first.
    static func folderPath(_ id: UUID?, in tree: DocTree) -> [String] {
        var parts: [String] = []
        var seen: Set<UUID> = []
        var next = id
        while let folder = next, !seen.contains(folder), let found = tree.folders.first(where: { $0.id == folder }) {
            seen.insert(folder)
            parts.insert(found.name, at: 0)
            next = found.parentId
        }
        return parts
    }

    /// A note's text with its sketches' embeds pointed at the pictures the archive has of
    /// them: a sketch is no use to anything that reads Markdown, and its picture is.
    /// `notePath` is where the note is in the archive, for an embed written as a relative link.
    static func exporting(_ markdown: String, sketches: [DocSketch], notePath: String) -> String {
        var text = markdown
        let depth = max(0, notePath.split(separator: "/").count - 1)
        for sketch in sketches {
            let picture = sketch.path + ".png"
            for target in Set([sketch.path, sketch.name + ".sketch"]) {
                let edits = OfflineTree.embedEdits(in: text, from: target, to: picture) { wiki, path in
                    wiki ? path : String(repeating: "../", count: depth) + (path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path)
                }
                var units = Array(text.utf16)
                for edit in edits {
                    units.replaceSubrange(edit.start..<(edit.start + edit.length), with: Array(edit.text.utf16))
                }
                text = String(decoding: units, as: UTF16.self)
            }
        }
        return text
    }

    /// The archive's notes: each as a Markdown file under the folders it is in. Two that would
    /// be one file — a name the instance had already and one made here — are told apart by id.
    static func notes(of view: OfflineBase, text: (DocNote) -> String) -> [String: Data] {
        var files: [String: Data] = [:]
        for note in view.tree.notes {
            var path = (folderPath(note.folderId, in: view.tree) + [note.name + ".md"]).joined(separator: "/")
            if files[path] != nil { path = String(path.dropLast(3)) + "-" + note.id.api + ".md" }
            let mine = view.sketches.filter { $0.noteId == note.id }
            files[path] = Data(exporting(text(note), sketches: mine, notePath: path).utf8)
        }
        return files
    }

    /// The rows the instance no longer has, put back beside the ones it has: a note deleted
    /// there whose text was written here is still a note of the archive.
    static func restoring(_ base: OfflineBase, _ recovery: OfflineBase?) -> OfflineBase {
        guard let recovery else { return base }
        var whole = base
        for folder in recovery.tree.folders where !whole.tree.folders.contains(where: { $0.id == folder.id }) {
            whole.tree.folders.append(folder)
        }
        for note in recovery.tree.notes where !whole.tree.notes.contains(where: { $0.id == note.id }) {
            whole.tree.notes.append(note)
        }
        for sketch in recovery.sketches where !whole.sketches.contains(where: { $0.id == sketch.id }) {
            whole.sketches.append(sketch)
        }
        return whole
    }

    /// The rows of `old` that `current` no longer has, added to what was already set aside.
    /// Nil where nothing has gone.
    static func settingAside(_ old: OfflineBase, gone current: OfflineBase, into kept: OfflineBase?) -> OfflineBase? {
        guard old.tree.notes.contains(where: { note in !current.tree.notes.contains { $0.id == note.id } }) else { return kept }
        return restoring(kept ?? old, old)
    }

    static func zip(_ files: [String: Data]) throws -> Data {
        let archive = try Archive(data: Data(), accessMode: .create)
        // A zip's times have no zone and are read as local ones, and the library writes them
        // as UTC's: the time is moved by the difference, or west of Greenwich every file in
        // the archive is from later today.
        let now = Date().addingTimeInterval(TimeInterval(TimeZone.current.secondsFromGMT()))
        // In a settled order: a folder's files together, wherever the archive is listed.
        for path in files.keys.sorted() {
            guard let data = files[path] else { continue }
            try archive.addEntry(with: path, type: .file, uncompressedSize: Int64(data.count), modificationDate: now,
                                 compressionMethod: .deflate) { position, size in
                data.subdata(in: Int(position)..<(Int(position) + size))
            }
        }
        guard let data = archive.data else { throw OfflineRefusal("The archive couldn't be made.") }
        return data
    }
}
