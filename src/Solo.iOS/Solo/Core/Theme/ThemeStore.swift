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
    }
}
