import MarkdownUI
import SwiftUI

/// Rewrites what a note adds to Markdown — `[[wiki links]]`, `![[embeds]]`,
/// `==highlights==` — into Markdown the renderer knows, with each link and
/// embed already resolved to the note, image or sketch it means.
enum DocRenderer {
    static let scheme = "solo-doc"

    nonisolated(unsafe) private static let blockEmbed = /(?m)^[ \t]*!\[\[([^\]]+)\]\][ \t]*$/
    nonisolated(unsafe) private static let inlineEmbed = /!\[\[([^\]]+)\]\]/
    nonisolated(unsafe) private static let image = /!\[([^\]]*)\]\((?:<([^>]+)>|([^\s)]+))(?:\s+"[^"]*")?\)/
    nonisolated(unsafe) private static let wiki = /\[\[([^\]|#]*)(?:#([^\]|]*))?(?:\|([^\]]*))?\]\]/
    nonisolated(unsafe) private static let highlight = /==([^=\n]+)==/

    static func prepare(_ markdown: String, note: DocNote, store: DocVaultStore) -> String {
        DocMarkdown.outsideCode(markdown) { text in
            var text = text
            // An embed on a line of its own becomes a block of its own, which is what lets a sketch be tapped.
            text = text.replacing(blockEmbed) { "\n" + embed(String($0.output.1), note: note, store: store) + "\n" }
            text = text.replacing(inlineEmbed) { embed(String($0.output.1), note: note, store: store) }
            text = text.replacing(image) { match in
                let target = String(match.output.2 ?? match.output.3 ?? "")
                guard !target.contains("://"), !target.hasPrefix("/"),
                      let found = store.resolveImage(DocMarkdown.target(target), from: note) else { return String(match.output.0) }
                return "![\(match.output.1)](\(scheme)://image/\(found.id.api))"
            }
            text = text.replacing(wiki) { match in
                link(target: String(match.output.1), heading: match.output.2.map(String.init), alias: match.output.3.map(String.init),
                     note: note, store: store)
            }
            return text.replacing(highlight) { "**\($0.output.1)**" }
        }
    }

    private static func embed(_ raw: String, note: DocNote, store: DocVaultStore) -> String {
        let target = DocMarkdown.target(raw)
        let name = escape(target.split(separator: "/").last.map(String.init) ?? target)
        if target.lowercased().hasSuffix(".sketch") {
            if let sketch = store.resolveSketch(target, from: note.id) {
                return "![\(name)](\(scheme)://sketch/\(sketch.id.api))"
            }
        } else if let image = store.resolveImage(target, from: note) {
            return "![\(name)](\(scheme)://image/\(image.id.api))"
        }
        return "![\(name)](\(scheme)://missing/\(target.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? ""))"
    }

    private static func link(target: String, heading: String?, alias: String?, note: DocNote, store: DocVaultStore) -> String {
        let name = target.split(separator: "/").last.map(String.init) ?? target
        let label = escape(alias?.nilIfBlank ?? (target.isEmpty ? heading ?? "" : heading.map { "\(name) › \($0)" } ?? name))
        guard let found = store.resolveNote(target, from: note) else {
            return "[\(label)](\(scheme)://unresolved/)"
        }
        let slug = heading.flatMap { wanted in
            found.headings.first { $0.text.caseInsensitiveCompare(wanted) == .orderedSame || $0.slug == wanted }?.slug
        }
        let fragment = slug.flatMap { $0.addingPercentEncoding(withAllowedCharacters: .urlFragmentAllowed) }.map { "#\($0)" } ?? ""
        return "[\(label)](\(scheme)://note/\(NoteAddress.encode(found.id))\(fragment))"
    }

    private static func escape(_ text: String) -> String {
        text.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "[", with: "\\[")
            .replacingOccurrences(of: "]", with: "\\]")
    }
}

/// Draws a note's embeds: an image from the vault, a sketch, or a line saying the file is gone.
struct DocEmbedProvider: ImageProvider {
    var store: DocVaultStore
    var mayWrite: Bool

    func makeImage(url: URL?) -> some View {
        DocEmbed(url: url, store: store, mayWrite: mayWrite)
    }
}

/// Embeds that sit inside a line of text, where only a picture can go.
struct DocInlineEmbedProvider: InlineImageProvider {
    var store: DocVaultStore

    func image(with url: URL, label: String) async throws -> Image {
        if url.scheme == DocRenderer.scheme {
            if url.host() == "image", let id = UUID(uuidString: url.lastPathComponent),
               let image = store.images.first(where: { $0.id == id }), let picture = await store.picture(image) {
                return Image(uiImage: picture).resizable()
            }
            return Image(systemName: url.host() == "sketch" ? "scribble.variable" : "photo.badge.exclamationmark")
        }
        return try await DefaultInlineImageProvider.default.image(with: url, label: label)
    }
}

private struct DocEmbed: View {
    @Environment(\.theme) private var theme
    var url: URL?
    var store: DocVaultStore
    var mayWrite: Bool

    var body: some View {
        if let url, url.scheme == DocRenderer.scheme {
            let id = UUID(uuidString: url.lastPathComponent)
            if url.host() == "image", let image = store.images.first(where: { $0.id == id }) {
                DocPicture(image: image, store: store)
            } else if url.host() == "sketch", let sketch = store.sketches.first(where: { $0.id == id }) {
                SketchPreview(projectId: store.projectId, vaultId: store.vaultId, sketch: sketch, mayWrite: mayWrite,
                              revision: store.sketchRevision)
            } else {
                Label("\(url.lastPathComponent.removingPercentEncoding ?? "Attachment") isn't in this design doc",
                      systemImage: "photo.badge.exclamationmark")
                    .font(.footnote)
                    .foregroundStyle(theme.textSecondary)
                    .padding(10)
                    .background(theme.surface, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            }
        } else {
            DefaultImageProvider.default.makeImage(url: url)
        }
    }
}

/// An image stored in the vault. Tapping it opens it full screen.
struct DocPicture: View {
    @Environment(\.theme) private var theme
    var image: DocImage
    var store: DocVaultStore
    var maxHeight: CGFloat = 520

    @State private var picture: UIImage?
    @State private var failed = false
    @State private var viewing = false

    var body: some View {
        Group {
            if let picture {
                Button { viewing = true } label: {
                    Image(uiImage: picture)
                        .resizable()
                        .scaledToFit()
                        .frame(maxWidth: min(CGFloat(max(image.width, 1)), 760), maxHeight: maxHeight)
                        .clipShape(RoundedRectangle(cornerRadius: 10, style: .continuous))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Image \(image.path.split(separator: "/").last.map(String.init) ?? "")")
            } else {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(theme.surface)
                    .aspectRatio(CGFloat(max(image.width, 1)) / CGFloat(max(image.height, 1)), contentMode: .fit)
                    .frame(maxWidth: min(CGFloat(max(image.width, 1)), 760), maxHeight: maxHeight)
                    .overlay {
                        if failed {
                            Image(systemName: "photo.badge.exclamationmark").foregroundStyle(theme.textSecondary)
                        } else {
                            ProgressView()
                        }
                    }
            }
        }
        .task(id: "\(image.id.compact):\(image.version)") {
            picture = await store.picture(image)
            failed = picture == nil
        }
        .fullScreenCover(isPresented: $viewing) {
            if let picture {
                DocPictureViewer(picture: picture, name: image.path.split(separator: "/").last.map(String.init) ?? "Image")
            }
        }
    }
}

/// One image, as large as the screen allows, with pinch to look closer.
struct DocPictureViewer: View {
    @Environment(\.dismiss) private var dismiss
    var picture: UIImage
    var name: String

    @State private var zoom: CGFloat = 1
    @GestureState private var pinch: CGFloat = 1

    var body: some View {
        NavigationStack {
            GeometryReader { proxy in
                ScrollView([.horizontal, .vertical]) {
                    Image(uiImage: picture)
                        .resizable()
                        .scaledToFit()
                        .frame(width: proxy.size.width * zoom * pinch, height: proxy.size.height * zoom * pinch)
                }
                .scrollIndicators(.hidden)
                .gesture(MagnifyGesture().updating($pinch) { value, state, _ in state = value.magnification }
                    .onEnded { zoom = min(max(zoom * $0.magnification, 1), 6) })
                .onTapGesture(count: 2) { withAnimation(.snappy) { zoom = zoom > 1 ? 1 : 2.5 } }
            }
            .background(.black)
            .navigationTitle(name)
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close", systemImage: "xmark") { dismiss() }
                }
                ToolbarItem(placement: .primaryAction) {
                    ShareLink(item: Image(uiImage: picture), preview: SharePreview(name, image: Image(uiImage: picture)))
                }
            }
        }
    }
}

/// A note as it reads: rendered, live, and cut at its headings so a link or
/// the outline can jump to one.
struct NoteReader: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var text: String
    var note: DocNote
    var store: DocVaultStore
    var mayWrite: Bool
    /// The heading to bring into view; set by the outline and by links within the note.
    @Binding var anchor: String?
    var onOpenNote: (UUID) -> Void

    var body: some View {
        let sections = DocMarkdown.sections(text)
        LazyVStack(alignment: .leading, spacing: 14) {
            ForEach(Array(sections.enumerated()), id: \.offset) { _, section in
                SoloMarkdown(text: DocRenderer.prepare(section.text, note: note, store: store), linkHandler: follow)
                    .markdownImageProvider(DocEmbedProvider(store: store, mayWrite: mayWrite))
                    .markdownInlineImageProvider(DocInlineEmbedProvider(store: store))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .id(section.slug ?? "solo-doc-top")
            }
        }
    }

    private func follow(_ url: URL) -> Bool {
        if url.scheme == DocRenderer.scheme {
            if url.host() == "note", let id = NoteAddress.decode(url.lastPathComponent) {
                if id == note.id {
                    anchor = url.fragment(percentEncoded: false)
                } else {
                    onOpenNote(id)
                }
            } else {
                session.toasts.show("That note doesn't exist in this design doc.", .info)
            }
            return true
        }
        // A note's address, written out: stay in this design doc when it is one of its own.
        guard url.host() == session.api.baseURL.host(), let address = NoteAddress.read(url.path()), store.note(address.id) != nil
        else { return false }
        if address.id == note.id {
            anchor = url.fragment(percentEncoded: false)
        } else {
            onOpenNote(address.id)
        }
        return true
    }
}
