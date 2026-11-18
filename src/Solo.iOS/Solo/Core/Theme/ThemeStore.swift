import Nuke
import SwiftUI

/// Light or dark is each person's own choice, kept on the device; which theme
/// is the instance's, chosen by its admin. The two are deliberately apart.
enum SchemePreference: String, CaseIterable, Identifiable {
    case system, light, dark

    var id: String { rawValue }

    var title: String {
        switch self {
        case .system: "Match system"
        case .light: "Light"
        case .dark: "Dark"
        }
    }

    var symbol: String {
        switch self {
        case .system: "circle.lefthalf.filled"
        case .light: "sun.max"
        case .dark: "moon"
        }
    }

    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .light: .light
        case .dark: .dark
        }
    }
}

@Observable
final class ThemeStore {
    private static let preferenceKey = "solo.colorScheme"
    private static let documentKey = "solo.theme.document"

    /// The theme of the instance showing. Kept across launches, and for each instance apart,
    /// so the first frame of one already wears it instead of flashing the fallback — or the
    /// last instance's — while it loads.
    private(set) var document: ThemeDocument?
    /// Where the instance showing keeps its theme, or nil when none is.
    @ObservationIgnored private var key: String?
    /// The theme's pictures for behind the app, by the path each was read from. Read once
    /// here rather than by each page that draws one: a page pushed in has its picture in its
    /// first frame, and every page draws the same one.
    private var backdrops: [String: Image] = [:]

    var preference: SchemePreference {
        didSet { UserDefaults.standard.set(preference.rawValue, forKey: Self.preferenceKey) }
    }

    init() {
        preference = UserDefaults.standard.string(forKey: Self.preferenceKey).flatMap(SchemePreference.init) ?? .system
    }

    private static func key(_ instance: URL) -> String { "\(documentKey).\(instance.absoluteString)" }

    /// Turns to an instance: its theme as last seen, until `load` says what it is now.
    func use(_ instance: URL?) {
        key = instance.map(Self.key)
        let defaults = UserDefaults.standard
        var data = key.flatMap(defaults.data)
        // From before themes were kept by instance: the one that was kept is the theme of
        // whichever instance was showing, which is the first to ask.
        if let key, data == nil, let legacy = defaults.data(forKey: Self.documentKey) {
            defaults.set(legacy, forKey: key)
            defaults.removeObject(forKey: Self.documentKey)
            data = legacy
        }
        document = data.flatMap { try? JSONDecoder().decode(ThemeDocument.self, from: $0) }
    }

    /// An instance was removed: its theme goes with it.
    func forget(_ instance: URL) {
        UserDefaults.standard.removeObject(forKey: Self.key(instance))
    }

    func theme(for scheme: ColorScheme) -> Theme {
        Theme(document: document, dark: scheme == .dark)
    }

    func apply(_ document: ThemeDocument?) {
        self.document = document
        guard let key else { return }
        if let document, let data = try? JSONEncoder().encode(document) {
            UserDefaults.standard.set(data, forKey: key)
        } else {
            UserDefaults.standard.removeObject(forKey: key)
        }
    }

    func load(from api: APIClient) async {
        // The theme as last seen first, so its picture is up without waiting on the instance
        // to say what the theme is now — and is up at all when the instance is out of reach.
        await loadBackdrops(from: api)
        if let document = try? await api.activeTheme() {
            apply(document)
            await loadBackdrops(from: api)
        }
    }

    /// The theme's picture for behind the app in a scheme, once it has been read.
    func backdrop(dark: Bool) -> Image? {
        guard let background = document?.background else { return nil }
        return (dark ? background.image : background.imageLight ?? background.image).flatMap { backdrops[$0] }
    }

    private func loadBackdrops(from api: APIClient) async {
        let background = document?.background
        let paths = Set([background?.image, background?.imageLight].compactMap { $0?.nilIfBlank })
        // A theme's content is addressed by a hash of what it is, so a path read before is
        // the same picture; one no longer named was another theme's, or another version's.
        backdrops = backdrops.filter { paths.contains($0.key) }
        for path in paths where backdrops[path] == nil {
            if let picture = try? await ImagePipeline.shared.image(for: api.url(path)) {
                backdrops[path] = Image(platform: picture)
            }
        }
    }
}

/// Resolves the accent for the scheme actually in effect and hands it to
/// everything below as the tint.
struct ThemedRoot<Content: View>: View {
    @Environment(ThemeStore.self) private var store
    @Environment(\.colorScheme) private var colorScheme
    @ViewBuilder var content: Content
    /// The window, edge to edge: what the theme's picture is laid out to fill.
    @State private var canvas: CGSize?

    var body: some View {
        let theme = store.theme(for: colorScheme)
        content
            .onGeometryChange(for: CGSize.self) { proxy in
                CGSize(width: proxy.size.width + proxy.safeAreaInsets.leading + proxy.safeAreaInsets.trailing,
                       height: proxy.size.height + proxy.safeAreaInsets.top + proxy.safeAreaInsets.bottom)
            } action: { canvas = $0 }
            .environment(\.backdropCanvas, canvas)
            .environment(\.theme, theme)
            .tint(theme.primary)
            // A switch's knob is white whatever its track is, so the track is the fill white reads on.
            .toggleStyle(AccentSwitchStyle(fill: theme.fill))
            // A Mac draws a field its own bezel; the app's fields already sit in something.
            #if os(macOS)
            .textFieldStyle(.plain)
            #endif
    }
}

extension View {
    /// On a menu that has toggles in it: they are the menu's own ticked rows, and not the
    /// app's switch. The switch is set for the whole app above, a menu included, and a switch
    /// in a menu is a row that is greyed out and does nothing when it is pressed.
    func menuToggles() -> some View { toggleStyle(.automatic) }
}

/// The system's switch, on a track its knob shows up against.
private struct AccentSwitchStyle: ToggleStyle {
    var fill: Color

    func makeBody(configuration: Configuration) -> some View {
        Toggle(configuration).toggleStyle(.switch).tint(fill)
    }
}

extension View {
    /// For the label of a button filled with the accent: a prominent button anywhere, a toolbar's
    /// confirming action. The colour has to be set here, on the label — set on the button, the
    /// prominent style lays its own white over it, which is unreadable on a light accent.
    func onAccent() -> some View { modifier(OnAccent()) }
}

private struct OnAccent: ViewModifier {
    @Environment(\.theme) private var theme
    @Environment(\.isEnabled) private var isEnabled

    func body(content: Content) -> some View {
        content.foregroundStyle(theme.onPrimary.opacity(isEnabled ? 1 : 0.45))
    }
}
