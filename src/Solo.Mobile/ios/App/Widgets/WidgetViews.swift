import SwiftUI
import WidgetKit

/// Every widget's frame: its title in the theme's heading style, its content,
/// and the refresh smile in the bottom-right corner, as in the sketch.
struct WidgetFrame<Content: View>: View {
    let title: String
    let entry: WorkEntry
    @ViewBuilder var content: (WorkSummary) -> Content

    var body: some View {
        ZStack(alignment: .bottomTrailing) {
            VStack(alignment: .leading, spacing: 6) {
                Text(title)
                    .font(WidgetTheme.mono(15, bold: true))
                    .foregroundStyle(WidgetTheme.primary)
                    .lineLimit(1)
                    .minimumScaleFactor(0.7)
                switch entry.state {
                case .work(let summary):
                    content(summary)
                case .noInstance:
                    EmptyLine(text: "Add an instance in Solo first.")
                case .signedOut:
                    EmptyLine(text: "Sign in to Solo to see your work.")
                case .unreachable:
                    EmptyLine(text: "Can't reach your instance.")
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            RefreshSmile()
                .padding(.trailing, -6)
                .padding(.bottom, -6)
        }
        .containerBackground(WidgetTheme.background, for: .widget)
    }
}

private struct EmptyLine: View {
    let text: String

    var body: some View {
        Text(text)
            .font(WidgetTheme.mono(12))
            .foregroundStyle(WidgetTheme.muted)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }
}

extension WorkEntry {
    /// The Your Tasks page on the entry's instance.
    var yourWork: URL? { instance.map { WidgetLink.url(instance: $0, path: "") } }
}

// MARK: Assigned (small)

struct AssignedCountView: View {
    let entry: WorkEntry

    var body: some View {
        WidgetFrame(title: "ASSIGNED", entry: entry) { summary in
            let count = summary.unstarted + summary.inProgress
            // The digits as large as fit beside the title's width and above the
            // smile's line, so neither the plus over 99 nor a small widget on
            // the smallest iPhone runs them into the smile.
            GeometryReader { geometry in
                let across = count > 99 ? SevenSegmentNumber.widthOverNinetyNine : SevenSegmentNumber.width
                let digit = min(42, geometry.size.width / across, (geometry.size.height - 20) / 1.8)
                SevenSegmentNumber(value: count, digitWidth: digit)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            }
        }
        .widgetURL(entry.yourWork)
    }
}

// MARK: Assigned work (medium)

struct MiniBoardView: View {
    let entry: WorkEntry

    var body: some View {
        WidgetFrame(title: "ASSIGNED WORK", entry: entry) { summary in
            let today = entry.date
            HStack(alignment: .top, spacing: 0) {
                Column(title: "NEW", color: WidgetTheme.new, total: summary.unstarted,
                       filled: Self.squares(summary.open.filter { !$0.isStarted }, summary.unstarted, today))
                Divider().overlay(WidgetTheme.divider)
                Column(title: "IN PROGRESS", color: WidgetTheme.started, total: summary.inProgress,
                       filled: Self.squares(summary.open.filter(\.isStarted), summary.inProgress, today))
                Divider().overlay(WidgetTheme.divider)
                Column(title: "DONE", color: WidgetTheme.done, total: summary.finished,
                       filled: Array(repeating: false, count: summary.finished))
            }
        }
        .widgetURL(entry.yourWork)
    }

    /// One square per task in the column, `total` of them. The summary lists
    /// at most 100 open tasks across both columns (the server's `WorkSummaryLimits.OpenListed`),
    /// soonest deadline first, so a column can hold more than it lists; the
    /// ones left out come after every listed one, and are drawn last, unfilled.
    static func squares(_ listed: [WorkSummary.Task], _ total: Int, _ today: Date) -> [Bool] {
        let drawn = listed.prefix(total).map { nearDeadline($0, today) }
        return drawn + Array(repeating: false, count: max(0, total - drawn.count))
    }

    /// Due in two days or less, or overdue: drawn filled.
    static func nearDeadline(_ task: WorkSummary.Task, _ today: Date) -> Bool {
        (task.daysLeft(from: today) ?? .max) <= 2
    }

    private struct Column: View {
        let title: String
        let color: Color
        let total: Int
        /// One per square to draw, in order; true draws it filled.
        let filled: [Bool]

        static let side: CGFloat = 11
        private static let gap: CGFloat = 3

        var body: some View {
            VStack(alignment: .leading, spacing: 5) {
                // A header that has to shrink to fit (IN PROGRESS at the largest
                // text sizes) keeps a full line's height, so the three rules
                // under the headers stay level.
                Text(verbatim: "Ag")
                    .font(WidgetTheme.mono(11, bold: true))
                    .hidden()
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .overlay(alignment: .leading) {
                        Text(title)
                            .font(WidgetTheme.mono(11, bold: true))
                            .foregroundStyle(WidgetTheme.muted)
                            .lineLimit(1)
                            .minimumScaleFactor(0.5)
                    }
                Rectangle().fill(WidgetTheme.divider).frame(height: 1)
                GeometryReader { geometry in
                    let perRow = max(1, Int((geometry.size.width + Self.gap) / (Self.side + Self.gap)))
                    let rows = max(1, Int((geometry.size.height + Self.gap) / (Self.side + Self.gap)))
                    let fits = perRow * rows
                    let overflow = max(0, total - fits)
                    let shown = Array(filled.prefix(overflow > 0 ? fits - 1 : fits))
                    LazyVGrid(columns: Array(repeating: GridItem(.fixed(Self.side), spacing: Self.gap), count: perRow),
                              alignment: .leading, spacing: Self.gap) {
                        ForEach(Array(shown.enumerated()), id: \.offset) { _, fill in
                            Square(color: color, filled: fill)
                        }
                        if overflow > 0 {
                            Text("+")
                                .font(WidgetTheme.mono(10, bold: true))
                                .foregroundStyle(color)
                                .frame(width: Self.side, height: Self.side)
                        }
                    }
                }
            }
            .padding(.horizontal, 6)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    private struct Square: View {
        let color: Color
        let filled: Bool

        var body: some View {
            RoundedRectangle(cornerRadius: 1.5)
                .fill(filled ? color : .clear)
                .overlay(RoundedRectangle(cornerRadius: 1.5).strokeBorder(color, lineWidth: 1.5))
                .frame(width: Column.side, height: Column.side)
        }
    }
}

// MARK: Upcoming due (large)

struct UpcomingView: View {
    let entry: WorkEntry
    static let maxRows = 7
    /// One row, its divider included: a 10pt serial over a 14pt title. Fixed:
    /// a widget caps its text well short of the largest sizes, while a
    /// @ScaledMetric scales all the way and left room for two rows there.
    static let rowHeight: CGFloat = 38
    /// The bottom line, shared with the smile: the smile's visible pixels
    /// (`PixelSmile` at scale 2) sit in the bottom 18pt of the content area.
    static let footerHeight: CGFloat = 18

    var body: some View {
        WidgetFrame(title: "UPCOMING DUE", entry: entry) { summary in
            let due = summary.open.compactMap { task in task.daysLeft(from: entry.date).map { (task, $0) } }
                .filter { $0.1 <= 7 }
            if due.isEmpty {
                Text("Nothing due in the next 7 days.")
                    .font(WidgetTheme.mono(12))
                    .foregroundStyle(WidgetTheme.muted)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
            } else {
                // As many rows as the widget has room for, up to seven, above
                // the smile's line, which "+N more" shares when some don't fit.
                GeometryReader { geometry in
                    let room = geometry.size.height - 1 - Self.footerHeight
                    let fits = max(1, min(Self.maxRows, Int(room / Self.rowHeight)))
                    let shown = Array(due.prefix(fits))
                    VStack(alignment: .leading, spacing: 0) {
                        Rectangle().fill(WidgetTheme.divider).frame(height: 1)
                        ForEach(shown, id: \.0.id) { task, days in
                            row(task, days)
                                .frame(height: Self.rowHeight - 1)
                            Rectangle().fill(WidgetTheme.divider).frame(height: 1)
                        }
                        Spacer(minLength: 0)
                        if due.count > shown.count {
                            moreLink(due.count - shown.count)
                        }
                    }
                }
            }
        }
    }

    @ViewBuilder
    private func row(_ task: WorkSummary.Task, _ days: Int) -> some View {
        let content = HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text(task.serial)
                    .font(WidgetTheme.mono(10))
                    .foregroundStyle(WidgetTheme.muted)
                Text(task.name)
                    .font(WidgetTheme.mono(14, bold: true))
                    .foregroundStyle(WidgetTheme.text)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            DaysRing(days: days)
        }
        .padding(.trailing, 4)
        if let instance = entry.instance {
            Link(destination: WidgetLink.url(instance: instance, path: task.path)) { content }
        } else {
            content
        }
    }

    @ViewBuilder
    private func moreLink(_ count: Int) -> some View {
        let label = Text("+\(count) more")
            .font(WidgetTheme.mono(12, bold: true))
            .foregroundStyle(WidgetTheme.primary)
            .frame(height: Self.footerHeight)
        if let url = entry.yourWork {
            Link(destination: url) { label }
        } else {
            label
        }
    }
}

/// Days left in a ring that empties over the last week: the theme's colour
/// with more than two days to go, the warning colour at two or less, and a full
/// ring in the error colour with "!" once overdue.
struct DaysRing: View {
    let days: Int

    var body: some View {
        let overdue = days < 0
        let color = overdue ? WidgetTheme.new : days <= 2 ? WidgetTheme.started : WidgetTheme.primary
        let fraction = overdue ? 1 : CGFloat(min(days, 7)) / 7
        ZStack {
            Circle().stroke(WidgetTheme.faint.opacity(0.35), lineWidth: 3)
            Circle()
                .trim(from: 0, to: max(fraction, 0.04))
                .stroke(color, style: StrokeStyle(lineWidth: 3, lineCap: .butt))
                .rotationEffect(.degrees(-90))
            Text(overdue ? "!" : "\(days)")
                .font(WidgetTheme.mono(13, bold: true))
                .foregroundStyle(color)
        }
        .frame(width: 30, height: 30)
        .accessibilityLabel(overdue ? "Overdue" : days == 1 ? "1 day left" : "\(days) days left")
    }
}
