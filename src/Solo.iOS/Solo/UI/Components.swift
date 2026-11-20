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
/// Over that goes the theme's picture, where it has one for behind the app. Every page draws
/// this view for itself at its own size, and the picture is one picture for the window: each
/// draws the part of it that it is standing in front of. So it holds still while a page
/// slides in or a panel opens beside one.
///
/// The picture is the main window's alone (`Backdrop`). A cover, and on a Mac the windows
/// that are not the main one — Settings, a sketch, a picture — draw the wash and no picture:
/// what is beside the app is told from the app by being plain. A sheet draws nothing at all
/// here, and stands on what the system gives one: on an iPhone or iPad a pane of glass,
/// through which the page it rose over is still there, bent, and on a Mac the sheet's own
/// plain ground.
///
/// It is drawn at a quarter of its strength. Glass carries most of what is read here, but
/// not all: a section's heading, a page's title and an empty page's one line sit straight
/// on the page. At full strength they were lost in the picture, and at the web's half the
/// quieter of them still were — the web has its panels behind such lines more often.
struct AppBackground: View {
    @Environment(\.theme) private var theme
    @Environment(ThemeStore.self) private var themes: ThemeStore?
    @Environment(\.backdropCanvas) private var canvas
    @Environment(\.backdrop) private var backdrop

    /// How much of the picture shows: what `--solo-backdrop-strength` is on the web.
    private static let strength = 0.25

    var body: some View {
        if backdrop != .none {
            ground
        }
    }

    private var ground: some View {
        ZStack {
            Color.groupedBackground
            LinearGradient(
                colors: [theme.primary.opacity(theme.isDark ? 0.16 : 0.12), theme.secondary.opacity(0.05), .clear],
                startPoint: .topLeading, endPoint: .bottom)
            RadialGradient(colors: [theme.primary.opacity(theme.isDark ? 0.10 : 0.07), .clear],
                           center: .bottomTrailing, startRadius: 0, endRadius: 520)
            if backdrop == .pictured, let background = themes?.document?.background, let picture = themes?.backdrop(dark: theme.isDark) {
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

/// What a page draws behind itself (`AppBackground`), by where the page is.
nonisolated enum Backdrop: Sendable {
    /// The main window: the wash, and the theme's picture over it.
    case pictured
    /// A cover, or a window beside the main one: the wash alone.
    case plain
    /// A sheet: nothing, so what the system gives a sheet is its ground.
    case none
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

    /// What a page draws behind itself: the theme's picture in the main window, and less
    /// in whatever is over it or beside it (`withoutBackdropPicture`, the app's `sheet`).
    @Entry var backdrop = Backdrop.pictured

    /// Whether what stands beside a page — the app's sidebar, a design doc's tree — goes over
    /// it instead, taking no room from it. An iPad has room for both and the page is made
    /// narrower. A phone's screen wide enough to have such a pane at all — a folding one
    /// opened out, a large one on its side — has none to spare: beside one, the page was
    /// squeezed to little more than the pane's own width. It is told by the window's shorter
    /// side, under every iPad's and over every phone's, and not by the kind of device: a
    /// window that size on anything is laid out the same. A Mac's window is the Mac's to size.
    var panesFloat: Bool {
        #if os(iOS)
        backdropCanvas.map { min($0.width, $0.height) < 700 } ?? false
        #else
        false
        #endif
    }
}

extension View {
    /// On what covers the app or is in a window beside it: a cover, Settings. Whatever is in
    /// it draws its background without the theme's picture.
    func withoutBackdropPicture() -> some View {
        environment(\.backdrop, .plain)
    }

    // Every sheet in the app comes through one of these two, without saying so. SwiftUI's own
    // `sheet` takes an `onDismiss` too; a call that says none matches these more closely than
    // it matches SwiftUI's, which would have to fill one in. SwiftUI's is reached by naming it.
    //
    // What they do for a sheet. Its pages draw no background of their own, the theme's
    // picture least of all, and not the wash either: a sheet stands on what the system
    // gives one. On an iPhone and iPad that is a pane of glass, and a page that painted
    // itself over it had a second copy of the app's background where the app, bent by the
    // glass, should show through. On a Mac it is the sheet's own plain ground, and the sheet
    // is given room to start from: an iPhone's sheet is the size the screen gives it, and a
    // Mac's is the size of what is in it, which for a list or a page of text is one line tall.
    func sheet<Content: View>(isPresented: Binding<Bool>, @ViewBuilder content: @escaping () -> Content) -> some View {
        sheet(isPresented: isPresented, onDismiss: nil) { content().asSheet() }
    }

    func sheet<Item: Identifiable, Content: View>(item: Binding<Item?>,
                                                 @ViewBuilder content: @escaping (Item) -> Content) -> some View {
        sheet(item: item, onDismiss: nil) { content($0).asSheet() }
    }

    private func asSheet() -> some View {
        #if os(macOS)
        frame(minWidth: 540, minHeight: 480).environment(\.backdrop, .none)
        #else
        // Said outright: left to itself a sheet at its full height is given a solid ground,
        // and only a shorter one the glass.
        environment(\.backdrop, .none)
            .presentationBackground { Color.clear.glassEffect(.regular, in: .rect).ignoresSafeArea() }
        #endif
    }

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
    /// frosted panel over the background rather than a solid slab on it. In
    /// Settings (`GlassList`) the panel is glass.
    func themedRow() -> some View {
        listRowBackground(ThemedRowBackground())
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

/// A grouped list whose sections are panes of Liquid Glass: the pages of Settings. They are
/// read straight through to the page, and where a theme puts a picture behind the app a
/// row's wash was not enough to read a row of small text over it.
///
/// An iPhone's and iPad's list is given glass a row at a time (`themedRow`), which the list
/// cuts to its sections' corners. A Mac's grouped form takes no background for a row and
/// draws its sections itself, so there a section is made one row holding all of its rows,
/// with one pane of glass behind it, let out to the edges of the panel the form draws: the
/// rows are still in the form, and its controls are still laid out as a form's. Only for
/// pages of a few rows: on a Mac a section's rows are all built at once.
struct GlassList<Content: View>: View {
    /// A form on an iPhone and iPad, where a page of fields was written as one.
    var form = false
    @ViewBuilder var content: Content

    var body: some View {
        #if os(macOS)
        Form {
            ForEach(sections: content) { section in
                // A heading that is there but shows nothing still takes a heading's room, so
                // a section is given one only if it has one to show. Until that is known the
                // section is not shown: it is known a frame later, and a section that was
                // drawn in that frame was drawn a heading's height too low.
                let heading = headings[section.id]
                Group {
                    switch (heading ?? true, section.footer.isEmpty) {
                    case (false, true): Section { pane(section) }
                    case (true, true): Section { pane(section) } header: { section.header }
                    case (false, false): Section { pane(section) } footer: { section.footer }
                    case (true, false): Section { pane(section) } header: { section.header } footer: { section.footer }
                    }
                }
                .opacity(heading == nil ? 0 : 1)
            }
        }
        #else
        Group {
            if form {
                Form { content }
            } else {
                List { content }
            }
        }
        .environment(\.glassRows, true)
        #endif
    }

    #if os(macOS)
    /// Whether each section has a heading. A section written without one is still handed
    /// over with a heading, an empty one, and nothing says which it is but its size: so each
    /// is laid out once, unseen, behind its section.
    @State private var headings: [SectionConfiguration.ID: Bool] = [:]

    /// Whether a heading laid out by itself came to nothing: no size at all, or the ten
    /// points square that stands in for a view there is nothing of, which is what a section
    /// written without a heading has for one.
    private static func showsNothing(_ size: CGSize) -> Bool {
        size.height < 1 || (size.width <= 10 && size.height <= 10)
    }

    /// A section's rows as the one row the form is given, on their pane of glass.
    private func pane(_ section: SectionConfiguration) -> some View {
        Group(subviews: section.content) { rows in
            if !rows.isEmpty {
                VStack(spacing: 0) {
                    ForEach(rows.indices, id: \.self) { index in
                        if index > 0 { Divider() }
                        rows[index]
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.top, index == 0 ? 0 : Self.rowGap)
                            .padding(.bottom, index == rows.count - 1 ? 0 : Self.rowGap)
                    }
                }
                .background {
                    Color.clear
                        .glassEffect(.regular, in: .rect(cornerRadius: Self.panelRadius))
                        .padding(-Self.panelInset)
                }
                .background {
                    VStack(spacing: 0) { section.header }
                        .fixedSize()
                        .onGeometryChange(for: Bool.self) { Self.showsNothing($0.size) } action: { empty in
                            headings[section.id] = !empty
                        }
                        .hidden()
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                }
            }
        }
    }

    // A grouped form's own measures: what it puts above and below a row, how far its panel
    // stands out from its rows, and the panel's corners.
    private static var rowGap: CGFloat { 10 }
    private static var panelInset: CGFloat { 10 }
    private static var panelRadius: CGFloat { 10 }
    #endif
}

extension EnvironmentValues {
    /// Inside a `GlassList`: its rows are backed by glass.
    @Entry var glassRows = false
}

/// What is behind a row of a themed list: the page's wash, or glass in a `GlassList`.
private struct ThemedRowBackground: View {
    @Environment(\.glassRows) private var glass

    var body: some View {
        if glass {
            // A row's glass is let out past the row on every side, where the list cuts it
            // off: glass bends what is behind it along its edge, and with an edge to every
            // row a section was a stack of lenses and not a pane.
            Color.clear.glassEffect(.regular, in: .rect).padding(-24)
        } else {
            Color.groupedRowBackground.opacity(0.55)
        }
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

/// A menu that stands by itself in a row of a card: a member's role, a label's actions, a
/// filter. An iPad draws one as its label and nothing more. A Mac gives it a bezel of its
/// own — flat, in the accent, with an arrow beside the one the label already draws — which
/// is not how anything else in the app is drawn; so there it stands on a lens of glass, as
/// the app's buttons do.
///
/// The Mac's bezel is kept and made clear, and the lens put behind it. A Mac's menu draws a
/// label of the app's own — a badge, two colours of text — only inside a bezel: told to be
/// plain or borderless it draws a title and a picture and nothing else. And the bezel is
/// what is clicked, so the whole of the lens opens the menu.
struct RowMenu<Face: View, Content: View>: View {
    @Environment(\.theme) private var theme
    @Environment(\.font) private var font
    /// A glyph by itself, on a round lens.
    var round = false
    @ViewBuilder var content: Content
    @ViewBuilder var label: Face

    var body: some View {
        #if os(macOS)
        Menu { content } label: {
            // The accent is given by name: the tint is what makes the bezel clear.
            if round {
                // Room for the glyph, or the bezel is no bigger than the three dots in it.
                label.labelStyle(.iconOnly).foregroundStyle(theme.primary).frame(width: 20, height: 18)
            } else {
                // A Mac's menu sets its label in its own size; the row's is handed in again,
                // so that a menu and a button side by side are set alike.
                label.font(font)
            }
        }
        .buttonStyle(.bordered)
        .buttonBorderShape(round ? .circle : .capsule)
        .tint(.clear)
        .menuIndicator(.hidden)
        .fixedSize()
        .glassEffect(.regular.interactive(), in: .capsule)
        #else
        if round {
            Menu { content } label: { label.labelStyle(.iconOnly) }.frame(width: 32, height: 32)
        } else {
            Menu { content } label: { label }
        }
        #endif
    }
}

extension RowMenu where Face == Label<Text, Image> {
    /// The usual one: a glyph that opens what can be done to the row.
    init(_ title: String, systemImage: String, @ViewBuilder content: () -> Content) {
        self.init(round: true, content: content) { Label(title, systemImage: systemImage) }
    }
}

#if os(macOS)
enum RowLens {
    /// A clear bezel still lays a little of the text's colour over what is behind it. A
    /// button on a lens has no bezel, and is given the same, so the two are one glass.
    static let wash = Color.primary.opacity(0.1)
}

/// The room a row's control has on its lens: a glyph in a circle, anything else in a capsule.
private struct RowLensRoom: ViewModifier {
    var round: Bool

    func body(content: Content) -> some View {
        if round {
            content.foregroundStyle(.tint).frame(width: 28, height: 28).contentShape(.circle)
        } else {
            content.padding(.horizontal, 10).padding(.vertical, 4).contentShape(.capsule)
        }
    }
}

/// A button in a row of a card, drawn as `RowMenu` draws a menu there.
struct RowLensButtonStyle: ButtonStyle {
    @Environment(\.isEnabled) private var isEnabled
    var round = false

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .modifier(RowLensRoom(round: round))
            // Over the glass and under the label, where a bezel's is.
            .background(RowLens.wash, in: .capsule)
            .glassEffect(.regular.interactive(), in: .capsule)
            .opacity(isEnabled ? (configuration.isPressed ? 0.6 : 1) : 0.45)
    }
}
#endif

extension View {
    /// On a button that stands by itself in a row of a card, beside its `RowMenu`s: a lens
    /// of glass on a Mac, and as it was on an iPad.
    @ViewBuilder
    func rowLens(round: Bool = false) -> some View {
        #if os(macOS)
        buttonStyle(RowLensButtonStyle(round: round))
        #else
        self
        #endif
    }
}

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
    /// What to put back when it is turned down, where asking had already changed what shows.
    var onCancel: () -> Void = {}
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
            Button("Cancel", role: .cancel) { confirmation.onCancel() }
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
