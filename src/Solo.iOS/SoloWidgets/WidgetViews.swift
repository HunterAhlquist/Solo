import SwiftUI
import WidgetKit

// MARK: - Shared pieces

/// The instance's accent, light or dark as the widget is. System blue where the theme has none.
struct WidgetAccent {
    var instance: WidgetInstance?

    var color: Color {
        let light = Self.parse(instance?.accentLight), dark = Self.parse(instance?.accentDark)
        guard light != nil || dark != nil else { return .blue }
        return Color(uiColor: UIColor { traits in
            (traits.userInterfaceStyle == .dark ? dark ?? light : light ?? dark) ?? .systemBlue
        })
    }

    private static func parse(_ hex: String?) -> UIColor? {
        guard var text = hex?.trimmingCharacters(in: .whitespaces), text.hasPrefix("#") else { return nil }
        text.removeFirst()
        if text.count == 3 { text = text.map { "\($0)\($0)" }.joined() }
        guard text.count >= 6, let value = UInt32(text.prefix(6), radix: 16) else { return nil }
        return UIColor(red: CGFloat(value >> 16 & 0xFF) / 255, green: CGFloat(value >> 8 & 0xFF) / 255,
                       blue: CGFloat(value & 0xFF) / 255, alpha: 1)
    }
}

/// Every widget's frame: what it is, a way to ask again, and its content — or, without any, why not.
struct WidgetFrame<Content: View>: View {
    let title: String
    let symbol: String
    let entry: WorkEntry
    @ViewBuilder var content: (WorkSummary, Color) -> Content

    var body: some View {
        let accent = WidgetAccent(instance: entry.instance).color
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 5) {
                Image(systemName: symbol)
                Text(title).lineLimit(1).minimumScaleFactor(0.7)
                Spacer(minLength: 4)
                Button(intent: RefreshIntent()) {
                    Image(systemName: "arrow.clockwise")
                        .font(.caption2.weight(.bold))
                        .foregroundStyle(.secondary)
                        .frame(width: 22, height: 22)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel("Refresh")
            }
            .font(.caption.weight(.semibold))
            .foregroundStyle(accent)
            .widgetAccentable()

            switch entry.state {
            case .work(let summary):
                content(summary, accent)
            case .signedOut:
                Notice(text: "Sign in to Solo to see your work.", symbol: "person.crop.circle.badge.questionmark")
            case .unreachable:
                Notice(text: "Can't reach your instance.", symbol: "icloud.slash")
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .containerBackground(for: .widget) {
            // The page's own wash: the system's background with a breath of the accent across it.
            ZStack {
                Color(uiColor: .systemBackground)
                LinearGradient(colors: [accent.opacity(0.16), accent.opacity(0.04), .clear], startPoint: .topLeading, endPoint: .bottom)
            }
        }
    }
}

private struct Notice: View {
    let text: String
    let symbol: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Image(systemName: symbol).font(.title3).foregroundStyle(.secondary)
            Text(text).font(.footnote).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}

// MARK: - Assigned (small)

struct AssignedCountView: View {
    let entry: WorkEntry

    var body: some View {
        WidgetFrame(title: "Assigned", symbol: "person.crop.rectangle.stack", entry: entry) { summary, accent in
            VStack(alignment: .leading, spacing: 2) {
                Text("\(summary.unstarted + summary.inProgress)")
                    .font(.system(size: 62, weight: .bold, design: .rounded))
                    .minimumScaleFactor(0.4)
                    .lineLimit(1)
                    .contentTransition(.numericText())
                    .foregroundStyle(.primary)
                Spacer(minLength: 0)
                // The two columns the number is made of.
                HStack(spacing: 10) {
                    Label("\(summary.unstarted)", systemImage: "circle.dashed")
                        .accessibilityLabel("\(summary.unstarted) new")
                    Label("\(summary.inProgress)", systemImage: "circle.lefthalf.filled")
                        .accessibilityLabel("\(summary.inProgress) in progress")
                }
                .font(.caption.weight(.medium).monospacedDigit())
                .foregroundStyle(.secondary)
                .labelStyle(.titleAndIcon)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .widgetURL(WidgetLink.yourTasks)
    }
}

// MARK: - Assigned work (medium)

struct MiniBoardView: View {
    let entry: WorkEntry

    var body: some View {
        WidgetFrame(title: "Assigned Work", symbol: "rectangle.split.3x1", entry: entry) { summary, accent in
            let today = entry.date
            HStack(alignment: .top, spacing: 10) {
                Column(title: "New", symbol: "circle.dashed", color: accent, total: summary.unstarted,
                       filled: Self.squares(summary.open.filter { !$0.started }, summary.unstarted, today))
                Column(title: "In Progress", symbol: "circle.lefthalf.filled", color: .orange, total: summary.inProgress,
                       filled: Self.squares(summary.open.filter(\.started), summary.inProgress, today))
                Column(title: "Done", symbol: "checkmark.circle.fill", color: .green, total: summary.finished,
                       filled: Array(repeating: false, count: summary.finished))
            }
        }
        .widgetURL(WidgetLink.yourTasks)
    }

    /// One square per task in the column, `total` of them. The summary lists only so many
    /// open tasks, soonest deadline first, so a column can hold more than it lists; the ones
    /// left out come after every listed one and are drawn last, unfilled.
    static func squares(_ listed: [WorkSummary.Item], _ total: Int, _ today: Date) -> [Bool] {
        let drawn = listed.prefix(total).map { nearDeadline($0, today) }
        return drawn + Array(repeating: false, count: max(0, total - drawn.count))
    }

    /// Due in two days or less, or overdue: drawn filled.
    static func nearDeadline(_ task: WorkSummary.Item, _ today: Date) -> Bool {
        (task.daysLeft(from: today) ?? .max) <= 2
    }

    private struct Column: View {
        let title: String
        let symbol: String
        let color: Color
        let total: Int
        /// One per square to draw, in order; true draws it filled.
        let filled: [Bool]

        static let side: CGFloat = 11
        private static let gap: CGFloat = 3

        var body: some View {
            VStack(alignment: .leading, spacing: 5) {
                HStack(spacing: 4) {
                    Text(title).lineLimit(1).minimumScaleFactor(0.6)
                    Spacer(minLength: 0)
                    Text("\(total)").monospacedDigit().foregroundStyle(.primary)
                }
                .font(.caption2.weight(.semibold))
                .foregroundStyle(.secondary)
                Capsule().fill(color.opacity(0.55)).frame(height: 2)
                GeometryReader { geometry in
                    let perRow = max(1, Int((geometry.size.width + Self.gap) / (Self.side + Self.gap)))
                    let rows = max(1, Int((geometry.size.height + Self.gap) / (Self.side + Self.gap)))
                    let fits = perRow * rows
                    let overflow = max(0, total - fits)
                    let shown = Array(filled.prefix(overflow > 0 ? fits - 1 : fits))
                    LazyVGrid(columns: Array(repeating: GridItem(.fixed(Self.side), spacing: Self.gap), count: perRow),
                              alignment: .leading, spacing: Self.gap) {
                        ForEach(Array(shown.enumerated()), id: \.offset) { _, fill in
                            RoundedRectangle(cornerRadius: 3, style: .continuous)
                                .fill(fill ? color : color.opacity(0.18))
                                .overlay(RoundedRectangle(cornerRadius: 3, style: .continuous).strokeBorder(color.opacity(fill ? 0 : 0.7), lineWidth: 1))
                                .frame(width: Self.side, height: Self.side)
                        }
                        if overflow > 0 {
                            Image(systemName: "plus")
                                .font(.system(size: 9, weight: .bold))
                                .foregroundStyle(color)
                                .frame(width: Self.side, height: Self.side)
                        }
                    }
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel("\(title): \(total), \(filled.count { $0 }) due soon")
        }
    }
}

// MARK: - Upcoming due (large)

struct UpcomingView: View {
    let entry: WorkEntry
    static let maxRows = 7
    static let rowHeight: CGFloat = 40
    static let footerHeight: CGFloat = 18

    var body: some View {
        WidgetFrame(title: "Upcoming Due", symbol: "calendar.badge.clock", entry: entry) { summary, accent in
            let due = summary.open.compactMap { task in task.daysLeft(from: entry.date).map { (task, $0) } }
                .filter { $0.1 <= 7 }
            if due.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    Image(systemName: "checkmark.circle").font(.title2).foregroundStyle(.secondary)
                    Text("Nothing due in the next 7 days.").font(.footnote).foregroundStyle(.secondary)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            } else {
                // As many rows as there is room for, up to seven; "+N more" takes the last line when some don't fit.
                GeometryReader { geometry in
                    let fits = max(1, min(Self.maxRows, Int((geometry.size.height - Self.footerHeight) / Self.rowHeight)))
                    let shown = Array(due.prefix(fits))
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(shown.enumerated()), id: \.element.0.id) { index, pair in
                            if index > 0 { Divider() }
                            Link(destination: WidgetLink.task(pair.0.serial)) {
                                row(pair.0, pair.1, accent)
                            }
                            .frame(height: Self.rowHeight - 1)
                        }
                        Spacer(minLength: 0)
                        if due.count > shown.count {
                            Link(destination: WidgetLink.yourTasks) {
                                Text("+\(due.count - shown.count) more")
                                    .font(.caption.weight(.semibold))
                                    .foregroundStyle(accent)
                                    .frame(height: Self.footerHeight)
                            }
                        }
                    }
                }
            }
        }
        .widgetURL(WidgetLink.yourTasks)
    }

    private func row(_ task: WorkSummary.Item, _ days: Int, _ accent: Color) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text(task.serial)
                    .font(.caption2.monospaced())
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                Text(task.name)
                    .font(.subheadline.weight(.medium))
                    .foregroundStyle(.primary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            DaysRing(days: days, accent: accent)
        }
        .contentShape(.rect)
    }
}

/// Days left in a ring that empties over the last week: the accent with more than two days to
/// go, orange at two or less, and a full red ring with "!" once overdue.
struct DaysRing: View {
    let days: Int
    let accent: Color

    var body: some View {
        let overdue = days < 0
        let color: Color = overdue ? .red : days <= 2 ? .orange : accent
        let fraction = overdue ? 1 : CGFloat(min(days, 7)) / 7
        ZStack {
            Circle().stroke(.quaternary, lineWidth: 3)
            Circle()
                .trim(from: 0, to: max(fraction, 0.04))
                .stroke(color, style: StrokeStyle(lineWidth: 3, lineCap: .round))
                .rotationEffect(.degrees(-90))
            Text(overdue ? "!" : "\(days)")
                .font(.system(size: 12, weight: .bold, design: .rounded).monospacedDigit())
                .foregroundStyle(color)
        }
        .frame(width: 28, height: 28)
        .accessibilityLabel(overdue ? "Overdue" : days == 0 ? "Due today" : days == 1 ? "1 day left" : "\(days) days left")
    }
}
