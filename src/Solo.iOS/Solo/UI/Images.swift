import Nuke
import NukeUI
import SwiftDraw
import SwiftUI

/// Image loading for the whole app: Nuke fetches and caches, and SVG — which
/// icons, logos and login backgrounds may all be — is taught to it once here.
nonisolated enum SoloImages {
    static func configure() {
        ImageDecoderRegistry.shared.register { context in
            SVGDecoder.matches(context) ? SVGDecoder() : nil
        }
    }
}

nonisolated private struct SVGDecoder: ImageDecoding {
    struct Unreadable: Error {}

    static func matches(_ context: ImageDecodingContext) -> Bool {
        if (context.urlResponse as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Type")?
            .contains("svg") == true {
            return true
        }
        // No header to go by (a cached entry): look for the root element.
        let head = String(decoding: context.data.prefix(512), as: UTF8.self)
        return head.contains("<svg")
    }

    func decode(_ data: Data) throws -> ImageContainer {
        guard let svg = SVG(data: data) else { throw Unreadable() }
        // Rasterized once at a size that stays sharp as an icon and bounded as
        // a full-screen background.
        let longest = max(svg.size.width, svg.size.height, 1)
        let factor = min(max(longest, 512), 2400) / longest
        let size = CGSize(width: svg.size.width * factor, height: svg.size.height * factor)
        return ImageContainer(image: svg.rasterize(size: size, scale: 1))
    }
}

/// A project's, board's or label's icon: its uploaded picture when it has one,
/// otherwise the curated symbol its stored name maps to.
struct EntityIcon: View {
    @Environment(Session.self) private var session
    var imageId: UUID?
    var icon: String?
    var size: CGFloat = 22
    var fallback = "folder.fill"

    var body: some View {
        if let imageId {
            LazyImage(url: session.api.imageURL(imageId)) { state in
                if let image = state.image {
                    image.resizable().aspectRatio(contentMode: .fill)
                } else {
                    Color.clear
                }
            }
            .frame(width: size, height: size)
            .clipShape(RoundedRectangle(cornerRadius: size * 0.22, style: .continuous))
        } else {
            Image(systemName: SoloIcons.symbol(icon, fallback: fallback))
                .font(.system(size: size * 0.82))
                .frame(width: size, height: size)
        }
    }
}

/// Icons are stored as free-text names. This maps a stored name to the system
/// symbol that says the same thing, and curates the shortlist the picker offers.
nonisolated enum SoloIcons {
    static let all: [(name: String, symbol: String)] = [
        ("Games", "gamecontroller.fill"),
        ("Rocket", "paperplane.fill"),
        ("Bug", "ladybug.fill"),
        ("Brush", "paintbrush.fill"),
        ("Palette", "paintpalette.fill"),
        ("Code", "chevron.left.forwardslash.chevron.right"),
        ("Terminal", "apple.terminal.fill"),
        ("Audio", "waveform"),
        ("Music", "music.note"),
        ("Movie", "film.fill"),
        ("Camera", "camera.fill"),
        ("Animation", "film.stack.fill"),
        ("Cube", "cube.fill"),
        ("Map", "map.fill"),
        ("Terrain", "mountain.2.fill"),
        ("Physics", "flask.fill"),
        ("Lightbulb", "lightbulb.fill"),
        ("Speed", "gauge.with.needle.fill"),
        ("Build", "wrench.and.screwdriver.fill"),
        ("Storage", "externaldrive.fill"),
        ("Cloud", "cloud.fill"),
        ("Shield", "shield.fill"),
        ("Flag", "flag.fill"),
        ("Star", "star.fill"),
        ("Bolt", "bolt.fill"),
        ("Fire", "flame.fill"),
        ("Puzzle", "puzzlepiece.extension.fill"),
        ("Dashboard", "rectangle.3.group.fill"),
        ("Book", "book.fill"),
        ("Chat", "bubble.left.and.bubble.right.fill"),
        ("People", "person.2.fill"),
        ("Calendar", "calendar"),
        ("Robot", "cpu.fill"),
    ]

    private static let byName = Dictionary(uniqueKeysWithValues: all.map { ($0.name.lowercased(), $0.symbol) })

    /// Falls back to a neutral symbol so an unknown stored name never draws a blank.
    static func symbol(_ name: String?, fallback: String = "folder.fill") -> String {
        name.flatMap { byName[$0.lowercased()] } ?? fallback
    }
}

/// A person: their picture through the instance's avatar proxy, or their
/// initials on a colour derived from their name — the same colour on every
/// client, without storing anything.
struct UserAvatar: View {
    @Environment(Session.self) private var session
    var userId: UUID
    var name: String?
    var hasPicture: Bool = false
    var size: CGFloat = 26

    private static let palette = ["#8d7ffa", "#4fd1c5", "#f6ad55", "#f56565", "#63b3ed", "#48bb78", "#ed64a6", "#ecc94b"]

    var body: some View {
        Group {
            if hasPicture {
                LazyImage(url: session.api.avatarURL(userId)) { state in
                    if let image = state.image {
                        image.resizable().aspectRatio(contentMode: .fill)
                    } else {
                        initials
                    }
                }
            } else {
                initials
            }
        }
        .frame(width: size, height: size)
        .clipShape(.circle)
        .accessibilityLabel(name ?? "Unknown user")
    }

    private var initials: some View {
        ZStack {
            Color(hex: Self.palette[colorIndex])
            Text(initialsText)
                .font(.system(size: size * 0.42, weight: .semibold, design: .rounded))
                .foregroundStyle(Color(hex: "#15151f"))
        }
    }

    /// FNV-1a over the name, as the web app computes it, so colours agree.
    private var colorIndex: Int {
        let text = name?.nilIfBlank ?? userId.api
        var hash: UInt32 = 2_166_136_261
        for unit in text.utf16 {
            hash = (hash ^ UInt32(unit)) &* 16_777_619
        }
        return Int(hash % UInt32(Self.palette.count))
    }

    private var initialsText: String {
        guard let name = name?.nilIfBlank else { return "?" }
        let parts = name.split(separator: " ")
        guard let first = parts.first?.first else { return "?" }
        if parts.count == 1 { return String(first).uppercased() }
        return "\(first)\(parts.last?.first.map(String.init) ?? "")".uppercased()
    }
}

extension UserAvatar {
    init(member: ProjectMember, size: CGFloat = 26) {
        self.init(userId: member.avatarId, name: member.name, hasPicture: member.avatarUrl?.nilIfBlank != nil, size: size)
    }

    init(user: User, size: CGFloat = 26) {
        self.init(userId: user.id, name: user.name, hasPicture: user.avatarUrl?.nilIfBlank != nil, size: size)
    }
}

/// Overlapping avatars, capped, with a count for the rest.
struct AvatarStack: View {
    @Environment(\.theme) private var theme
    var members: [ProjectMember]
    var max = 3
    var size: CGFloat = 24

    var body: some View {
        HStack(spacing: -size * 0.3) {
            ForEach(members.prefix(max)) { member in
                UserAvatar(member: member, size: size)
                    .overlay(Circle().stroke(theme.surface, lineWidth: 1.5))
            }
            if members.count > max {
                Text("+\(members.count - max)")
                    .font(.system(size: size * 0.4, weight: .semibold))
                    .frame(width: size, height: size)
                    .background(theme.backgroundGray, in: .circle)
                    .overlay(Circle().stroke(theme.surface, lineWidth: 1.5))
            }
        }
    }
}

/// Who else is on an entity: a plain avatar is viewing, a ringed and pulsing one is editing.
struct PresenceCluster: View {
    @Environment(\.theme) private var theme
    var entries: [PresenceEntry]
    var max = 3
    var size: CGFloat = 22

    var body: some View {
        if !entries.isEmpty {
            HStack(spacing: -size * 0.25) {
                ForEach(entries.prefix(max), id: \.self) { entry in
                    UserAvatar(userId: entry.userId, name: entry.displayName, size: size)
                        .saturation(entry.mode == .editing ? 1 : 0.35)
                        .overlay {
                            if entry.mode == .editing {
                                Circle().stroke(theme.warning, lineWidth: 2)
                                    .phaseAnimator([0.4, 1.0]) { ring, phase in ring.opacity(phase) } animation: { _ in
                                        .easeInOut(duration: 0.8)
                                    }
                            }
                        }
                        .accessibilityLabel(
                            "\(entry.displayName ?? "Someone") is \(entry.mode == .editing ? "editing" : "viewing") this")
                }
                if entries.count > max {
                    Text("+\(entries.count - max)")
                        .font(.system(size: size * 0.4, weight: .semibold))
                        .frame(width: size, height: size)
                        .background(theme.backgroundGray, in: .circle)
                }
            }
        }
    }
}
