import SwiftUI

/// How the app is coloured.
///
/// A Solo theme is written for the web app: palettes, a stylesheet, fonts,
/// corner radii. The native app looks like an Apple app first — system
/// backgrounds, system type, Liquid Glass — and takes one thing from the
/// instance's theme: its accent. That accent arrives as the tint on controls
/// and as a faint wash behind the content, which is enough to tell one
/// instance from another without repainting the system.
nonisolated struct Theme: Equatable, Sendable {
    /// The instance's accent for the scheme in effect.
    var primary: Color
    var secondary: Color
    /// Text on a filled accent surface: black or white, whichever reads.
    var onPrimary: Color
    /// The accent as a fill the system puts white on and gives no say over: a switch that is on,
    /// a swipe action. The accent itself where white reads on it, and a deeper shade of it where
    /// it would not — a bright yellow accent is no ground for a white knob.
    var fill: Color
    var isDark: Bool

    // Everything else is the system's, named for what it is used for.
    var textPrimary: Color { .primary }
    var textSecondary: Color { .secondary }
    var textDisabled: Color { .tertiaryLabel }
    var divider: Color { .separator }
    /// A quiet fill for a chip, a well, a card resting on glass.
    var backgroundGray: Color { .tertiaryFill }
    var surface: Color { .quaternaryFill }
    var success: Color { .green }
    var warning: Color { .orange }
    var error: Color { .red }
    var info: Color { .blue }
    var headingWeight: Font.Weight { .bold }

    /// A soft tint of the accent, for a selected row or segment.
    var accentWash: Color { primary.opacity(isDark ? 0.24 : 0.16) }

    static func fallback(dark: Bool) -> Theme {
        Theme(primary: .accentColor, secondary: .secondary, onPrimary: .white, fill: .accentColor, isDark: dark)
    }

    init(primary: Color, secondary: Color, onPrimary: Color, fill: Color, isDark: Bool) {
        self.primary = primary
        self.secondary = secondary
        self.onPrimary = onPrimary
        self.fill = fill
        self.isDark = isDark
    }

    init(document: ThemeDocument?, dark: Bool) {
        guard let palette = dark ? document?.dark : document?.light, let primary = CSSColor(palette.primary ?? "") else {
            self = .fallback(dark: dark)
            return
        }
        self.init(
            primary: primary.color,
            secondary: CSSColor(palette.secondary ?? "")?.color ?? primary.color,
            // Worked out from the accent rather than read from the theme: a theme written for
            // the web may name a contrast colour that its own accent doesn't carry.
            onPrimary: primary.readable,
            fill: primary.carryingWhite.color,
            isDark: dark)
    }

    /// The shape of a panel everywhere one is drawn.
    var cardShape: RoundedRectangle { RoundedRectangle(cornerRadius: 20, style: .continuous) }

    /// The countdown colour a deadline wears: green at the start of its window, red at the end.
    static func deadlineColor(elapsed: Double) -> Color {
        Color(hue: 120 * (1 - min(max(elapsed, 0), 1)) / 360, saturation: 0.75, brightness: 0.78)
    }
}

/// A parsed CSS colour: the forms a theme's palette may use.
nonisolated struct CSSColor: Equatable, Sendable {
    var red: Double
    var green: Double
    var blue: Double
    var alpha: Double

    init(red: Double, green: Double, blue: Double, alpha: Double = 1) {
        self.red = red
        self.green = green
        self.blue = blue
        self.alpha = alpha
    }

    init?(_ text: String) {
        let value = text.trimmingCharacters(in: .whitespaces).lowercased()
        if value.hasPrefix("#") {
            let hex = Array(value.dropFirst())
            guard hex.allSatisfy(\.isHexDigit) else { return nil }
            func component(_ digits: ArraySlice<Character>) -> Double {
                let text = digits.count == 1 ? String(repeating: String(digits), count: 2) : String(digits)
                return Double(Int(text, radix: 16) ?? 0) / 255
            }
            switch hex.count {
            case 3, 4:
                self.init(red: component(hex[0...0]), green: component(hex[1...1]), blue: component(hex[2...2]),
                          alpha: hex.count == 4 ? component(hex[3...3]) : 1)
            case 6, 8:
                self.init(red: component(hex[0...1]), green: component(hex[2...3]), blue: component(hex[4...5]),
                          alpha: hex.count == 8 ? component(hex[6...7]) : 1)
            default:
                return nil
            }
            return
        }

        if let open = value.firstIndex(of: "("), value.hasSuffix(")") {
            let function = value[..<open]
            let arguments = value[value.index(after: open)..<value.index(before: value.endIndex)]
                .split(whereSeparator: { $0 == "," || $0 == " " || $0 == "/" })
                .map(String.init)
            func number(_ text: String, scale: Double) -> Double? {
                if text.hasSuffix("%") { return Double(text.dropLast()).map { $0 / 100 } }
                return Double(text.replacingOccurrences(of: "deg", with: "")).map { $0 / scale }
            }
            guard arguments.count >= 3 else { return nil }
            let alpha = arguments.count > 3 ? number(arguments[3], scale: 1) ?? 1 : 1
            if function.hasPrefix("rgb"),
               let r = number(arguments[0], scale: 255), let g = number(arguments[1], scale: 255),
               let b = number(arguments[2], scale: 255) {
                self.init(red: r, green: g, blue: b, alpha: alpha)
                return
            }
            if function.hasPrefix("hsl"),
               let h = number(arguments[0], scale: 360), let s = number(arguments[1], scale: 1),
               let l = number(arguments[2], scale: 1) {
                self = CSSColor.hsl(h, s, l, alpha: alpha)
                return
            }
            return nil
        }

        switch value {
        case "white": self.init(red: 1, green: 1, blue: 1)
        case "black": self.init(red: 0, green: 0, blue: 0)
        case "transparent": self.init(red: 0, green: 0, blue: 0, alpha: 0)
        case "red": self.init(red: 1, green: 0, blue: 0)
        case "green": self.init(red: 0, green: 0.5, blue: 0)
        case "blue": self.init(red: 0, green: 0, blue: 1)
        case "gray", "grey": self.init(red: 0.5, green: 0.5, blue: 0.5)
        case "yellow": self.init(red: 1, green: 1, blue: 0)
        case "orange": self.init(red: 1, green: 0.647, blue: 0)
        case "purple": self.init(red: 0.5, green: 0, blue: 0.5)
        default: return nil
        }
    }

    static func hsl(_ h: Double, _ s: Double, _ l: Double, alpha: Double = 1) -> CSSColor {
        func channel(_ n: Double) -> Double {
            let k = (n + h * 12).truncatingRemainder(dividingBy: 12)
            let a = s * min(l, 1 - l)
            return l - a * max(-1, min(k - 3, 9 - k, 1))
        }
        return CSSColor(red: channel(0), green: channel(8), blue: channel(4), alpha: alpha)
    }

    /// WCAG relative luminance, which is what decides black or white text on a fill.
    var luminance: Double {
        func linear(_ c: Double) -> Double { c <= 0.03928 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue)
    }

    var color: Color { Color(.sRGB, red: red, green: green, blue: blue, opacity: alpha) }

    /// How strongly this stands out from another colour, as WCAG measures it: 1 for none, 21 for black on white.
    func contrast(with other: CSSColor) -> Double {
        let lighter = max(luminance, other.luminance), darker = min(luminance, other.luminance)
        return (lighter + 0.05) / (darker + 0.05)
    }

    static let white = CSSColor(red: 1, green: 1, blue: 1)
    static let black = CSSColor(red: 0, green: 0, blue: 0)

    /// Black or white, whichever stands out more on this as a fill.
    var readable: Color {
        contrast(with: .black) >= contrast(with: .white) ? .black : .white
    }

    /// This colour, or the nearest darker shade of it that white reads on (3:1, what WCAG asks
    /// of a control's parts). The hue is kept: only its channels are scaled down.
    var carryingWhite: CSSColor {
        guard contrast(with: .white) < 3 else { return self }
        var low = 0.0, high = 1.0
        for _ in 0..<16 {
            let middle = (low + high) / 2
            let shade = CSSColor(red: red * middle, green: green * middle, blue: blue * middle, alpha: alpha)
            if shade.contrast(with: .white) >= 3 { low = middle } else { high = middle }
        }
        return CSSColor(red: red * low, green: green * low, blue: blue * low, alpha: alpha)
    }

    /// `#rrggbb`, the only form a label's colour is stored in.
    var hex: String {
        String(format: "#%02x%02x%02x", Int((red * 255).rounded()), Int((green * 255).rounded()), Int((blue * 255).rounded()))
    }
}

nonisolated extension Color {
    /// A label colour as the API stores it; grey when the string isn't one.
    init(hex: String) {
        self = CSSColor(hex)?.color ?? .gray
    }

    /// Black or white, whichever reads on this fill.
    static func readable(on hex: String) -> Color {
        CSSColor(hex)?.readable ?? .white
    }
}

extension EnvironmentValues {
    @Entry var theme: Theme = .fallback(dark: true)
}
