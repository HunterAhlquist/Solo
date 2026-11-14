import Foundation

// The Design Docs extension's wire types and endpoints (api/ext/design-docs/…),
// and the rules both ends have to agree on: how a note is addressed in a
// link, what counts as an embed, and how a heading becomes a slug.

nonisolated struct DocVault: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var name: String
    var isClosed: Bool
    var closedAt: Date?
    var version: UInt32
}

nonisolated struct DocVaultList: Codable, Sendable {
    /// Creating, renaming, closing and deleting a vault: a super admin who owns the project.
    var canManage: Bool
    var vaults: [DocVault]
}

nonisolated struct DocFolder: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var parentId: UUID?
    var name: String
    var version: UInt32
}

nonisolated struct DocHeading: Codable, Hashable, Sendable {
    var slug: String
    var text: String
    var level: Int
}

/// A note as the tree lists it. `markdown` is the last saved copy; the live
/// text is the note's shared document (see DocSync).
nonisolated struct DocNote: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var vaultId: UUID
    var folderId: UUID?
    var name: String
    var markdown: String
    var seq: String
    var headings: [DocHeading]
    var lastEditedBy: UUID?
    var lastEditedAt: Date?
    var version: UInt32
}

nonisolated struct DocTree: Codable, Sendable {
    var vault: DocVault
    var folders: [DocFolder]
    var notes: [DocNote]
}

nonisolated struct DocLocation: Codable, Sendable {
    var projectId: UUID
    var projectAlias: String
    var vaultId: UUID
    var noteId: UUID
}

nonisolated struct DocImage: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var path: String
    var contentType: String
    var width: Int
    var height: Int
    var bytes: Int
    var version: UInt32
}

nonisolated struct DocSketch: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var noteId: UUID
    var name: String
    var pageSize: String
    var orientation: String
    var pattern: String
    var lastEditedBy: UUID?
    var lastEditedAt: Date?
    var version: UInt32
    var path: String
}

/// Every image and sketch in a vault, and the raw embed targets in each note's saved Markdown.
nonisolated struct DocAttachments: Codable, Sendable {
    var images: [DocImage]
    var sketches: [DocSketch]
    var embeds: [UUID: [String]]

    private enum CodingKeys: String, CodingKey { case images, sketches, embeds }

    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        images = try container.decode([DocImage].self, forKey: .images)
        sketches = try container.decode([DocSketch].self, forKey: .sketches)
        // Keyed by note id, which JSON can only spell as a string.
        let raw = try container.decodeIfPresent([String: [String]].self, forKey: .embeds) ?? [:]
        embeds = Dictionary(uniqueKeysWithValues: raw.compactMap { key, value in UUID(uuidString: key).map { ($0, value) } })
    }
}

nonisolated private struct ItemChange: Encodable, Sendable {
    var name: String
    var parentId: UUID?
    var version: UInt32
}

nonisolated private struct VaultChange: Encodable, Sendable {
    var name: String
    var isClosed: Bool
    var version: UInt32
}

nonisolated private struct SketchChange: Encodable, Sendable {
    var name: String
    var pageSize: String
    var orientation: String
    var pattern: String
    var version: UInt32
}

nonisolated private struct CopyRequest: Encodable, Sendable {
    var markdown: String
    var headings: [DocHeading]
    var seq: String
}

nonisolated enum DocTopics {
    static let vaults = "design-docs:vaults"
    static func vault(_ id: UUID) -> String { "design-docs:vault:\(id.compact)" }
    static func doc(_ id: UUID) -> String { "design-docs:doc:\(id.compact)" }
}

extension UUID {
    /// .NET's "N" format: 32 hex digits, no hyphens.
    nonisolated var compact: String { uuidString.lowercased().replacingOccurrences(of: "-", with: "") }
}

/// Which kind of thing a tree operation is about.
nonisolated enum DocItemKind: String, Sendable {
    case folder = "folders"
    case note = "notes"
}

extension APIClient {
    private func docs(_ projectId: UUID) -> String { "api/ext/design-docs/projects/\(projectId.api)" }

    func docVaults(_ projectId: UUID) async throws -> DocVaultList { try await get("\(docs(projectId))/vaults") }

    func createDocVault(_ projectId: UUID, name: String) async throws -> DocVault {
        try await send("POST", "\(docs(projectId))/vaults", body: ["name": name])
    }

    func changeDocVault(_ projectId: UUID, _ vault: DocVault, name: String, isClosed: Bool) async throws -> DocVault {
        try await send("PUT", "\(docs(projectId))/vaults/\(vault.id.api)",
                       body: VaultChange(name: name, isClosed: isClosed, version: vault.version))
    }

    func deleteDocVault(_ projectId: UUID, _ vault: DocVault) async throws {
        try await send("DELETE", "\(docs(projectId))/vaults/\(vault.id.api)?version=\(vault.version)")
    }

    func docTree(_ projectId: UUID, _ vaultId: UUID) async throws -> DocTree {
        try await get("\(docs(projectId))/vaults/\(vaultId.api)")
    }

    func docNote(_ projectId: UUID, _ vaultId: UUID, _ noteId: UUID) async throws -> DocNote {
        try await get("\(docs(projectId))/vaults/\(vaultId.api)/notes/\(noteId.api)")
    }

    func addDocFolder(_ projectId: UUID, _ vaultId: UUID, name: String, parentId: UUID?) async throws -> DocFolder {
        try await send("POST", "\(docs(projectId))/vaults/\(vaultId.api)/folders",
                       body: ItemChange(name: name, parentId: parentId, version: 0))
    }

    func addDocNote(_ projectId: UUID, _ vaultId: UUID, name: String, parentId: UUID?) async throws -> DocNote {
        try await send("POST", "\(docs(projectId))/vaults/\(vaultId.api)/notes",
                       body: ItemChange(name: name, parentId: parentId, version: 0))
    }

    /// Renames or moves a folder or a note; `parentId` nil is the vault root.
    func changeDocItem(_ projectId: UUID, _ vaultId: UUID, _ kind: DocItemKind, _ id: UUID, name: String, parentId: UUID?,
                       version: UInt32) async throws {
        try await send("PUT", "\(docs(projectId))/vaults/\(vaultId.api)/\(kind.rawValue)/\(id.api)",
                       body: ItemChange(name: name, parentId: parentId, version: version))
    }

    func deleteDocItem(_ projectId: UUID, _ vaultId: UUID, _ kind: DocItemKind, _ id: UUID, version: UInt32) async throws {
        try await send("DELETE", "\(docs(projectId))/vaults/\(vaultId.api)/\(kind.rawValue)/\(id.api)?version=\(version)")
    }

    /// The saved copy of a note's text: what the tree, links and attachment list read.
    func saveDocCopy(_ projectId: UUID, _ vaultId: UUID, _ noteId: UUID, markdown: String, seq: String) async throws {
        try await send("PUT", "\(docs(projectId))/vaults/\(vaultId.api)/notes/\(noteId.api)/copy",
                       body: CopyRequest(markdown: markdown, headings: DocMarkdown.headings(markdown), seq: seq))
    }

    func docAttachments(_ projectId: UUID, _ vaultId: UUID) async throws -> DocAttachments {
        try await get("\(docs(projectId))/vaults/\(vaultId.api)/attachments")
    }

    func docImageData(_ projectId: UUID, _ vaultId: UUID, _ imageId: UUID) async throws -> Data {
        try await bytes("\(docs(projectId))/vaults/\(vaultId.api)/images/\(imageId.api)").0
    }

    func uploadDocImage(_ projectId: UUID, _ vaultId: UUID, noteId: UUID, path: String, data: Data, contentType: String) async throws -> DocImage {
        var query = URLComponents()
        query.queryItems = [URLQueryItem(name: "path", value: path)]
        return try await upload("POST", "\(docs(projectId))/vaults/\(vaultId.api)/images/notes/\(noteId.api)?\(query.percentEncodedQuery ?? "")",
                                data: data, contentType: contentType)
    }

    /// Puts a new picture in an image's place; every embed of it shows the new one.
    func replaceDocImage(_ projectId: UUID, _ vaultId: UUID, _ image: DocImage, data: Data, contentType: String) async throws -> DocImage {
        try await upload("PUT", "\(docs(projectId))/vaults/\(vaultId.api)/images/\(image.id.api)?version=\(image.version)",
                         data: data, contentType: contentType)
    }

    func deleteDocImage(_ projectId: UUID, _ vaultId: UUID, _ image: DocImage) async throws {
        try await send("DELETE", "\(docs(projectId))/vaults/\(vaultId.api)/images/\(image.id.api)?version=\(image.version)")
    }

    func createDocSketch(_ projectId: UUID, _ vaultId: UUID, noteId: UUID, name: String) async throws -> DocSketch {
        try await send("POST", "\(docs(projectId))/vaults/\(vaultId.api)/sketches/notes/\(noteId.api)", body: ["name": name])
    }

    /// A sketch's name and paper. The page settings also live in the sketch itself; this is the instance's copy.
    func changeDocSketch(_ projectId: UUID, _ vaultId: UUID, _ sketch: DocSketch, name: String? = nil, pageSize: String,
                         orientation: String, pattern: String) async throws -> DocSketch {
        try await send("PUT", "\(docs(projectId))/vaults/\(vaultId.api)/sketches/\(sketch.id.api)",
                       body: SketchChange(name: name ?? sketch.name, pageSize: pageSize, orientation: orientation, pattern: pattern,
                                          version: sketch.version))
    }

    func deleteDocSketch(_ projectId: UUID, _ vaultId: UUID, _ sketch: DocSketch) async throws {
        try await send("DELETE", "\(docs(projectId))/vaults/\(vaultId.api)/sketches/\(sketch.id.api)?version=\(sketch.version)")
    }

    /// Where a note lives, from its id alone: what a link to it needs to become a place.
    func locateDocNote(_ noteId: UUID) async throws -> DocLocation {
        try await get("api/ext/design-docs/notes/\(noteId.api)/locate")
    }

    /// The current names of linked notes, by link address.
    func docLinkLabels(_ urls: [String]) async throws -> [String: String] {
        try await send("POST", "api/ext/design-docs/links/labels", body: ["urls": urls])
    }
}

/// A note's address in a link: `/docs/note/` and its id as 22 characters of
/// URL-safe base64, which survives a rename or a move.
nonisolated enum NoteAddress {
    static let prefix = "/docs/note/"
    nonisolated(unsafe) static let pattern = /\/docs\/note\/([0-9a-fA-F-]{32,36}|[A-Za-z0-9_-]{22})(?:#([\p{L}\p{N}_%-]+))?/

    static func encode(_ id: UUID) -> String {
        let bytes = withUnsafeBytes(of: id.uuid) { Data($0) }
        return bytes.base64EncodedString().replacingOccurrences(of: "=", with: "")
            .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
    }

    static func url(_ id: UUID, heading: String? = nil) -> String {
        prefix + encode(id) + (heading.map { "#\($0)" } ?? "")
    }

    static func decode(_ value: String) -> UUID? {
        if value.count == 36 || value.count == 32, let id = UUID(uuidString: value.count == 32 ? hyphenate(value) : value) {
            return id
        }
        guard value.count == 22,
              let data = Data(base64Encoded: value.replacingOccurrences(of: "-", with: "+")
                  .replacingOccurrences(of: "_", with: "/") + "=="), data.count == 16 else { return nil }
        return data.withUnsafeBytes { UUID(uuid: $0.loadUnaligned(as: uuid_t.self)) }
    }

    /// The note a link points at, and the heading within it, if it is one of ours.
    static func read(_ link: String) -> (id: UUID, heading: String?)? {
        guard let match = link.firstMatch(of: pattern), let id = decode(String(match.1)) else { return nil }
        return (id, match.2.map(String.init))
    }

    private static func hyphenate(_ hex: String) -> String {
        let c = Array(hex)
        return [c[0..<8], c[8..<12], c[12..<16], c[16..<20], c[20..<32]].map { String($0) }.joined(separator: "-")
    }
}

/// One embed as a note writes it: the whole of its syntax, and the target inside.
nonisolated struct DocEmbedRef: Hashable, Sendable {
    var source: String
    var target: String
}

nonisolated enum DocMarkdown {
    nonisolated(unsafe) private static let code = /(?m)^(```|~~~)[^\n]*\n[\s\S]*?(^\1[^\n]*$|\z)|`[^`\n]*`/
    nonisolated(unsafe) private static let embed = /!\[\[([^\]]+)\]\]|!\[[^\]]*\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/

    /// The raw targets of a note's image and sketch embeds, outside code.
    static func embedTargets(_ markdown: String) -> [String] {
        let blanked = markdown.replacing(code) { String(repeating: " ", count: $0.output.0.count) }
        return blanked.matches(of: embed).map { match in
            String(match.output.1 ?? match.output.2 ?? match.output.3 ?? "")
        }
    }

    /// The embeds on a line that holds nothing else — which is what makes it a
    /// row of pictures rather than text with pictures in it. Nil for any other line.
    static func mediaRow(_ line: String) -> [DocEmbedRef]? {
        let matches = line.matches(of: embed)
        guard !matches.isEmpty else { return nil }
        var rest = line
        for match in matches.reversed() { rest.removeSubrange(match.range) }
        guard rest.allSatisfy(\.isWhitespace) else { return nil }
        return matches.map { match in
            DocEmbedRef(source: String(match.output.0), target: String(match.output.1 ?? match.output.2 ?? match.output.3 ?? ""))
        }
    }

    /// A raw target as it resolves: the size or alias after `|` dropped, URL-decoded.
    static func target(_ raw: String) -> String {
        let path = String(raw.split(separator: "|", maxSplits: 1, omittingEmptySubsequences: false).first ?? "")
        return path.removingPercentEncoding ?? path
    }

    /// Where an attachment created in a note is stored.
    static func attachmentPath(noteId: UUID, name: String) -> String {
        "attachments/\(NoteAddress.encode(noteId))/\(name)"
    }

    /// The note an attachment was created in, read from its path. Imported files have none.
    static func owner(ofPath path: String) -> UUID? {
        let parts = path.split(separator: "/")
        guard parts.count > 2, parts[0] == "attachments" else { return nil }
        return NoteAddress.decode(String(parts[1]))
    }

    /// A note's headings and the slugs links use for them.
    static func headings(_ markdown: String) -> [DocHeading] {
        outline(markdown).map(\.heading)
    }

    /// Each heading with the line it is on. The slug rule is the web
    /// editor's: lower-cased, spaces to hyphens, anything that isn't a
    /// letter, digit, underscore or hyphen dropped, repeats numbered.
    static func outline(_ markdown: String) -> [(heading: DocHeading, line: Int)] {
        var used: [String: Int] = [:]
        var result: [(heading: DocHeading, line: Int)] = []
        var fence: Substring?
        for (index, line) in markdown.split(separator: "\n", omittingEmptySubsequences: false).enumerated() {
            let trimmed = line.drop { $0 == " " }
            if let open = fence {
                if trimmed.hasPrefix(open) { fence = nil }
                continue
            }
            if trimmed.hasPrefix("```") || trimmed.hasPrefix("~~~") {
                fence = trimmed.prefix(3)
                continue
            }
            guard line.count - trimmed.count <= 3, let match = trimmed.wholeMatch(of: /(#{1,6})\s+(.*?)(?:\s+#+\s*)?/) else { continue }
            var text = String(match.2).replacingOccurrences(of: "==", with: "")
            text = text.replacing(/\[([^\]]+)\]\([^)]+\)/) { String($0.output.1) }
            text = text.replacing(/[*_`]/, with: "").replacing(/\s+/, with: " ").trimmingCharacters(in: .whitespaces)
            let base = String(text.lowercased().replacing(/\s+/, with: "-").unicodeScalars.filter {
                CharacterSet.letters.contains($0) || CharacterSet.decimalDigits.contains($0) || $0 == "_" || $0 == "-"
            }.map(Character.init))
            let count = (used[base] ?? 0) + 1
            used[base] = count
            result.append((DocHeading(slug: count > 1 ? "\(base)-\(count)" : base, text: text, level: match.1.count), index))
        }
        return result
    }
}
