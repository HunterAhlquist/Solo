import SwiftUI

/// Two digits on a seven-segment display, unlit segments faintly showing as on
/// a real one, and the zero slashed as in a terminal font.
struct SevenSegmentNumber: View {
    var value: Int
    var digitWidth: CGFloat = 44
    var color: Color = WidgetTheme.primary

    var body: some View {
        let clamped = max(0, min(99, value))
        HStack(spacing: digitWidth * 0.22) {
            SevenSegmentDigit(digit: clamped / 10, color: color)
                .frame(width: digitWidth, height: digitWidth * 1.8)
            SevenSegmentDigit(digit: clamped % 10, color: color)
                .frame(width: digitWidth, height: digitWidth * 1.8)
        }
        .accessibilityElement()
        .accessibilityLabel("\(clamped)")
    }
}

struct SevenSegmentDigit: View {
    var digit: Int
    var color: Color

    /// Segments a to g, clockwise from the top, g in the middle.
    private static let lit: [[Bool]] = [
        [true, true, true, true, true, true, false],
        [false, true, true, false, false, false, false],
        [true, true, false, true, true, false, true],
        [true, true, true, true, false, false, true],
        [false, true, true, false, false, true, true],
        [true, false, true, true, false, true, true],
        [true, false, true, true, true, true, true],
        [true, true, true, false, false, false, false],
        [true, true, true, true, true, true, true],
        [true, true, true, true, false, true, true],
    ]

    var body: some View {
        Canvas { context, size in
            let w = size.width, h = size.height
            let t = w * 0.2
            let segments = Self.segments(w: w, h: h, t: t)
            for (index, segment) in segments.enumerated() {
                let on = Self.lit[digit][index]
                context.fill(segment, with: .color(color.opacity(on ? 1 : 0.1)))
            }
            if digit == 0 {
                var slash = Path()
                slash.move(to: CGPoint(x: w - t * 1.2, y: t * 1.6))
                slash.addLine(to: CGPoint(x: t * 1.2, y: h - t * 1.6))
                context.stroke(slash, with: .color(color), style: StrokeStyle(lineWidth: t * 0.7, lineCap: .square))
            }
        }
    }

    /// Hexagonal bars with a small gap between neighbours.
    private static func segments(w: CGFloat, h: CGFloat, t: CGFloat) -> [Path] {
        let g = t * 0.12
        let mid = h / 2
        func horizontal(_ y: CGFloat) -> Path {
            Path { p in
                p.move(to: CGPoint(x: t / 2 + g, y: y))
                p.addLine(to: CGPoint(x: t + g, y: y - t / 2))
                p.addLine(to: CGPoint(x: w - t - g, y: y - t / 2))
                p.addLine(to: CGPoint(x: w - t / 2 - g, y: y))
                p.addLine(to: CGPoint(x: w - t - g, y: y + t / 2))
                p.addLine(to: CGPoint(x: t + g, y: y + t / 2))
                p.closeSubpath()
            }
        }
        func vertical(_ x: CGFloat, _ top: CGFloat, _ bottom: CGFloat) -> Path {
            Path { p in
                p.move(to: CGPoint(x: x, y: top + g))
                p.addLine(to: CGPoint(x: x + t / 2, y: top + t / 2 + g))
                p.addLine(to: CGPoint(x: x + t / 2, y: bottom - t / 2 - g))
                p.addLine(to: CGPoint(x: x, y: bottom - g))
                p.addLine(to: CGPoint(x: x - t / 2, y: bottom - t / 2 - g))
                p.addLine(to: CGPoint(x: x - t / 2, y: top + t / 2 + g))
                p.closeSubpath()
            }
        }
        let left = t / 2, right = w - t / 2
        return [
            horizontal(t / 2),
            vertical(right, t / 2, mid),
            vertical(right, mid, h - t / 2),
            horizontal(h - t / 2),
            vertical(left, mid, h - t / 2),
            vertical(left, t / 2, mid),
            horizontal(mid),
        ]
    }
}
