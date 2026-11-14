import SwiftUI

/// Where a card on Your Tasks came from: the way back to its board.
struct TaskOrigin: Hashable {
    var projectName: String
    var boardName: String
    var route: Route
    /// "Group • Epic", or just the epic, when the task sits under one.
    var epicLine: String?
}

/// A card is: name, label, deadline countdown, assignees, sub-task progress and
/// presence. It flashes its outline once when a remote change lands on it.
struct TaskCardView: View {
    @Environment(\.theme) private var theme
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator

    var task: TaskItem
    var projectId: UUID
    var label: SoloLabel?
    var assignees: [ProjectMember] = []
    var presence: [PresenceEntry] = []
    var pulse = false
    var origin: TaskOrigin?
    var canWrite = false
    /// The origin tab's measured size, which the card's outline is cut around.
    @State private var tab = CGSize.zero

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let origin {
                // The tab is a navigation, never a detail open.
                Button {
                    navigator.open(origin.route)
                } label: {
                    HStack(spacing: 4) {
                        Text(origin.projectName).lineLimit(1)
                        Text("▪").foregroundStyle(theme.textDisabled)
                        Text(origin.boardName).lineLimit(1)
                    }
                    .font(.caption2.weight(.medium))
                    .foregroundStyle(theme.textSecondary)
                    .padding(.horizontal, 10)
                    .padding(.top, 5)
                    .padding(.bottom, 3)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .onGeometryChange(for: CGSize.self) { $0.size } action: { tab = $0 }
                .accessibilityLabel("Open \(origin.boardName) in \(origin.projectName)")
            }

            VStack(alignment: .leading, spacing: 8) {
                if let epicLine = origin?.epicLine {
                    Text(epicLine)
                        .font(.caption2)
                        .foregroundStyle(theme.textSecondary)
                        .lineLimit(1)
                }

                HStack(alignment: .top, spacing: 6) {
                    Text(task.name)
                        .font(.subheadline)
                        .foregroundStyle(isFinished ? theme.textSecondary : theme.textPrimary)
                        .strikethrough(isFinished, color: theme.textDisabled)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, alignment: .leading)
                    if let label {
                        LabelChip(label: label, compact: true)
                    }
                }

                if hasFooter {
                    HStack(spacing: 8) {
                        if showDeadline {
                            deadlineRing
                        }
                        Spacer(minLength: 0)
                        if !task.subTasks.isEmpty {
                            HStack(spacing: 4) {
                                ProgressRing(value: Double(task.finishedSubTasks) / Double(task.subTasks.count),
                                             color: task.finishedSubTasks == task.subTasks.count ? theme.success : theme.primary,
                                             size: 16, lineWidth: 2.5)
                                Text("\(task.finishedSubTasks)/\(task.subTasks.count)")
                                    .font(.caption2.monospacedDigit())
                                    .foregroundStyle(theme.textSecondary)
                            }
                            .accessibilityElement(children: .ignore)
                            .accessibilityLabel("\(task.finishedSubTasks) of \(task.subTasks.count) sub-tasks done")
                        }
                        PresenceCluster(entries: presence, max: 2, size: 20)
                        if !assignees.isEmpty {
                            AvatarStack(members: assignees, max: 3, size: 22)
                        }
                    }
                }
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        // One outline around the tab and the card together: drawn apart, the card's top edge ran
        // under the tab and split the two.
        .background(theme.surface, in: cardShape)
        .overlay(cardShape.strokeBorder(outline, lineWidth: pulse || showDeadline ? 1.5 : 1))
        .opacity(isFinished ? 0.75 : 1)
        .contentShape(.dragPreview, cardShape)
        .animation(.easeOut(duration: 0.6), value: pulse)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isButton)
    }

    private var cardShape: TabbedCardShape {
        TabbedCardShape(tab: origin == nil ? .zero : tab)
    }

    private var outline: Color {
        if pulse { return theme.primary }
        if showDeadline { return deadlineColor.opacity(0.85) }
        return theme.divider
    }

    private var hasFooter: Bool {
        showDeadline || !task.subTasks.isEmpty || !presence.isEmpty || !assignees.isEmpty
    }

    private var isFinished: Bool { task.status == .finished }

    // MARK: Deadline countdown
    // The ring drains as the start→deadline window burns down, and its colour —
    // shared with the card outline — slides green to red over the same window.
    // The start is the task's own when it has one, and its creation otherwise.

    private var showDeadline: Bool {
        guard task.deadlineAt != nil, !isFinished else { return false }
        if let start = task.startAt, Day.today < start { return false }
        return true
    }

    private var daysLeft: Int { Day.today.days(until: task.deadlineAt ?? .today) }

    private var elapsed: Double {
        guard let deadline = task.deadlineAt else { return 0 }
        let start = task.startAt?.date() ?? task.createdAt
        let end = deadline.adding(days: 1).date()
        let total = end.timeIntervalSince(start)
        return total <= 0 ? 1 : min(max(Date().timeIntervalSince(start) / total, 0), 1)
    }

    private var deadlineColor: Color { Theme.deadlineColor(elapsed: elapsed) }

    private var deadlineRing: some View {
        ZStack {
            ProgressRing(value: daysLeft < 0 ? 1 : 1 - elapsed, color: deadlineColor, size: 22, lineWidth: 2.5)
            if max(daysLeft, 0) <= 99 {
                Text("\(max(daysLeft, 0))")
                    .font(.system(size: 9, weight: .bold, design: .rounded).monospacedDigit())
                    .foregroundStyle(deadlineColor)
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(deadlineDescription)
    }

    private var deadlineDescription: String {
        guard let deadline = task.deadlineAt else { return "" }
        let when: String = switch daysLeft {
        case ..<(-1): "overdue by \(-daysLeft) days"
        case -1: "overdue by 1 day"
        case 0: "due today"
        case 1: "1 day left"
        default: "\(daysLeft) days left"
        }
        let due = "Due \(deadline.formatted) — \(when)"
        return task.startAt.map { "Starts \($0.formatted). \(due)" } ?? due
    }
}

/// A card's outline: a rounded rectangle, with a tab standing on its top leading corner when the
/// card says where it came from. The tab and the card are one path, so one fill and one stroke
/// cover both and no edge runs between them.
nonisolated struct TabbedCardShape: InsettableShape {
    var tab: CGSize
    var radius: CGFloat = 12
    var tabRadius: CGFloat = 9
    var inset: CGFloat = 0

    func path(in rect: CGRect) -> Path {
        let rect = rect.insetBy(dx: inset, dy: inset)
        let radius = max(radius - inset, 0)
        // No tab, or one as wide as the card: a plain rounded rectangle.
        guard tab.height > 0, tab.width > 0, tab.width + radius * 2 < rect.width else {
            return Path(roundedRect: rect, cornerRadius: radius, style: .continuous)
        }
        let top = rect.minY, shoulder = rect.minY + tab.height
        let tabRight = rect.minX + tab.width - inset * 2
        let corner = min(max(tabRadius - inset, 0), tab.height / 2)
        // Where the tab's side meets the card's top, the corner turns the other way.
        let fillet = min(6, tab.height / 2)

        var path = Path()
        path.move(to: CGPoint(x: rect.minX, y: top + corner))
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: top), tangent2End: CGPoint(x: rect.minX + corner, y: top), radius: corner)
        path.addArc(tangent1End: CGPoint(x: tabRight, y: top), tangent2End: CGPoint(x: tabRight, y: top + corner), radius: corner)
        path.addArc(tangent1End: CGPoint(x: tabRight, y: shoulder), tangent2End: CGPoint(x: tabRight + fillet, y: shoulder), radius: fillet)
        path.addArc(tangent1End: CGPoint(x: rect.maxX, y: shoulder), tangent2End: CGPoint(x: rect.maxX, y: shoulder + radius), radius: radius)
        path.addArc(tangent1End: CGPoint(x: rect.maxX, y: rect.maxY), tangent2End: CGPoint(x: rect.maxX - radius, y: rect.maxY), radius: radius)
        path.addArc(tangent1End: CGPoint(x: rect.minX, y: rect.maxY), tangent2End: CGPoint(x: rect.minX, y: rect.maxY - radius), radius: radius)
        path.closeSubpath()
        return path
    }

    func inset(by amount: CGFloat) -> TabbedCardShape {
        var shape = self
        shape.inset += amount
        return shape
    }
}

/// What a column shows when a Finished zone has tucked cards away: past 25
/// the overflow folds behind this, reachable but out of the way.
struct ShowAllButton: View {
    @Environment(\.theme) private var theme
    var hidden: Int
    var action: () -> Void

    var body: some View {
        Button(action: action) {
            Text("Show all \(hidden) more")
                .font(.caption.weight(.medium))
                .frame(maxWidth: .infinity)
                .padding(.vertical, 8)
                .foregroundStyle(theme.textSecondary)
                .background(theme.surface.opacity(0.5), in: RoundedRectangle(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }
}

/// What a dragged card carries: its serial number, which another app reads as
/// text and this one resolves back to the task.
enum BoardDrag {
    static let epicPrefix = "solo-epic:"
    static let subTaskPrefix = "solo-subtask:"

    static func epic(_ id: UUID) -> String { epicPrefix + id.api }
    static func subTask(_ id: UUID) -> String { subTaskPrefix + id.api }

    static func id(_ payload: String, prefix: String) -> UUID? {
        payload.hasPrefix(prefix) ? UUID(uuidString: String(payload.dropFirst(prefix.count))) : nil
    }

    static let finishedVisibleLimit = 25
}
