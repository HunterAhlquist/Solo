import PhotosUI
import SwiftUI

// A note's pictures, as the web editor shows them: a row of embeds is a small
// stack of prints, slightly fanned, and tapping it opens them full screen to
// swipe through, edit, replace or remove. The reader and the live editor both
// draw their rows with this, so a note looks the same read or being written.

/// An image or a sketch a note embeds.
enum NoteEmbed: Hashable {
    case image(DocImage)
    case sketch(DocSketch)

    var id: UUID {
        switch self {
        case .image(let image): image.id
        case .sketch(let sketch): sketch.id
        }
    }

    var path: String {
        switch self {
        case .image(let image): image.path
        case .sketch(let sketch): sketch.path
        }
    }

    var name: String { path.split(separator: "/").last.map(String.init) ?? path }

    var isSketch: Bool {
        if case .sketch = self { true } else { false }
    }

    /// The note it was made in, which its path records.
    var ownerId: UUID? {
        switch self {
        case .image(let image): DocMarkdown.owner(ofPath: image.path)
        case .sketch(let sketch): sketch.noteId
        }
    }
}

/// One embed of a row: what the note wrote, and what it resolved to.
struct NoteMediaEntry: Identifiable, Hashable {
    var id: String
    var ref: DocEmbedRef
    var embed: NoteEmbed
}

/// What a row of pictures needs from the note it is in.
struct NoteMediaContext {
    var store: DocVaultStore
    var note: DocNote
    /// Whether the note and its vault can be changed right now.
    var canWrite: Bool
    /// Takes embeds — each the exact text the note wrote — out of the note.
    var removeEmbeds: ([String]) -> Void
    var openNote: (UUID) -> Void

    func resolve(_ target: String) -> NoteEmbed? {
        let path = DocMarkdown.target(target)
        if path.lowercased().hasSuffix(".sketch") {
            return store.resolveSketch(path, from: note.id).map(NoteEmbed.sketch)
        }
        return store.resolveImage(path, from: note).map(NoteEmbed.image)
    }

    /// A row's embeds resolved, or nil when any of them isn't a picture this vault holds.
    func entries(_ refs: [DocEmbedRef]) -> [NoteMediaEntry]? {
        var result: [NoteMediaEntry] = []
        for (index, ref) in refs.enumerated() {
            guard let embed = resolve(ref.target) else { return nil }
            result.append(NoteMediaEntry(id: "\(index):\(embed.id.compact)", ref: ref, embed: embed))
        }
        return result.isEmpty ? nil : result
    }
}

/// An embed's picture, loaded: the stored image, or the sketch's page.
private struct NoteMediaPicture<Content: View>: View {
    @Environment(\.colorScheme) private var colorScheme
    var embed: NoteEmbed
    var store: DocVaultStore
    var onLoad: (UIImage) -> Void = { _ in }
    @ViewBuilder var content: (UIImage?, Bool) -> Content

    @State private var picture: UIImage?
    @State private var failed = false

    /// The embed as the vault has it now: a replaced image has a new version, a redrawn sketch a new revision.
    private var current: NoteEmbed {
        switch embed {
        case .image(let image): store.images.first { $0.id == image.id }.map(NoteEmbed.image) ?? embed
        case .sketch(let sketch): store.sketches.first { $0.id == sketch.id }.map(NoteEmbed.sketch) ?? embed
        }
    }

    private var key: String {
        switch current {
        case .image(let image): "\(image.id.compact):\(image.version)"
        case .sketch(let sketch): "\(sketch.id.compact):\(sketch.version):\(store.sketchRevision):\(colorScheme == .dark)"
        }
    }

    var body: some View {
        content(picture, failed)
            .task(id: key) {
                let loaded: UIImage? = switch current {
                case .image(let image): await store.picture(image)
                case .sketch(let sketch): await store.sketchPicture(sketch, dark: colorScheme == .dark)
                }
                if let loaded {
                    picture = loaded
                    onLoad(loaded)
                }
                failed = loaded == nil
            }
    }
}

/// A row of a note's pictures: one print, or several fanned into a stack.
struct NoteMediaStack: View {
    @Environment(\.theme) private var theme
    var entries: [NoteMediaEntry]
    var context: NoteMediaContext

    @Namespace private var zoom
    @State private var viewing: Start?
    /// The print last looked at, which stays on top of the stack.
    @State private var front: String?

    private struct Start: Identifiable {
        var id: String
    }

    static let side: CGFloat = 128
    private static let step: CGFloat = 24

    /// The room a row of `count` prints takes, as the web editor lays it out.
    static func size(count: Int) -> CGSize {
        CGSize(width: 148 + CGFloat(min(max(count, 1) - 1, 7)) * step, height: 164)
    }

    /// The same few degrees of tilt the web gives a print, worked out from its path so it never changes.
    static func angle(_ path: String) -> Double {
        var seed: Int32 = 0
        // Per character, by its first UTF-16 unit, which is how the web's loop counts.
        for scalar in path.unicodeScalars {
            let unit = scalar.value > 0xFFFF ? UInt32(UTF16.leadSurrogate(scalar)) : scalar.value
            seed = seed &* 31 &+ Int32(unit)
        }
        return Double(abs(Int(seed)) % 701) / 100 - 3.5
    }

    var body: some View {
        ZStack(alignment: .topLeading) {
            ForEach(Array(entries.enumerated()), id: \.element.id) { index, entry in
                Button {
                    viewing = Start(id: entry.id)
                } label: {
                    NoteMediaPicture(embed: entry.embed, store: context.store) { picture, failed in
                        ZStack {
                            if let picture {
                                Image(uiImage: picture)
                                    .interpolation(entry.embed.isSketch ? .none : .medium)
                                    .resizable()
                                    .scaledToFit()
                                    .clipShape(RoundedRectangle(cornerRadius: 5, style: .continuous))
                                    .shadow(color: .black.opacity(0.22), radius: 4, x: 1, y: 2)
                            } else {
                                RoundedRectangle(cornerRadius: 5, style: .continuous)
                                    .fill(.regularMaterial)
                                    .overlay {
                                        if failed {
                                            Image(systemName: "photo.badge.exclamationmark").foregroundStyle(theme.textSecondary)
                                        } else {
                                            ProgressView()
                                        }
                                    }
                                    .shadow(color: .black.opacity(0.15), radius: 4, x: 1, y: 2)
                            }
                        }
                        .frame(width: Self.side, height: Self.side)
                    }
                    .matchedTransitionSource(id: entry.id, in: zoom)
                }
                .buttonStyle(.plain)
                .rotationEffect(.degrees(Self.angle(entry.embed.path) * (entries.count > 1 ? 2 : 1)))
                .offset(x: 10 + CGFloat(min(index, 7)) * Self.step, y: 12 + CGFloat(index % 2) * 6)
                .zIndex(entry.id == front ? Double(entries.count + 1) : Double(index))
                .accessibilityLabel("\(entry.embed.isSketch ? "Sketch" : "Image") \(entry.embed.name)")
                .accessibilityHint("Opens it full screen")
            }
            if entries.count > 1 {
                Text("\(entries.count)")
                    .font(.caption.weight(.semibold).monospacedDigit())
                    .padding(.horizontal, 9)
                    .padding(.vertical, 4)
                    .foregroundStyle(theme.primary)
                    .glassEffect(.regular, in: .capsule)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomTrailing)
                    .zIndex(100)
                    .allowsHitTesting(false)
            }
        }
        .frame(width: Self.size(count: entries.count).width, height: Self.size(count: entries.count).height, alignment: .topLeading)
        .fullScreenCover(item: $viewing) { start in
            NoteMediaViewer(entries: entries, context: context, current: start.id, zoom: zoom) { front = $0 }
        }
    }
}

/// A row's pictures, full screen: swipe between them, and act on the one in view.
struct NoteMediaViewer: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State var entries: [NoteMediaEntry]
    var context: NoteMediaContext
    @State var current: String
    var zoom: Namespace.ID
    /// Told which print is in view, so the stack keeps it on top.
    var onSelect: (String) -> Void

    @State private var pictures: [String: UIImage] = [:]
    @State private var editing: SketchModel?
    @State private var replacing = false
    @State private var photo: PhotosPickerItem?
    @State private var removing = false
    @State private var busy = false

    private var entry: NoteMediaEntry? { entries.first { $0.id == current } ?? entries.first }

    private var owner: DocNote? {
        guard let id = entry?.embed.ownerId, id != context.note.id else { return nil }
        return context.store.note(id)
    }

    var body: some View {
        NavigationStack {
            TabView(selection: $current) {
                ForEach(entries) { entry in
                    NoteMediaPage(embed: entry.embed, store: context.store) { pictures[entry.id] = $0 }
                        .tag(entry.id)
                }
            }
            .tabViewStyle(.page(indexDisplayMode: .never))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Close", systemImage: "xmark") { dismiss() }
                }
                ToolbarItem(placement: .principal) {
                    VStack(spacing: 0) {
                        Text(entry?.embed.name ?? "").font(.headline).lineLimit(1)
                        if entries.count > 1, let index = entries.firstIndex(where: { $0.id == current }) {
                            Text("\(index + 1) of \(entries.count)").font(.caption2).foregroundStyle(theme.textSecondary)
                        }
                    }
                }
                ToolbarItemGroup(placement: .bottomBar) { actions }
            }
        }
        // The page it rose from is gone while this is up, so there is nothing for a material to
        // blur: the app's own background stands in, which keeps the theme's wash behind the picture.
        .presentationBackground { AppBackground() }
        .navigationTransition(.zoom(sourceID: current, in: zoom))
        .onChange(of: current, initial: true) { _, id in onSelect(id) }
        .photosPicker(isPresented: $replacing, selection: $photo, matching: .images)
        .onChange(of: photo) { _, item in
            guard let item else { return }
            photo = nil
            Task { await replace(with: item) }
        }
        .fullScreenCover(item: $editing, onDismiss: { context.store.sketchChanged() }) { SketchEditor(model: $0) }
    }

    @ViewBuilder
    private var actions: some View {
        if let entry {
            if case .sketch(let sketch) = entry.embed {
                Button(context.canWrite ? "Edit Sketch" : "Open Sketch", systemImage: "pencil.tip.crop.circle") {
                    editing = SketchModel(session: session, projectId: context.store.projectId, vaultId: context.store.vaultId,
                                          sketch: sketch, mayWrite: context.canWrite)
                }
            } else if context.canWrite {
                Button("Replace Image", systemImage: "photo.badge.arrow.down") { replacing = true }
                    .disabled(busy)
            }
            if let picture = pictures[entry.id] {
                let name = entry.embed.isSketch ? entry.embed.name.replacingOccurrences(of: ".sketch", with: ".png") : entry.embed.name
                ShareLink(item: Image(uiImage: picture), preview: SharePreview(name, image: Image(uiImage: picture)))
            }
            if let owner {
                Button("Open \u{201C}\(owner.name)\u{201D}", systemImage: "arrow.up.forward.square") {
                    dismiss()
                    context.openNote(owner.id)
                }
            }
            Spacer()
            if busy {
                ProgressView()
            }
            if context.canWrite {
                Button("Remove", systemImage: "trash", role: .destructive) { removing = true }
                    .disabled(busy)
                    .confirmationDialog("What do you want to do with \u{201C}\(entry.embed.name)\u{201D}?", isPresented: $removing,
                                        titleVisibility: .visible) {
                        Button("Remove from Note") { remove(deleting: false) }
                        Button("Delete Permanently", role: .destructive) { remove(deleting: true) }
                        Button("Cancel", role: .cancel) {}
                    } message: {
                        let kind = entry.embed.isSketch ? "sketch" : "image"
                        Text("Removing takes the embed out of this note; the \(kind) stays in the design doc. "
                            + "Deleting also deletes the \(kind), and anywhere else it is embedded shows it as unavailable.")
                    }
            }
        }
    }

    private func remove(deleting: Bool) {
        guard let entry else { return }
        Task {
            if deleting {
                busy = true
                defer { busy = false }
                // Asked for afresh: the version the vault has now is the one a delete has to name.
                await context.store.load()
                let done = await session.perform {
                    switch entry.embed {
                    case .image(let image):
                        guard let current = context.store.images.first(where: { $0.id == image.id }) else { return }
                        try await session.api.deleteDocImage(context.store.projectId, context.store.vaultId, current)
                    case .sketch(let sketch):
                        guard let current = context.store.sketches.first(where: { $0.id == sketch.id }) else { return }
                        try await session.api.deleteDocSketch(context.store.projectId, context.store.vaultId, current)
                    }
                }
                guard done else { return }
            }
            let gone = deleting ? entries.filter { $0.embed.id == entry.embed.id } : [entry]
            context.removeEmbeds(gone.map(\.ref.source))
            if deleting { await context.store.load() }
            let index = entries.firstIndex { $0.id == entry.id } ?? 0
            entries.removeAll { item in gone.contains { $0.id == item.id } }
            guard !entries.isEmpty else {
                dismiss()
                return
            }
            current = entries[min(index, entries.count - 1)].id
        }
    }

    private func replace(with item: PhotosPickerItem) async {
        guard case .image(let image) = entry?.embed else { return }
        busy = true
        defer { busy = false }
        guard let data = try? await item.loadTransferable(type: Data.self), let encoded = DocImageEncoder.jpeg(from: data) else {
            session.toasts.show("That image couldn't be read.", .warning)
            return
        }
        await context.store.load()
        guard let stored = context.store.images.first(where: { $0.id == image.id }) else { return }
        await session.perform {
            _ = try await session.api.replaceDocImage(context.store.projectId, context.store.vaultId, stored, data: encoded,
                                                      contentType: "image/jpeg")
        }
        await context.store.load()
    }
}

/// One picture in the viewer, as large as it fits, with pinch and double tap to look closer.
private struct NoteMediaPage: View {
    @Environment(\.theme) private var theme
    var embed: NoteEmbed
    var store: DocVaultStore
    var onLoad: (UIImage) -> Void

    @State private var scale: CGFloat = 1
    @GestureState private var pinch: CGFloat = 1

    var body: some View {
        NoteMediaPicture(embed: embed, store: store, onLoad: onLoad) { picture, failed in
            GeometryReader { proxy in
                ZStack {
                    if let picture {
                        ScrollView([.horizontal, .vertical]) {
                            Image(uiImage: picture)
                                .interpolation(embed.isSketch ? .none : .high)
                                .resizable()
                                .scaledToFit()
                                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                                .shadow(color: .black.opacity(0.25), radius: 16, y: 6)
                                .padding(24)
                                .frame(width: proxy.size.width * scale * pinch, height: proxy.size.height * scale * pinch)
                        }
                        .scrollIndicators(.hidden)
                        .scrollDisabled(scale * pinch <= 1.01)
                        .simultaneousGesture(MagnifyGesture().updating($pinch) { value, state, _ in state = value.magnification }
                            .onEnded { scale = min(max(scale * $0.magnification, 1), 6) })
                        .onTapGesture(count: 2) { withAnimation(.snappy) { scale = scale > 1 ? 1 : 2.5 } }
                    } else if failed {
                        ContentUnavailableView("This attachment couldn't load", systemImage: "photo.badge.exclamationmark")
                    } else {
                        ProgressView().controlSize(.large)
                    }
                }
                .frame(width: proxy.size.width, height: proxy.size.height)
            }
        }
    }
}

/// Makes a photo something the instance takes: PNG, JPEG or WebP, no larger than 2,560 pixels a side.
enum DocImageEncoder {
    static func jpeg(from data: Data) -> Data? {
        guard let picture = UIImage(data: data) else { return nil }
        let longest = max(picture.size.width, picture.size.height) * picture.scale
        let scale = min(1, 2560 / max(longest, 1))
        let size = CGSize(width: max(1, (picture.size.width * picture.scale * scale).rounded(.down)),
                          height: max(1, (picture.size.height * picture.scale * scale).rounded(.down)))
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        // Redrawn rather than re-encoded, which also settles a photo's orientation.
        return UIGraphicsImageRenderer(size: size, format: format).image { _ in
            picture.draw(in: CGRect(origin: .zero, size: size))
        }
        .jpegData(compressionQuality: 0.85)
    }
}
