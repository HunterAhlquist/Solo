import SwiftUI
import UIKit

/// The Ahlquist Interactive theme (src/Solo.Api/Themes/ahlquist-interactive),
/// light and dark: a black-and-yellow terminal at night, warm paper and orange
/// by day, in Courier New.
enum WidgetTheme {
    static let background = dynamic(dark: 0x000000, light: 0xFDF9F3)
    static let primary = dynamic(dark: 0xFFF600, light: 0xDF6610)
    static let text = dynamic(dark: 0xFFFFFF, light: 0x231D16)
    static let muted = dynamic(dark: 0xB0B0A0, light: 0x5B4F42)
    static let faint = dynamic(dark: 0x6F6F64, light: 0x8F8375)
    static let divider = dynamic(dark: 0xFFF600, light: 0xE4D9C9, darkAlpha: 0.25)
    static let new = dynamic(dark: 0xFF5252, light: 0xB3261E)
    // The theme's light warning (#8a5a00) reads as brown on paper, and the
    // board's "yellow = in progress" has to read as yellow: a darker gold that
    // still clears 3:1 against the background, as a graphic needs to.
    static let started = dynamic(dark: 0xFFB300, light: 0xB07D00)
    static let done = dynamic(dark: 0x5AD469, light: 0x1F6F43)

    static func mono(_ size: CGFloat, bold: Bool = false) -> Font {
        .custom(bold ? "CourierNewPS-BoldMT" : "CourierNewPSMT", size: size)
    }

    private static func dynamic(dark: UInt32, light: UInt32, darkAlpha: CGFloat = 1) -> Color {
        Color(UIColor { traits in
            traits.userInterfaceStyle == .dark ? UIColor(hex: dark, alpha: darkAlpha) : UIColor(hex: light)
        })
    }
}

private extension UIColor {
    convenience init(hex: UInt32, alpha: CGFloat = 1) {
        self.init(red: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
                  blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
    }
}
