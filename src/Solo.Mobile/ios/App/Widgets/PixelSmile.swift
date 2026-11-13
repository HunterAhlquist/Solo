import SwiftUI

/// The smile from the email header (src/Solo.Api/Email/mark.png): 12 by 9
/// pixels, drawn square at whole multiples so it stays crisp.
struct PixelSmile: View {
    var scale: CGFloat = 2

    static let rows = [
        ".###....###.",
        "#...#..#...#",
        "............",
        "............",
        "............",
        ".##########.",
        ".#........#.",
        "..#......#..",
        "...######...",
    ]

    var body: some View {
        Canvas { context, _ in
            for (y, row) in Self.rows.enumerated() {
                for (x, pixel) in row.enumerated() where pixel == "#" {
                    context.fill(Path(CGRect(x: CGFloat(x) * scale, y: CGFloat(y) * scale, width: scale, height: scale)),
                                 with: .foreground)
                }
            }
        }
        .frame(width: 12 * scale, height: 9 * scale)
        .accessibilityHidden(true)
    }
}

/// The smile as the refresh button, in the bottom-right corner of every widget.
struct RefreshSmile: View {
    var scale: CGFloat = 2

    var body: some View {
        Button(intent: RefreshIntent()) {
            PixelSmile(scale: scale)
                .foregroundStyle(WidgetTheme.primary)
                .padding(6)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .accessibilityLabel("Refresh")
    }
}
