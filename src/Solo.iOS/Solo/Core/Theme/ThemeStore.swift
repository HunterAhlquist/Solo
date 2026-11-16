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

    /// The instance's active theme. Kept across launches so the first frame
    /// already wears it, instead of flashing the fallback while it loads.
    private(set) var document: ThemeDocument?

    var preference: SchemePreference {
        didSet { UserDefaults.standard.set(preference.rawValue, forKey: Self.preferenceKey) }
    }

    init() {
        preference = UserDefaults.standard.string(forKey: Self.preferenceKey).flatMap(SchemePreference.init) ?? .system
        if let data = UserDefaults.standard.data(forKey: Self.documentKey) {
            document = try? JSONDecoder().decode(ThemeDocument.self, from: data)
        }
    }

    func theme(for scheme: ColorScheme) -> Theme {
        Theme(document: document, dark: scheme == .dark)
    }

    func apply(_ document: ThemeDocument?) {
        self.document = document
        if let document, let data = try? JSONEncoder().encode(document) {
            UserDefaults.standard.set(data, forKey: Self.documentKey)
        } else {
            UserDefaults.standard.removeObject(forKey: Self.documentKey)
        }
    }

    func load(from api: APIClient) async {
        if let document = try? await api.activeTheme() {
            apply(document)
        }
    }
}

/// Resolves the accent for the scheme actually in effect and hands it to
/// everything below as the tint.
struct ThemedRoot<Content: View>: View {
    @Environment(ThemeStore.self) private var store
    @Environment(\.colorScheme) private var colorScheme
    @ViewBuilder var content: Content

    var body: some View {
        let theme = store.theme(for: colorScheme)
        content
            .environment(\.theme, theme)
            .tint(theme.primary)
            // A switch's knob is white whatever its track is, so the track is the fill white reads on.
            .toggleStyle(AccentSwitchStyle(fill: theme.fill))
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
