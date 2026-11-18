import SwiftUI

// MARK: - Surfaces

/// A panel of content: Liquid Glass, so what is behind it still comes through.
/// Nothing in the app paints a solid block of colour under or over the glass.
struct CardBackground: ViewModifier {
    @Environment(\.theme) private var theme
    var padding: CGFloat = 12
    var outline: Color?
    var lineWidth: CGFloat = 1

    func body(content: Content) -> some View {
        content
            .padding(padding)
            .glassEffect(.regular, in: theme.cardShape)
            .overlay {
                if let outline {
                    theme.cardShape.strokeBorder(outline, lineWidth: lineWidth)
                }
            }
    }
}

/// The page behind everything: the system's own background, with the
/// instance's accent as a faint wash at the top. The wash is what the glass
/// above it picks up, and the only place the accent is painted large.
///
/// Over that goes the theme's picture, where it has one for behind the app. Every page, and
/// every sheet over a page, draws this view for itself at its own size, and the picture is
/// one picture for the window: each draws the part of it that it is standing in front of.
/// So it holds still while a page slides in or a panel opens beside one, and a sheet is a
/// further pane over the same picture rather than a smaller copy of it.
///
/// It is drawn at a quarter of its strength. Glass carries most of what is read here, but
/// not all: a section's heading, a page's title and an empty page's one line sit straight
/// on the page. At full strength they were lost in the picture, and at the web's half the
/// quieter of them still were — the web has its panels behind such lines more often.
struct AppBackground: View {
    @Environment(\.theme) private var theme
    @Environment(ThemeStore.self) private var themes: ThemeStore?
    @Environment(\.backdropCanvas) private var canvas

    /// How much of the picture shows: what `--solo-backdrop-strength` is on the web.
    private static let strength = 0.25

    var body: some View {
        ZStack {
            Color.groupedBackground
            LinearGradient(
                colors: [theme.primary.opacity(theme.isDark ? 0.16 : 0.12), theme.secondary.opacity(0.05), .clear],
                startPoint: .topLeading, endPoint: .bottom)
            RadialGradient(colors: [theme.primary.opacity(theme.isDark ? 0.10 : 0.07), .clear],
                           center: .bottomTrailing, startRadius: 0, endRadius: 520)
            if let background = themes?.document?.background, let picture = themes?.backdrop(dark: theme.isDark) {
                GeometryReader { proxy in
                    let own = proxy.frame(in: .global)
                    // Without a window to go by — a preview, a window of its own — it fills this.
                    let window = canvas.map { CGRect(origin: .zero, size: $0) } ?? own
                    FittedPicture(image: picture, fit: background.fit, position: background.position)
                        .frame(width: window.width, height: window.height)
                        .offset(x: window.minX - own.minX, y: window.minY - own.minY)
                        .frame(width: own.width, height: own.height, alignment: .topLeading)
                        .clipped()
                }
                .opacity(Self.strength)
                // What a theme asks to be laid over a picture too busy to read on.
                Color.groupedBackground.opacity(min(max(background.scrim ?? 0, 0), 1))
            }
        }
        .ignoresSafeArea()
        .accessibilityHidden(true)
    }
}

/// A theme's picture laid out in the room it is given by the theme's `fit` and `position`,
/// as the web lays it out with `background-size` and `background-position`.
struct FittedPicture: View {
    var image: Image
    var fit: String?
    var position: String?

    var body: some View {
        GeometryReader { proxy in
            fitted(in: proxy.size)
                .frame(width: proxy.size.width, height: proxy.size.height, alignment: alignment)
                .clipped()
        }
    }

    @ViewBuilder
    private func fitted(in size: CGSize) -> some View {
        switch fit?.lowercased() {
        case "contain":
            image.resizable().scaledToFit()
        case "width":
            image.resizable().aspectRatio(contentMode: .fit).frame(width: size.width)
        case "height":
            image.resizable().aspectRatio(contentMode: .fit).frame(height: size.height).fixedSize()
        case "stretch":
            image.resizable().frame(width: size.width, height: size.height)
        default:
            image.resizable().scaledToFill()
        }
    }

    private var alignment: Alignment {
        let value = position?.lowercased() ?? "center"
        let vertical: VerticalAlignment = value.contains("top") ? .top : value.contains("bottom") ? .bottom : .center
        let horizontal: HorizontalAlignment = value.contains("left") ? .leading : value.contains("right") ? .trailing : .center
        return Alignment(horizontal: horizontal, vertical: vertical)
    }
}

extension EnvironmentValues {
    /// The size of the window a view is in, where the app's root has measured it.
    @Entry var backdropCanvas: CGSize?
}

extension View {
    func card(padding: CGFloat = 12, outline: Color? = nil, lineWidth: CGFloat = 1) -> some View {
        modifier(CardBackground(padding: padding, outline: outline, lineWidth: lineWidth))
    }

    /// The app's page background, edge to edge.
    func themedBackground() -> some View {
        background { AppBackground() }
    }

    /// A grouped list or form over the app's page background; its rows stay the system's.
    func themedList() -> some View {
        #if os(macOS)
        // Grouped, as System Settings is: left to itself a Mac's form is two columns, which
        // these pages were not laid out for.
        formStyle(.grouped).macControls().scrollContentBackground(.hidden).background { AppBackground() }
        #else
        scrollContentBackground(.hidden).background { AppBackground() }
        #endif
    }

    /// A list row that lets the page's wash through, so a list reads as a
    /// frosted panel over the background rather than a solid slab on it.
    func themedRow() -> some View {
        listRowBackground(Color.groupedRowBackground.opacity(0.55))
    }
}

/// A small upper-case heading over a block of content.
struct SectionTitle: View {
    @Environment(\.theme) private var theme
    var title: String
    var count: Int?

    init(_ title: String, count: Int? = nil) {
        self.title = title
        self.count = count
    }

    var body: some View {
        HStack(spacing: 6) {
            Text(title.uppercased())
                .font(.caption.weight(.semibold))
                .kerning(0.6)
            if let count {
                Text("\(count)")
                    .font(.caption.weight(.semibold).monospacedDigit())
                    .padding(.horizontal, 6)
                    .padding(.vertical, 1)
                    .background(theme.backgroundGray, in: .capsule)
            }
        }
        .foregroundStyle(theme.textSecondary)
        .accessibilityAddTraits(.isHeader)
    }
}

/// A list of sections of rows, as Settings has. On an iPhone that is a list; on a Mac it is a
/// grouped form, which is what draws sections as panels there — a Mac's list is a plain table.
struct GroupedList<Content: View>: View {
    @ViewBuilder var content: Content

    var body: some View {
        #if os(macOS)
        Form { content }
        #else
        List { content }
        #endif
    }
}

/// A glass button whose label is in the accent. An iPhone's glass button is that already; a
/// Mac's takes the accent as its fill, which is the accent painted solid over glass.
#if os(macOS)
struct ClearGlassButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    @Environment(\.controlSize) private var size

    func makeBody(configuration: Configuration) -> some View {
        let large = size == .large || size == .extraLarge, small = size == .small || size == .mini
        configuration.label
            .foregroundStyle(.tint)
            .padding(.horizontal, large ? 14 : small ? 8 : 10)
            .padding(.vertical, large ? 8 : small ? 4 : 6)
            .glassEffect(.regular.interactive(), in: .capsule)
            .opacity(isEnabled ? (configuration.isPressed ? 0.6 : 1) : 0.45)
            .contentShape(.capsule)
    }
}

extension ButtonStyle where Self == ClearGlassButtonStyle {
    static var clearGlass: ClearGlassButtonStyle { ClearGlassButtonStyle() }
}
#else
extension PrimitiveButtonStyle where Self == GlassButtonStyle {
    static var clearGlass: GlassButtonStyle { .glass }
}
#endif

/// A label as an icon beside a name and nothing more, in whatever colour it is given. The
/// icon has the room a sidebar row gives its own (`SidebarRow`), so the two stand in line.
struct AsWrittenLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 10) {
            configuration.icon.frame(width: 24)
            configuration.title
        }
    }
}

// MARK: - Labels

struct LabelChip: View {
    @Environment(Session.self) private var session
    var label: SoloLabel
    var compact = false

    var body: some View {
        HStack(spacing: 4) {
            if label.imageId != nil || label.icon != nil {
                EntityIcon(imageId: label.imageId, icon: label.icon, size: compact ? 12 : 14, fallback: "tag.fill")
            }
            Text(label.name)
                .font(compact ? .caption2.weight(.semibold) : .caption.weight(.semibold))
                .lineLimit(1)
        }
        .padding(.horizontal, compact ? 6 : 8)
        .padding(.vertical, compact ? 2 : 4)
        .foregroundStyle(Color.readable(on: label.color))
        .background(Color(hex: label.color), in: .capsule)
        .accessibilityLabel("Label: \(label.name)")
    }
}

// MARK: - Warnings & states

/// Before you type a character, you see that someone else is already in here.
struct EditingWarning: View {
    @Environment(\.theme) private var theme
    var editors: [PresenceEntry]

    var body: some View {
        if !editors.isEmpty {
            Label("\(names) is editing this right now — your save may overwrite theirs.", systemImage: "pencil.line")
                .font(.footnote)
                .foregroundStyle(theme.textPrimary)
                .padding(10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(theme.warning.opacity(0.18), in: theme.cardShape)
                .overlay(theme.cardShape.strokeBorder(theme.warning.opacity(0.5)))
        }
    }

    private var names: String {
        func name(_ index: Int) -> String { editors[index].displayName ?? "Someone" }
        switch editors.count {
        case 1: return editors[0].displayName ?? "Someone else"
        case 2: return "\(name(0)) and \(name(1))"
        default: return "\(name(0)) and \(editors.count - 1) others"
        }
    }
}

/// A banner for a standing condition of the page, such as a closed board.
struct InfoBanner: View {
    @Environment(\.theme) private var theme
    var text: String
    var symbol = "info.circle.fill"

    var body: some View {
        Label(text, systemImage: symbol)
            .font(.footnote)
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(theme.info.opacity(0.14), in: theme.cardShape)
    }
}

/// Shown where the hub is down while a live page is open. Says nothing while
/// connected: an always-on "connected" badge is noise. And nothing with the instance out of
/// reach, which is no longer a mark in the bar: the window itself changes, and says so down
/// its side and on its first page (OfflineMode.swift).
struct ConnectionIndicator: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var body: some View {
        if !session.isOffline, session.hub.phase == .reconnecting {
            Image(systemName: "arrow.trianglehead.2.clockwise")
                .foregroundStyle(theme.warning)
                .symbolEffect(.rotate, isActive: true)
                .accessibilityLabel("Reconnecting to the live feed — changes from others may be delayed")
        }
    }
}

// MARK: - Progress rings

/// The sub-task rollup and the deadline countdown share this ring, so the two
/// on a card agree on size, stroke and cap.
struct ProgressRing: View {
    var value: Double
    var color: Color
    var size: CGFloat = 20
    var lineWidth: CGFloat = 3

    var body: some View {
        ZStack {
            Circle().stroke(color.opacity(0.22), lineWidth: lineWidth)
            Circle()
                .trim(from: 0, to: min(max(value, 0), 1))
                .stroke(color, style: StrokeStyle(lineWidth: lineWidth, lineCap: .round))
                .rotationEffect(.degrees(-90))
        }
        .frame(width: size, height: size)
        .animation(.smooth, value: value)
    }
}

// MARK: - Prompts

/// A one-field prompt, as creating a task, epic or group asks for: a name and nothing else.
struct TextPrompt: Identifiable {
    let id = UUID()
    var title: String
    var placeholder: String
    var initial = ""
    var action = "Create"
    var limit = FieldLimits.itemName
    var onSubmit: (String) async -> Void
}

private struct TextPromptModifier: ViewModifier {
    @Binding var prompt: TextPrompt?
    @State private var text = ""

    func body(content: Content) -> some View {
        content.alert(prompt?.title ?? "", isPresented: Binding(get: { prompt != nil }, set: { if !$0 { prompt = nil } }),
                      presenting: prompt) { prompt in
            TextField(prompt.placeholder, text: $text)
                .textInputAutocapitalization(.sentences)
            Button(prompt.action) {
                let value = String(text.trimmingCharacters(in: .whitespacesAndNewlines).prefix(prompt.limit))
                guard !value.isEmpty else { return }
                Task { await prompt.onSubmit(value) }
            }
            Button("Cancel", role: .cancel) {}
        }
        .onChange(of: prompt?.id) { _, _ in
            text = prompt?.initial ?? ""
        }
    }
}

/// A destructive action that asks first.
struct Confirmation: Identifiable {
    let id = UUID()
    var title: String
    var message: String
    var action = "Delete"
    var onConfirm: () async -> Void
}

private struct ConfirmationModifier: ViewModifier {
    @Binding var confirmation: Confirmation?

    func body(content: Content) -> some View {
        content.alert(confirmation?.title ?? "",
                      isPresented: Binding(get: { confirmation != nil }, set: { if !$0 { confirmation = nil } }),
                      presenting: confirmation) { confirmation in
            Button(confirmation.action, role: .destructive) {
                Task { await confirmation.onConfirm() }
            }
            Button("Cancel", role: .cancel) {}
        } message: { confirmation in
            Text(confirmation.message)
        }
    }
}

extension View {
    func textPrompt(_ prompt: Binding<TextPrompt?>) -> some View {
        modifier(TextPromptModifier(prompt: prompt))
    }

    func confirmation(_ confirmation: Binding<Confirmation?>) -> some View {
        modifier(ConfirmationModifier(confirmation: confirmation))
    }
}

// MARK: - Dates

extension Date {
    /// "13 Nov 2026, 14:05", for comments, sessions and the audit log.
    var stamp: String {
        formatted(.dateTime.day().month(.abbreviated).year().hour().minute())
    }

    var relative: String {
        formatted(.relative(presentation: .named, unitsStyle: .abbreviated))
    }
}
