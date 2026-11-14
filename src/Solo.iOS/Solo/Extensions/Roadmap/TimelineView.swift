import SwiftUI

/// A roadmap's grid: one column per day, scrolled sideways between the
/// instance's bounds, with each task a bar on its row.
///
/// Touch works the way Calendar's does. A tap opens the task. A press and hold
/// picks a bar up: dragged sideways it moves its dates, dragged up or down it
/// changes row, and once picked up it stays selected with a handle on each end
/// to stretch it by. More bars join the selection with a tap. A press and hold
/// on an empty day offers to add a task or a milestone there.
struct TimelineView: View {
    @Environment(\.theme) private var theme
    @Bindable var store: RoadmapStore

    var onOpen: (RoadmapEntry) -> Void
    var onAddTask: (Day, UUID?) -> Void
    var onAddMilestone: (Day) -> Void
    var onEditMilestone: (RoadmapMilestone) -> Void
    /// Bumped by the page to bring a day into view.
    var scrollRequest: ScrollRequest?

    struct ScrollRequest: Equatable {
        var day: Day
        var token = UUID()
    }

    static let dayWidth: CGFloat = 40
    static let rowHeight: CGFloat = 44
    private static let monthHeight: CGFloat = 26
    private static let dayHeight: CGFloat = 34
    private static var headHeight: CGFloat { monthHeight + dayHeight }

    @State private var scrollX: CGFloat = 0
    @State private var viewport: CGFloat = 0
    @State private var position = ScrollPosition(x: 0)
    @State private var drag: BarDrag?
    @State private var milestoneDrag: (id: UUID, days: Int)?
    @State private var dayMenu: DayTarget?
    @State private var didOpen = false

    private struct BarDrag: Equatable {
        enum Kind: Equatable { case undecided, move, row, resize(BarEdge) }
        var entryId: UUID
        var kind: Kind
        var translation: CGSize = .zero
        var days = 0
        var target: RowTarget?
    }

    private enum RowTarget: Equatable {
        case join(row: Int)
        case insert(before: Int)
    }

    private struct DayTarget: Identifiable {
        let id = UUID()
        var day: Day
        var beside: UUID?
        var point: CGPoint
    }

    var body: some View {
        let layout = store.layout
        let window = store.window
        // A person's own roadmap sits above their tasks, so it takes less room than a project's — but
        // enough that a handful of overlapping tasks show without scrolling the page to find them.
        let lineCount = store.isPersonal
            ? max(6, layout.lines.count + TimelineRules.spareRows) : TimelineRules.rows(forLines: layout.lines.count)
        let height = Self.headHeight + CGFloat(lineCount) * Self.rowHeight
        let width = CGFloat(window.days) * Self.dayWidth
        let first = max(Int(scrollX / Self.dayWidth) - 2, 0)
        let last = min(Int((scrollX + max(viewport, 1)) / Self.dayWidth) + 2, window.days - 1)

        ScrollView(.horizontal) {
            ZStack(alignment: .topLeading) {
                grid(first: first, last: last, lineCount: lineCount)
                    .frame(width: CGFloat(last - first + 1) * Self.dayWidth, height: height)
                    .offset(x: CGFloat(first) * Self.dayWidth)

                // The empty days: where a press and hold adds something, and a tap clears the selection.
                Color.clear
                    .contentShape(.rect)
                    .frame(width: width, height: height - Self.headHeight)
                    .offset(y: Self.headHeight)
                    .onTapGesture { store.selected = [] }
                    .gesture(emptyPress(layout: layout))

                rowIndicator(layout: layout, width: width)

                ForEach(store.milestones.filter { $0.day >= window.first && $0.day <= window.last }) { milestone in
                    milestoneView(milestone, height: height)
                }

                ForEach(store.entries) { entry in
                    let place = store.place(entry)
                    if let line = layout.line(of: entry.id), place.startIndex <= last, place.endIndex >= first {
                        bar(entry, place: place, line: line, first: first, last: last)
                    }
                }

                if store.entries.isEmpty {
                    Text(emptyText)
                        .font(.subheadline)
                        .foregroundStyle(theme.textSecondary)
                        .frame(width: min(max(viewport - 48, 200), 420), alignment: .leading)
                        .offset(x: scrollX + 24, y: Self.headHeight + 20)
                        .allowsHitTesting(false)
                }

            }
            .frame(width: width, height: height, alignment: .topLeading)
            // Anchored to the pressed day itself. Hung on a view of its own the anchor was that view's
            // frame, and a positioned view's frame is the whole grid — years of days wide.
            .popover(item: $dayMenu, attachmentAnchor: .rect(.rect(dayMenuAnchor))) { target in dayMenuContent(target) }
        }
        .scrollPosition($position)
        .onScrollGeometryChange(for: [CGFloat].self) { [$0.contentOffset.x, $0.containerSize.width] } action: { _, new in
            scrollX = new[0]
            viewport = new[1]
        }
        .frame(height: height)
        .glassEffect(.regular, in: theme.cardShape)
        .clipShape(theme.cardShape)
        .sensoryFeedback(.selection, trigger: store.selected)
        .sensoryFeedback(.impact(weight: .light), trigger: drag?.days)
        .onChange(of: store.timeline?.id, initial: true) { _, id in
            // Opens on the oldest work in flight, a day's margin to its left.
            guard id != nil, !didOpen else { return }
            didOpen = true
            scroll(to: store.startingDay, animated: false)
        }
        .onChange(of: scrollRequest) { _, request in
            if let request { scroll(to: request.day, animated: true) }
        }
    }

    private var emptyText: String {
        store.isPersonal
            ? "Nothing on your roadmap yet. Use Add Task, or press and hold a day in the grid, to put one of your tasks on it."
            : store.canWrite
                ? "No tasks on this roadmap yet. Press and hold a day in the grid, or use Add Task, to put one on it."
                : "No tasks on this roadmap yet."
    }

    private func scroll(to day: Day, animated: Bool) {
        let x = max(CGFloat(store.window.index(of: day) - 1) * Self.dayWidth, 0)
        if animated {
            withAnimation(.smooth) { position.scrollTo(x: x) }
        } else {
            position.scrollTo(x: x)
        }
    }

    // MARK: Grid

    /// The month band, the day band and the columns under them, drawn only for
    /// the days in view: a year or three of days is far too wide to lay out.
    private func grid(first: Int, last: Int, lineCount: Int) -> some View {
        let window = store.window
        let settings = store.settings
        let today = store.today
        let accent = theme.primary
        let secondary = theme.textSecondary
        let disabled = theme.textDisabled
        let hairline = theme.divider.opacity(0.5)
        let visibleLeft = scrollX - CGFloat(first) * Self.dayWidth

        return Canvas { context, size in
            for index in first...max(last, first) {
                let day = window.day(at: index)
                let x = CGFloat(index - first) * Self.dayWidth
                let column = CGRect(x: x, y: Self.monthHeight, width: Self.dayWidth, height: size.height - Self.monthHeight)

                // Sprints as alternating stripes, continuous across years.
                if RoadmapSprints.band(day, settings: settings) & 1 == 1 {
                    context.fill(Path(column), with: .color(.primary.opacity(0.045)))
                }
                if day == today {
                    context.fill(Path(column), with: .color(accent.opacity(0.16)))
                }
                context.fill(Path(CGRect(x: x, y: Self.monthHeight, width: 0.5, height: size.height)), with: .color(hairline))

                let muted = day.isWeekend
                context.draw(Text("\(day.day)").font(.caption.weight(day == today ? .bold : .medium))
                    .foregroundStyle(day == today ? accent : muted ? disabled : .primary),
                    at: CGPoint(x: x + Self.dayWidth / 2, y: Self.monthHeight + 11))
                context.draw(Text(Self.weekdayLetter(day)).font(.system(size: 9, weight: .medium))
                    .foregroundStyle(day == today ? accent : muted ? disabled : secondary),
                    at: CGPoint(x: x + Self.dayWidth / 2, y: Self.monthHeight + 25))

                if day.day == 1 {
                    context.fill(Path(CGRect(x: x, y: 0, width: 1, height: Self.monthHeight)), with: .color(hairline))
                }
            }

            // Month names stay in view while any of their month is.
            var index = first
            while index <= last {
                let day = window.day(at: index)
                let monthEnd = min(index + (Self.daysInMonth(day) - day.day), last)
                let startX = CGFloat(index - first) * Self.dayWidth
                let endX = CGFloat(monthEnd - first + 1) * Self.dayWidth
                let label = Text(day.date().formatted(.dateTime.month(.wide).year())).font(.caption.weight(.semibold))
                    .foregroundStyle(secondary)
                let resolved = context.resolve(label)
                let labelWidth = resolved.measure(in: size).width
                let x = min(max(startX + 8, visibleLeft + 12), endX - labelWidth - 8)
                context.draw(resolved, at: CGPoint(x: max(x, startX + 4), y: Self.monthHeight / 2), anchor: .leading)
                index = monthEnd + 1
            }

            context.fill(Path(CGRect(x: 0, y: Self.headHeight, width: size.width, height: 0.5)), with: .color(hairline))
            for line in 1..<max(lineCount, 2) {
                context.fill(Path(CGRect(x: 0, y: Self.headHeight + CGFloat(line) * Self.rowHeight, width: size.width, height: 0.5)),
                             with: .color(hairline.opacity(0.5)))
            }
        }
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    private static func weekdayLetter(_ day: Day) -> String {
        ["S", "M", "T", "W", "T", "F", "S"][day.weekday - 1]
    }

    private static func daysInMonth(_ day: Day) -> Int {
        Calendar.current.range(of: .day, in: .month, for: day.date())?.count ?? 30
    }

    // MARK: Bars

    @ViewBuilder
    private func bar(_ entry: RoadmapEntry, place original: BarPlacement, line: Int, first: Int, last: Int) -> some View {
        let dragged = drag.map { active in
            active.entryId == entry.id || (store.selected.contains(active.entryId) && store.selected.contains(entry.id))
        } ?? false
        let place = adjusted(original, entry: entry, dragged: dragged)
        // Only the part in view is laid out; a bar can run for hundreds of days.
        let start = max(place.startIndex, first)
        let end = min(place.endIndex, last)
        let barX = CGFloat(start) * Self.dayWidth + 2
        let barWidth = max(CGFloat(end - start + 1) * Self.dayWidth - 4, 12)
        let lift = dragged && drag?.kind == .row ? drag?.translation.height ?? 0 : 0
        let selected = store.selected.contains(entry.id)

        RoadmapBar(entry: entry, store: store, place: place, selected: selected,
                   labelInset: min(max(scrollX - barX + 6, 0), max(barWidth - 90, 0)), width: barWidth)
            .frame(width: barWidth, height: Self.rowHeight - 10)
            .overlay {
                if selected, store.selected.count == 1, store.canWrite, drag?.kind != .row {
                    handles(entry, place: place, start: start, end: end)
                }
            }
            .offset(x: barX, y: Self.headHeight + CGFloat(line) * Self.rowHeight + 5 + lift)
            .zIndex(dragged ? 10 : selected ? 5 : 1)
            .shadow(color: .black.opacity(dragged ? 0.25 : 0), radius: 10, y: 4)
            .onTapGesture {
                if store.selected.isEmpty || !store.canWrite {
                    onOpen(entry)
                } else if store.selected.contains(entry.id) {
                    store.selected.remove(entry.id)
                } else {
                    store.selected.insert(entry.id)
                }
            }
            .gesture(store.canWrite ? pickUp(entry, line: line) : nil)
            .animation(.snappy(duration: 0.18), value: drag?.days)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(barDescription(entry))
            .accessibilityAddTraits(.isButton)
            .accessibilityAction(named: "Open task") { onOpen(entry) }
    }

    /// Where a bar is drawn while it, or the selection it belongs to, is in hand.
    private func adjusted(_ place: BarPlacement, entry: RoadmapEntry, dragged: Bool) -> BarPlacement {
        guard let drag, dragged else { return place }
        var moved = place
        let limit = store.window.days - 1
        switch drag.kind {
        case .move:
            moved.startIndex = min(max(place.startIndex + drag.days, 0), limit)
            moved.endIndex = min(max(place.endIndex + drag.days, 0), limit)
        case .resize(.start) where drag.entryId == entry.id:
            moved.startIndex = min(max(place.startIndex + drag.days, 0), place.endIndex)
        case .resize(.end) where drag.entryId == entry.id:
            moved.endIndex = max(min(place.endIndex + drag.days, limit), place.startIndex)
        default:
            break
        }
        return moved
    }

    private func barDescription(_ entry: RoadmapEntry) -> String {
        let span = TimelineRules.span(entry)
        let when = TimelineRules.isPill(entry) ? "no deadline yet, added \(RoadmapDates.day(span.start))"
            : RoadmapDates.range(span.start, span.end)
        return "\(store.serial(entry)) \(entry.task.name), \(when)"
    }

    /// Press and hold picks the bar up. The first direction it is then moved in
    /// sets what the drag does, so a move up or down never changes its dates.
    private func pickUp(_ entry: RoadmapEntry, line: Int) -> some Gesture {
        LongPressGesture(minimumDuration: 0.28)
            .sequenced(before: DragGesture(minimumDistance: 0))
            .onChanged { value in
                guard case .second(true, let movement) = value else { return }
                if drag == nil {
                    if !store.selected.contains(entry.id) {
                        store.selected = [entry.id]
                    }
                    store.gesturing = true
                    store.report(editing: true)
                    drag = BarDrag(entryId: entry.id, kind: .undecided)
                }
                guard let movement, var active = drag else { return }
                active.translation = movement.translation
                if active.kind == .undecided, max(abs(movement.translation.width), abs(movement.translation.height)) > 10 {
                    active.kind = abs(movement.translation.width) >= abs(movement.translation.height) ? .move : .row
                }
                switch active.kind {
                case .move:
                    let wanted = Int((movement.translation.width / Self.dayWidth).rounded())
                    active.days = store.group(of: entry).reduce(wanted) { days, moving in
                        let clamped = store.window.clampMove(TimelineRules.span(moving), by: days)
                        return abs(clamped) < abs(days) ? clamped : days
                    }
                case .row:
                    active.target = rowTarget(entry, line: line, dy: movement.translation.height)
                default:
                    break
                }
                drag = active
            }
            .onEnded { _ in
                guard let active = drag else { return }
                drag = nil
                store.gesturing = false
                store.report(editing: false)
                let moving = store.group(of: entry)
                Task {
                    switch active.kind {
                    case .move where active.days != 0:
                        await store.shift(moving, by: active.days)
                    case .row:
                        switch active.target {
                        case .join(let row): await store.dropOnRow(entry, row: row, join: true)
                        case .insert(let before): await store.dropOnRow(entry, row: before, join: false)
                        case nil: break
                        }
                    default:
                        break
                    }
                }
            }
    }

    /// Dropped on the middle of a row with room for it, a bar joins that row;
    /// anywhere else it gets a row of its own between two.
    private func rowTarget(_ entry: RoadmapEntry, line: Int, dy: CGFloat) -> RowTarget? {
        let layout = store.layout
        let position = (CGFloat(line) * Self.rowHeight + Self.rowHeight / 2 + dy) / Self.rowHeight
        let over = Int(position.rounded(.down))
        if position < 0 { return .insert(before: 0) }
        guard layout.lines.indices.contains(over) else { return .insert(before: layout.rows.count) }

        let row = layout.lines[over].row
        let fraction = position - CGFloat(over)
        let moving = store.group(of: entry)
        let movingIds = Set(moving.map(\.id))
        let staying = layout.rows[row].filter { !movingIds.contains($0.id) }
        if (0.25...0.75).contains(fraction), !staying.isEmpty,
           TimelineRules.fits(moving.map(store.place), beside: staying.map(store.place)) {
            return .join(row: row)
        }
        return .insert(before: fraction < 0.5 ? row : row + 1)
    }

    @ViewBuilder
    private func rowIndicator(layout: TimelineLayout, width: CGFloat) -> some View {
        switch drag?.target {
        case .join(let row):
            if let lines = layout.lineRange(ofRow: row) {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(theme.accentWash)
                    .frame(width: width, height: CGFloat(lines.count) * Self.rowHeight)
                    .offset(y: Self.headHeight + CGFloat(lines.lowerBound) * Self.rowHeight)
                    .allowsHitTesting(false)
            }
        case .insert(let before):
            let line = before < layout.rows.count ? layout.lineRange(ofRow: before)?.lowerBound ?? 0 : layout.lines.count
            Rectangle()
                .fill(theme.primary)
                .frame(width: width, height: 2)
                .offset(y: Self.headHeight + CGFloat(line) * Self.rowHeight - 1)
                .allowsHitTesting(false)
        case nil:
            EmptyView()
        }
    }

    /// The two ends of the selected bar, each a grip to stretch it by.
    private func handles(_ entry: RoadmapEntry, place: BarPlacement, start: Int, end: Int) -> some View {
        HStack {
            if start == place.startIndex, !place.clippedStart {
                handle(entry, edge: .start)
            }
            Spacer(minLength: 0)
            if end == place.endIndex, !place.clippedEnd {
                handle(entry, edge: .end)
            }
        }
        .padding(.horizontal, -11)
    }

    private func handle(_ entry: RoadmapEntry, edge: BarEdge) -> some View {
        Circle()
            .fill(.background)
            .overlay(Circle().strokeBorder(theme.primary, lineWidth: 2.5))
            .frame(width: 16, height: 16)
            .padding(10)
            .contentShape(.rect)
            .gesture(
                DragGesture(minimumDistance: 2)
                    .onChanged { value in
                        if drag == nil {
                            store.gesturing = true
                            store.report(editing: true)
                        }
                        drag = BarDrag(entryId: entry.id, kind: .resize(edge), translation: value.translation,
                                       days: Int((value.translation.width / Self.dayWidth).rounded()))
                    }
                    .onEnded { _ in
                        guard let active = drag else { return }
                        drag = nil
                        store.gesturing = false
                        store.report(editing: false)
                        guard active.days != 0 else { return }
                        let span = TimelineRules.span(entry)
                        let day = (edge == .start ? span.start : span.end).adding(days: active.days)
                        Task { await store.resize(entry, edge: edge, to: day) }
                    })
            .accessibilityLabel(edge == .start ? "Start" : "Deadline")
            .accessibilityHint("Drag to change")
    }

    // MARK: Empty days

    private func emptyPress(layout: TimelineLayout) -> some Gesture {
        LongPressGesture(minimumDuration: 0.4)
            .sequenced(before: DragGesture(minimumDistance: 0))
            .onEnded { value in
                guard store.canWrite, case .second(true, let press?) = value else { return }
                let index = Int(press.startLocation.x / Self.dayWidth)
                let line = Int(press.startLocation.y / Self.rowHeight)
                dayMenu = DayTarget(day: store.window.day(at: min(max(index, 0), store.window.days - 1)),
                                    beside: layout.lines.indices.contains(line) ? layout.lines[line].entries.first?.id : nil,
                                    point: CGPoint(x: press.startLocation.x, y: press.startLocation.y + Self.headHeight))
            }
    }

    private var dayMenuAnchor: CGRect {
        guard let point = dayMenu?.point else { return .zero }
        // The pressed day's column on the pressed row, so the arrow points at the cell.
        let column = (point.x / Self.dayWidth).rounded(.down) * Self.dayWidth
        let row = ((point.y - Self.headHeight) / Self.rowHeight).rounded(.down) * Self.rowHeight + Self.headHeight
        return CGRect(x: column, y: row, width: Self.dayWidth, height: Self.rowHeight)
    }

    private func dayMenuContent(_ target: DayTarget) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Button {
                dayMenu = nil
                onAddTask(target.day, target.beside)
            } label: {
                Label("Add a task on \(RoadmapDates.day(target.day))", systemImage: "plus")
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            if !store.isPersonal {
                Divider()
                Button {
                    dayMenu = nil
                    onAddMilestone(target.day)
                } label: {
                    Label("Add a milestone on \(RoadmapDates.day(target.day))", systemImage: "flag")
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
            }
        }
        .buttonStyle(.plain)
        .padding(16)
        .frame(minWidth: 250)
        .presentationCompactAdaptation(.popover)
    }

    // MARK: Milestones

    /// A dotted line at the end of the milestone's day, with a flag naming it in the month band.
    private func milestoneView(_ milestone: RoadmapMilestone, height: CGFloat) -> some View {
        let offset = milestoneDrag?.id == milestone.id ? milestoneDrag?.days ?? 0 : 0
        let index = min(max(store.window.index(of: milestone.day) + offset, 0), store.window.days - 1)
        let x = CGFloat(index + 1) * Self.dayWidth
        let color = Color(hex: milestone.color)
        let due = RoadmapSprints.isMilestoneDue(milestone.day, today: store.today, settings: store.settings)

        return ZStack(alignment: .topLeading) {
            Path { path in
                path.move(to: CGPoint(x: 0, y: Self.monthHeight))
                path.addLine(to: CGPoint(x: 0, y: height))
            }
            .stroke(color, style: StrokeStyle(lineWidth: 2, dash: [4, 4]))
            .frame(width: 2, height: height)
            .allowsHitTesting(false)

            Text(milestone.name)
                .font(.caption2.weight(.semibold))
                .lineLimit(1)
                .padding(.horizontal, 7)
                .frame(height: Self.monthHeight - 6)
                .foregroundStyle(Color.readable(on: milestone.color))
                .background(color, in: UnevenRoundedRectangle(topLeadingRadius: 0, bottomLeadingRadius: 0,
                                                              bottomTrailingRadius: 7, topTrailingRadius: 7))
                .offset(y: 3)
                .fixedSize()
                .onTapGesture {
                    if store.canWrite { onEditMilestone(milestone) }
                }
                .gesture(store.canWrite ? moveMilestone(milestone) : nil)
                .accessibilityLabel("Milestone \(milestone.name), \(milestone.day.formatted)")
        }
        .offset(x: x - 1)
        // A milestone flashes while it is due; once its day has ended it is history.
        .phaseAnimator(due ? [1.0, 0.45] : [1.0]) { view, phase in
            view.opacity(phase)
        } animation: { _ in .easeInOut(duration: 0.9) }
        .zIndex(4)
    }

    private func moveMilestone(_ milestone: RoadmapMilestone) -> some Gesture {
        LongPressGesture(minimumDuration: 0.28)
            .sequenced(before: DragGesture(minimumDistance: 0))
            .onChanged { value in
                guard case .second(true, let movement?) = value else { return }
                store.gesturing = true
                milestoneDrag = (milestone.id, Int((movement.translation.width / Self.dayWidth).rounded()))
            }
            .onEnded { _ in
                let days = milestoneDrag?.days ?? 0
                milestoneDrag = nil
                store.gesturing = false
                guard days != 0 else { return }
                let day = min(max(milestone.day.adding(days: days), store.window.first), store.window.last)
                Task { await store.saveMilestone(milestone, name: milestone.name, day: day, color: milestone.color) }
            }
    }
}

/// One task's bar.
private struct RoadmapBar: View {
    @Environment(\.theme) private var theme
    var entry: RoadmapEntry
    var store: RoadmapStore
    var place: BarPlacement
    var selected: Bool
    /// How far the label is pushed in, so it stays in view while the bar's start is scrolled off.
    var labelInset: CGFloat
    var width: CGFloat

    var body: some View {
        let task = entry.task
        let pill = TimelineRules.isPill(entry)
        let missed = TimelineRules.isMissed(task, today: store.today)
        let finished = task.status == .finished
        let overrun = TimelineRules.daysPastDeadline(entry)
        let shape = RoundedRectangle(cornerRadius: pill ? 17 : 9, style: .continuous)
        let edge = missed ? theme.error : theme.primary.opacity(0.55)

        HStack(spacing: 6) {
            if place.clippedStart {
                Image(systemName: "chevron.left").font(.caption2.weight(.bold))
            }
            Text(store.serial(entry))
                .font(.caption2.monospaced())
                .foregroundStyle(theme.textSecondary)
                .lineLimit(1)
                .fixedSize()
                .layoutPriority(width > 150 ? 1 : -1)
            Text(task.name)
                .font(.footnote.weight(.medium))
                .strikethrough(finished)
                .lineLimit(1)
            Spacer(minLength: 0)
            if overrun > 0 {
                Image(systemName: "exclamationmark.triangle.fill")
                    .font(.caption2)
                    .foregroundStyle(theme.warning)
                    .accessibilityLabel("Planned \(overrun) \(overrun == 1 ? "day" : "days") past its deadline")
            }
            if width >= 220 {
                AvatarStack(members: store.assignees(task), max: 3, size: 20)
            }
            if place.clippedEnd {
                Image(systemName: "chevron.right").font(.caption2.weight(.bold))
            }
        }
        .padding(.leading, 10 + labelInset)
        .padding(.trailing, 8)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background {
            ZStack(alignment: .leading) {
                shape.fill(.thinMaterial)
                shape.fill(missed ? theme.error.opacity(0.2) : selected ? theme.accentWash : .clear)
                // The label's colour down the leading edge, as on the web.
                if let label = store.label(task) {
                    Rectangle().fill(Color(hex: label.color)).frame(width: 4)
                }
            }
            .clipShape(shape)
        }
        .overlay {
            shape.strokeBorder(selected ? theme.primary : edge,
                               style: StrokeStyle(lineWidth: selected ? 2 : 1, dash: pill ? [5, 3] : []))
        }
        .opacity(finished ? 0.6 : 1)
        .contentShape(shape)
        // A missed deadline flashes.
        .phaseAnimator(missed ? [1.0, 0.6] : [1.0]) { view, phase in
            view.opacity(phase)
        } animation: { _ in .easeInOut(duration: 0.9) }
    }
}
