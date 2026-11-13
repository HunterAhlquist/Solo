import SwiftUI

/// Two digits on a seven-segment display, unlit segments faintly showing as on
/// a real one, and the zero slashed as in a terminal font. Over 99 it shows 99
/// with a plus at the first digit's top-right corner, drawn as two more
/// segments, and the digits part a little to make room for it.
struct SevenSegmentNumber: View {
    var value: Int
    var digitWidth: CGFloat = 44
    var color: Color = WidgetTheme.primary

    /// The display's width in digit widths, for sizing it to the room it has.
    static let width: CGFloat = 2 + gapRatio
    static let widthOverNinetyNine: CGFloat = 2 + gapRatio + plusRatio * 0.8
    private static let gapRatio: CGFloat = 0.22
    private static let plusRatio: CGFloat = 0.36

    var body: some View {
        let clamped = max(0, min(99, value))
        let over = value > 99
        let plus = digitWidth * Self.plusRatio
        let gap = digitWidth * Self.gapRatio
        HStack(alignment: .top, spacing: over ? gap + plus * 0.8 : gap) {
            SevenSegmentDigit(digit: clamped / 10, color: color)
                .frame(width: digitWidth, height: digitWidth * 1.8)
                .overlay(alignment: .topTrailing) {
                    if over {
                        SevenSegmentPlus(color: color)
                            .frame(width: plus, height: plus)
                            .offset(x: plus + gap * 0.35)
                    }
                }
            SevenSegmentDigit(digit: clamped % 10, color: color)
                .frame(width: digitWidth, height: digitWidth * 1.8)
        }
        .accessibilityElement()
        .accessibilityLabel(over ? "More than 99" : "\(clamped)")
    }
}

/// A plus made of two segments, the display's own bars.
struct SevenSegmentPlus: View {
    var color: Color

    var body: some View {
        Canvas { context, size in
            let s = min(size.width, size.height), t = s * 0.3, mid = s / 2
            let across = Path { p in
                p.move(to: CGPoint(x: 0, y: mid))
                p.addLine(to: CGPoint(x: t / 2, y: mid - t / 2))
                p.addLine(to: CGPoint(x: s - t / 2, y: mid - t / 2))
                p.addLine(to: CGPoint(x: s, y: mid))
                p.addLine(to: CGPoint(x: s - t / 2, y: mid + t / 2))
                p.addLine(to: CGPoint(x: t / 2, y: mid + t / 2))
                p.closeSubpath()
            }
            context.fill(across, with: .color(color))
            context.fill(across.applying(CGAffineTransform(translationX: mid, y: mid).rotated(by: .pi / 2)
                .translatedBy(x: -mid, y: -mid)), with: .color(color))
        }
        .accessibilityHidden(true)
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
