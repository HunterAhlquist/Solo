import SwiftUI

/// One scheme of the instance's theme, resolved to colours SwiftUI can draw.
///
/// A Solo theme is written for the web app: palettes, a stylesheet, fonts.
/// The native app takes the palette and leaves the rest to the system, so the
/// instance's colours arrive on Apple's own controls and materials.
nonisolated struct Theme: Equatable, Sendable {
    var primary: Color
    var secondary: Color
    var tertiary: Color
    /// Text on a filled primary surface.
    var onPrimary: Color
    var background: Color
    var backgroundGray: Color
    var surface: Color
    var textPrimary: Color
    var textSecondary: Color
    var textDisabled: Color
    var divider: Color
    var success: Color
    var warning: Color
    var error: Color
    var info: Color
    var isDark: Bool

    /// The theme's type family reduced to the nearest system design: a
    /// monospaced terminal theme stays monospaced without shipping a web font.
    var fontDesign: Font.Design = .default
    var headingWeight: Font.Weight = .bold
    /// Card corners, scaled from the theme's `borderRadius`.
    var cornerRadius: CGFloat = 14

    /// The neutral look: system colours, for before a theme arrives and for a
    /// palette key the theme leaves out.
    static func fallback(dark: Bool) -> Theme {
        Theme(
            primary: dark ? Color(red: 1, green: 0.965, blue: 0) : Color(red: 0.875, green: 0.4, blue: 0.063),
            secondary: .secondary, tertiary: .gray,
            onPrimary: dark ? .black : .white,
            background: Color(uiColor: .systemBackground),
            backgroundGray: Color(uiColor: .secondarySystemBackground),
            surface: Color(uiColor: .secondarySystemGroupedBackground),
            textPrimary: Color(uiColor: .label),
            textSecondary: Color(uiColor: .secondaryLabel),
            textDisabled: Color(uiColor: .tertiaryLabel),
            divider: Color(uiColor: .separator),
            success: .green, warning: .orange, error: .red, info: .blue,
            isDark: dark)
    }

    init(primary: Color, secondary: Color, tertiary: Color, onPrimary: Color, background: Color, backgroundGray: Color,
         surface: Color, textPrimary: Color, textSecondary: Color, textDisabled: Color, divider: Color, success: Color,
         warning: Color, error: Color, info: Color, isDark: Bool) {
        self.primary = primary
        self.secondary = secondary
        self.tertiary = tertiary
        self.onPrimary = onPrimary
        self.background = background
        self.backgroundGray = backgroundGray
        self.surface = surface
        self.textPrimary = textPrimary
        self.textSecondary = textSecondary
        self.textDisabled = textDisabled
        self.divider = divider
        self.success = success
        self.warning = warning
        self.error = error
        self.info = info
        self.isDark = isDark
    }

    init(document: ThemeDocument?, dark: Bool) {
        let base = Theme.fallback(dark: dark)
        guard let document, let palette = dark ? document.dark : document.light else {
            self = base
            return
        }

        func color(_ value: String?, _ fallback: Color) -> Color {
            value.flatMap(CSSColor.init)?.color ?? fallback
        }

        let primary = CSSColor(palette.primary ?? "")
        self.init(
            primary: primary?.color ?? base.primary,
            secondary: color(palette.secondary, base.secondary),
            tertiary: color(palette.tertiary, base.tertiary),
            // Left out by most themes on purpose: derived from the fill, so a
            // light accent gets dark labels.
            onPrimary: CSSColor(palette.primaryContrastText ?? "")?.color
                ?? (primary.map { $0.luminance > 0.45 ? Color.black : Color.white } ?? base.onPrimary),
            background: color(palette.background, base.background),
            backgroundGray: color(palette.backgroundGray, base.backgroundGray),
            surface: color(palette.surface, base.surface),
            textPrimary: color(palette.textPrimary, base.textPrimary),
            textSecondary: color(palette.textSecondary, base.textSecondary),
            textDisabled: color(palette.textDisabled, base.textDisabled),
            divider: color(palette.divider ?? palette.linesDefault, base.divider),
            success: color(palette.success, base.success),
            warning: color(palette.warning, base.warning),
            error: color(palette.error, base.error),
            info: color(palette.info, base.info),
            isDark: dark)

        let families = (document.typography?.fontFamily ?? []).map { $0.lowercased() }
        if families.contains(where: { $0.contains("mono") || $0.contains("courier") || $0.contains("consolas") }) {
            fontDesign = .monospaced
        } else if families.contains(where: { $0 == "serif" || $0.contains("georgia") || $0.contains("times") }) {
            fontDesign = .serif
        } else if families.contains(where: { $0.contains("rounded") }) {
            fontDesign = .rounded
        }

        if let weight = document.typography?.headingWeight.flatMap(Int.init) {
            headingWeight = weight >= 800 ? .heavy : weight >= 700 ? .bold : weight >= 600 ? .semibold : .medium
        }

        if let radius = document.layout?.borderRadius, let px = Double(radius.replacingOccurrences(of: "px", with: "")) {
            cornerRadius = max(4, px * 1.5)
        }
    }

    /// A card's shape everywhere a card is drawn.
    var cardShape: RoundedRectangle { RoundedRectangle(cornerRadius: cornerRadius, style: .continuous) }

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
        (CSSColor(hex)?.luminance ?? 0) > 0.45 ? .black : .white
    }
}

extension EnvironmentValues {
    @Entry var theme: Theme = .fallback(dark: true)
}
