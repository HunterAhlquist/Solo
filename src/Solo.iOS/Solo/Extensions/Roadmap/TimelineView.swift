import SwiftUI

/// A roadmap's grid: one column per day, scrolled sideways between the
/// instance's bounds, with each task a bar on its row.
///
/// Touch works the way Calendar's does. A tap opens the task. A press and hold
/// picks a bar up: dragged sideways it moves its dates, dragged up or down it
/// changes row, and let go where it was picked up it is only selected. A bar
/// selected alone stands a little larger, with a grip grown into each end to
/// stretch it by. More bars join the selection with a tap. A press and hold
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
    /// How far the one selected bar stands out past its days and its row on every side. Its grips
    /// are its ends, so what makes the bar larger makes them easier to take hold of.
    private static let swell: CGFloat = 7
    /// How far a grip can be taken hold of outside its bar, and at most inside it.
    private static let gripOutside: CGFloat = 12
    private static let gripInside: CGFloat = 24
    /// Selecting a bar and letting it go: the spring the system moves its own controls with.
    private static let selecting = Animation.spring(duration: 0.35, bounce: 0.2)

    @State private var scrollX: CGFloat = 0
    @State private var viewport: CGFloat = 0
    @State private var position = ScrollPosition(x: 0)
    @State private var drag: BarDrag?
    @State private var milestoneDrag: (id: UUID, days: Int)?
    @State private var dayMenu: DayTarget?
    @State private var didOpen = false
    /// The bar being carried, for as long as the drag carrying it lasts. It empties however the
    /// drag ends, which a gesture's own ending does not promise.
    @GestureState private var holding: UUID?
    /// The bar a hold was last lifted from, and when.
    @State private var lifted: (id: UUID, at: Date)?

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
                    // With a pointer, the secondary button asks for the same thing a press and hold does.
                    .gesture(SecondaryClick { point in
                        guard store.canWrite else { return }
                        showDayMenu(at: point, layout: layout)
                    })

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
        .onChange(of: holding) { held, now in
            // A drag taken away rather than lifted (another gesture winning it, the system
            // stepping in) never reports its end, and the bar would stay in hand for good.
            if now == nil, let held { putDown(held) }
        }
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
        // Only a bar selected alone can be stretched, so only it grows and shows its grips.
        let grown = selected && store.selected.count == 1 && store.canWrite
        let gripped = grown && drag?.kind != .row
        let swell = grown ? Self.swell : 0
        let grips = BarGrips(start: gripped && start == place.startIndex && !place.clippedStart,
                             end: gripped && end == place.endIndex && !place.clippedEnd)

        RoadmapBar(entry: entry, store: store, place: place, selected: selected, grips: grips,
                   labelInset: min(max(scrollX - barX + 6, 0), max(barWidth - 90, 0)), width: barWidth)
            .frame(width: barWidth + swell * 2, height: Self.rowHeight - 10 + swell * 2)
            .overlay {
                if gripped {
                    handles(entry, grips: grips, width: barWidth + swell * 2)
                }
            }
            .offset(x: barX - swell, y: Self.headHeight + CGFloat(line) * Self.rowHeight + 5 - swell + lift)
            .zIndex(dragged ? 10 : selected ? 5 : 1)
            .shadow(color: .black.opacity(dragged ? 0.25 : 0), radius: 10, y: 4)
            .onTapGesture { tapped(entry) }
            .gesture(store.canWrite ? pickUp(entry, line: line) : nil)
            .animation(.snappy(duration: 0.18), value: drag?.days)
            .animation(Self.selecting, value: grown)
            .animation(Self.selecting, value: grips)
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

    /// With nothing selected a tap opens the task; once something is, it adds the bar to the
    /// selection or takes it out.
    ///
    /// A tap is any touch lifted where it landed, however long it stayed there, so the end of a
    /// hold that never moved arrives here too, before or after the hold's own end. That one is
    /// not a tap: the hold has selected the bar, and answering it would take the bar out again.
    private func tapped(_ entry: RoadmapEntry) {
        if drag?.entryId == entry.id {
            putDown(entry.id)
            return
        }
        if let lifted, lifted.id == entry.id, Date.now.timeIntervalSince(lifted.at) < 0.1 { return }
        if store.selected.isEmpty || !store.canWrite {
            onOpen(entry)
        } else if store.selected.contains(entry.id) {
            store.selected.remove(entry.id)
        } else {
            store.selected.insert(entry.id)
        }
    }

    /// Press and hold picks the bar up, and selects it there and then: let go without moving,
    /// that is all the hold does. The first direction it is then moved in sets what the drag
    /// does, so a move up or down never changes its dates.
    ///
    /// Each half of the gesture is listened to on its own. Listening to the pair for changes has
    /// it follow every touch from the moment it lands, and a swipe that starts on a bar then
    /// never reaches the scroll view: with bars across most of a busy roadmap, it hardly scrolls.
    private func pickUp(_ entry: RoadmapEntry, line: Int) -> some Gesture {
        LongPressGesture(minimumDuration: 0.28)
            .onEnded { _ in
                guard drag == nil else { return }
                if !store.selected.contains(entry.id) {
                    store.selected = [entry.id]
                }
                store.gesturing = true
                store.report(editing: true)
                drag = BarDrag(entryId: entry.id, kind: .undecided)
            }
            .sequenced(before: DragGesture(minimumDistance: 0)
                .updating($holding) { _, held, _ in held = entry.id }
                .onChanged { carry(entry, line: line, by: $0.translation) }
                .onEnded { _ in putDown(entry.id) })
    }

    /// Moves the bar in hand to where the finger has taken it.
    private func carry(_ entry: RoadmapEntry, line: Int, by translation: CGSize) {
        guard var active = drag, active.entryId == entry.id else { return }
        if case .resize = active.kind { return }
        active.translation = translation
        if active.kind == .undecided, max(abs(translation.width), abs(translation.height)) > 10 {
            active.kind = abs(translation.width) >= abs(translation.height) ? .move : .row
        }
        switch active.kind {
        case .move:
            let wanted = Int((translation.width / Self.dayWidth).rounded())
            active.days = store.group(of: entry).reduce(wanted) { days, moving in
                let clamped = store.window.clampMove(TimelineRules.span(moving), by: days)
                return abs(clamped) < abs(days) ? clamped : days
            }
        case .row:
            active.target = rowTarget(entry, line: line, dy: translation.height)
        default:
            break
        }
        drag = active
    }

    /// Ends a hold on a bar, saving wherever it was carried to. The gesture's end and the press
    /// going away both come here, in either order, and the second finds nothing left in hand.
    private func putDown(_ id: UUID) {
        guard let active = drag, active.entryId == id else { return }
        if case .resize = active.kind { return }
        drag = nil
        lifted = (id, .now)
        store.gesturing = false
        store.report(editing: false)
        guard let entry = store.entries.first(where: { $0.id == id }) else { return }
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

    /// Where the selected bar's two grips are taken hold of. The bar draws them; these are the
    /// room around each, reaching past the bar's end and no further in than leaves its middle
    /// to pick the bar up by.
    private func handles(_ entry: RoadmapEntry, grips: BarGrips, width: CGFloat) -> some View {
        let inside = min(Self.gripInside, width / 3)
        return HStack(spacing: 0) {
            if grips.start {
                handle(entry, edge: .start, width: Self.gripOutside + inside)
            }
            Spacer(minLength: 0)
            if grips.end {
                handle(entry, edge: .end, width: Self.gripOutside + inside)
            }
        }
        .padding(.horizontal, -Self.gripOutside)
        .padding(.vertical, -1)
    }

    private func handle(_ entry: RoadmapEntry, edge: BarEdge, width: CGFloat) -> some View {
        Color.clear
            .frame(width: width)
            .frame(maxHeight: .infinity)
            .contentShape(.rect)
            .gesture(
                // Measured against the screen: the grip moves with the end it stretches, and a drag
                // measured against the grip itself would lose a day for every day it gained.
                DragGesture(minimumDistance: 2, coordinateSpace: .global)
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
                showDayMenu(at: press.startLocation, layout: layout)
            }
    }

    /// Offers to add something on the day at a place in the grid.
    private func showDayMenu(at point: CGPoint, layout: TimelineLayout) {
        // The place is measured from the top of the grid, head included: an offset moves what is
        // drawn, not where a gesture on it counts from.
        let index = Int(point.x / Self.dayWidth)
        let line = Int((point.y - Self.headHeight) / Self.rowHeight)
        dayMenu = DayTarget(day: store.window.day(at: min(max(index, 0), store.window.days - 1)),
                            beside: layout.lines.indices.contains(line) ? layout.lines[line].entries.first?.id : nil,
                            point: point)
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

    /// Listened to in halves, as a bar's hold is, so a swipe across a flag still scrolls.
    private func moveMilestone(_ milestone: RoadmapMilestone) -> some Gesture {
        LongPressGesture(minimumDuration: 0.28)
            .sequenced(before: DragGesture(minimumDistance: 0)
                .onChanged { movement in
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
                })
    }
}

/// Which ends of a bar show a grip to stretch it by.
private struct BarGrips: Equatable {
    var start = false
    var end = false
}

/// A grip on one end of a bar, in the bar's outline: a cloth pinned along the edge with a ball
/// risen under the middle of it. How far it stands in from the edge, at each height `y` of a bar
/// `h` tall, is
///
///     band + (peak - band) * exp(-|(y - h/2) / (spread * h)|^blunt) + fillet(y)
///
/// a band the length of the edge, a bell over the ball, and at each end a quarter circle easing the
/// band into the outline it meets there. `rise` scales all of it: at none the grip lies flat along
/// the edge, which is where it grows from and goes back to.
private nonisolated struct BarGrip: Shape {
    var edge: BarEdge
    var rise: CGFloat

    var animatableData: CGFloat {
        get { rise }
        set { rise = newValue }
    }

    /// How far the bump stands in from the edge at its highest.
    static let peak: CGFloat = 18.5
    /// How thick the cloth lies along the rest of the edge.
    private static let band: CGFloat = 6.5
    /// How wide the bump is, as the share of the bar's height its bell falls away over.
    private static let spread: CGFloat = 0.28
    /// How flat the top of the bump is: 2 is a bell, more squares its shoulders.
    private static let blunt: CGFloat = 2.3
    /// The radius the band turns into the top and bottom outline with, and how thick that outline is.
    private static let fillet: CGFloat = 8
    private static let outline: CGFloat = 2

    static func reach(at y: CGFloat, height: CGFloat) -> CGFloat {
        let fromMiddle = abs(y - height / 2) / (spread * height)
        let bump = (peak - band) * exp(-pow(fromMiddle, blunt))
        // How far into the turn this height is: none clear of it, all of it at the outline.
        let fromEnd = min(y, height - y) - outline
        let turned = min(max(fillet - fromEnd, 0), fillet)
        return band + bump + fillet - (fillet * fillet - turned * turned).squareRoot()
    }

    func path(in rect: CGRect) -> Path {
        let x = edge == .start ? rect.minX : rect.maxX
        let inward: CGFloat = edge == .start ? rise : -rise
        var path = Path()
        path.move(to: CGPoint(x: x, y: rect.minY))
        // Drawn as the curve itself, a point for every half point of height: finer than a screen shows.
        let steps = max(Int(rect.height * 2), 1)
        for step in 0...steps {
            let y = rect.height * CGFloat(step) / CGFloat(steps)
            path.addLine(to: CGPoint(x: x + inward * Self.reach(at: y, height: rect.height), y: rect.minY + y))
        }
        path.addLine(to: CGPoint(x: x, y: rect.maxY))
        path.closeSubpath()
        return path
    }
}

/// One task's bar.
private struct RoadmapBar: View {
    @Environment(\.theme) private var theme
    var entry: RoadmapEntry
    var store: RoadmapStore
    var place: BarPlacement
    var selected: Bool
    var grips = BarGrips()
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
        let outline = selected ? theme.primary : edge

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
        // The words are level with a grip's bump, and stand clear of it.
        .padding(.leading, 10 + labelInset + (grips.start ? BarGrip.peak - 3 : 0))
        .padding(.trailing, 8 + (grips.end ? BarGrip.peak - 3 : 0))
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
        .background {
            ZStack(alignment: .leading) {
                shape.fill(.thinMaterial)
                shape.fill(missed ? theme.error.opacity(0.2) : selected ? theme.accentWash : .clear)
                // The label's colour down the leading edge, as on the web.
                if let label = store.label(task) {
                    Rectangle().fill(Color(hex: label.color)).frame(width: 4)
                }
                // The grips are the outline itself grown inward, so they are its colour. They are
                // always here, flat against their ends until wanted, to have something to grow from.
                BarGrip(edge: .start, rise: grips.start ? 1 : 0).fill(outline)
                BarGrip(edge: .end, rise: grips.end ? 1 : 0).fill(outline)
            }
            .clipShape(shape)
        }
        .overlay {
            shape.strokeBorder(outline, style: StrokeStyle(lineWidth: selected ? 2 : 1, dash: pill ? [5, 3] : []))
        }
        .opacity(finished ? 0.6 : 1)
        .contentShape(shape)
        // A missed deadline flashes.
        .phaseAnimator(missed ? [1.0, 0.6] : [1.0]) { view, phase in
            view.opacity(phase)
        } animation: { _ in .easeInOut(duration: 0.9) }
    }
}

/// A click of a pointer's secondary button — a right click on a trackpad or a mouse — and where it was.
#if os(macOS)
private struct SecondaryClick: NSGestureRecognizerRepresentable {
    var action: (CGPoint) -> Void

    func makeNSGestureRecognizer(context: Context) -> NSClickGestureRecognizer {
        let recognizer = NSClickGestureRecognizer()
        recognizer.buttonMask = 0x2
        return recognizer
    }

    func handleNSGestureRecognizerAction(_ recognizer: NSClickGestureRecognizer, context: Context) {
        if recognizer.state == .ended { action(context.converter.localLocation) }
    }
}
#else
private struct SecondaryClick: UIGestureRecognizerRepresentable {
    var action: (CGPoint) -> Void

    func makeUIGestureRecognizer(context: Context) -> UITapGestureRecognizer {
        let recognizer = UITapGestureRecognizer()
        recognizer.buttonMaskRequired = .secondary
        recognizer.allowedTouchTypes = [NSNumber(value: UITouch.TouchType.indirectPointer.rawValue)]
        return recognizer
    }

    func handleUIGestureRecognizerAction(_ recognizer: UITapGestureRecognizer, context: Context) {
        if recognizer.state == .ended { action(context.converter.localLocation) }
    }
}
#endif
