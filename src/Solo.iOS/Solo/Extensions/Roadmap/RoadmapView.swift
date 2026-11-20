import SwiftUI

/// A project's roadmap: its tasks as bars on a day grid, its milestones, and
/// the deadlines that were missed.
struct RoadmapView: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme

    var projectAlias: String
    var roadmapId: UUID

    @State private var store: RoadmapStore?
    @State private var failed: String?

    var body: some View {
        Group {
            if let store, store.timeline != nil {
                RoadmapPage(store: store)
            } else if let message = store?.loadError ?? failed {
                ContentUnavailableView("Couldn't open that roadmap", systemImage: "chart.line.uptrend.xyaxis",
                                       description: Text(message))
            } else {
                ProgressView().controlSize(.large)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .themedBackground()
        .navigationTitle(store?.timeline?.title ?? "Roadmap")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: roadmapId) {
            if session.projects.project(alias: projectAlias) == nil {
                await session.projects.refreshProjects()
            }
            guard let project = session.projects.project(alias: projectAlias) else {
                failed = "That project doesn't exist, or you don't have access to it."
                return
            }
            let fresh = RoadmapStore(session: session, scope: .project(projectId: project.id, roadmapId: roadmapId))
            store = fresh
            await fresh.load()
        }
        .onDisappear {
            Task { await store?.stopLive() }
        }
        .onChange(of: store?.gone) { _, gone in
            // Deleted by someone else while it was open.
            guard gone == true else { return }
            session.toasts.show("This roadmap was deleted.", .info)
            navigator.open(.project(alias: projectAlias))
        }
    }
}

/// The timeline with everything around it: the toolbar or its personal
/// equivalent, the sheets it opens, the selection bar, and the missed list.
struct RoadmapPage: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    @Bindable var store: RoadmapStore
    /// Set where the page is embedded in another that owns the task panel.
    var onOpenTask: ((UUID) -> Void)?

    @State private var openTask: UUID?
    @State private var lastOpened: UUID?
    @State private var picking: PickTarget?
    @State private var milestone: MilestoneDraft?
    @State private var prompt: TextPrompt?
    @State private var confirmation: Confirmation?
    @State private var scrollRequest: TimelineView.ScrollRequest?

    struct PickTarget: Identifiable {
        let id = UUID()
        var day: Day
        var beside: UUID?
    }

    var body: some View {
        Group {
            if store.isPersonal {
                // Embedded in Your Tasks, which does the scrolling.
                content
                    .overlay(alignment: .bottom) {
                        if !store.selected.isEmpty, !Platform.isMac {
                            selectionBar.padding(.bottom, 8)
                        }
                    }
            } else {
                ScrollView {
                    content.padding(16)
                }
                .safeAreaInset(edge: .bottom) {
                    if !store.selected.isEmpty, !Platform.isMac {
                        selectionBar.padding(.bottom, 8)
                    }
                }
                // Inside the panel modifier: an inspector lays its own backing under the page beside it.
                .themedBackground()
                .taskDetail($openTask)
            }
        }
        .animation(.snappy, value: store.selected.isEmpty)
        .toolbar {
            if !store.isPersonal {
                ToolbarItem(placement: .topBarTrailing) {
                    PresenceCluster(entries: store.present.map {
                        PresenceEntry(userId: $0.userId, displayName: $0.displayName, entityType: "roadmap",
                                      entityId: store.timeline?.id ?? UUID(), mode: $0.mode)
                    }, max: 4)
                }
                ToolbarItemGroup(placement: .topBarTrailing) {
                    Button("Today", systemImage: "calendar") { scrollRequest = .init(day: store.today) }
                    if store.canWrite {
                        Menu("Add", systemImage: "plus") {
                            Button("Add Task", systemImage: "checklist") { picking = PickTarget(day: store.today) }
                            Button("Add Milestone", systemImage: "flag") { milestone = MilestoneDraft(day: store.today) }
                        } primaryAction: {
                            picking = PickTarget(day: store.today)
                        }
                    }
                    if store.canWrite || store.canDelete {
                        Menu("Roadmap actions", systemImage: "ellipsis") {
                            if store.canWrite {
                                Button("Rename", systemImage: "pencil") { rename() }
                            }
                            if store.canDelete {
                                Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete() }
                            }
                        }
                    }
                }
            }
        }
        .sheet(item: $picking) { target in
            TaskPickerSheet(
                title: "Add a task on \(RoadmapDates.day(target.day))",
                projectId: store.timeline.flatMap { store.isPersonal ? nil : $0.projectId },
                exclude: Set(store.entries.map(\.task.id)),
                assignedTo: store.isPersonal ? session.user.id : nil
            ) { task, _ in
                Task {
                    if let entryId = await store.add(task, on: target.day, beside: target.beside),
                       let entry = store.entries.first(where: { $0.id == entryId }) {
                        scrollRequest = .init(day: TimelineRules.span(entry).start)
                    }
                }
            }
        }
        .sheet(item: $milestone) { draft in
            MilestoneSheet(draft: draft) { name, day, color in
                await store.saveMilestone(draft.existing, name: name, day: day, color: color)
            } onDelete: {
                if let existing = draft.existing {
                    await store.deleteMilestone(existing)
                }
            }
        }
        .textPrompt($prompt)
        .confirmation($confirmation)
        .onChange(of: openTask) { _, now in
            // The panel closed: whatever it did to the task shows on its bar.
            if now == nil, let lastOpened {
                Task { await store.taskChanged(lastOpened) }
            }
            lastOpened = now ?? lastOpened
        }
    }

    private var content: some View {
        VStack(alignment: .leading, spacing: 18) {
            if store.isPersonal {
                HStack {
                    Button("Add Task", systemImage: "plus") { picking = PickTarget(day: store.today) }
                        .buttonStyle(.clearGlass)
                    Button("Today", systemImage: "calendar") { scrollRequest = .init(day: store.today) }
                        .buttonStyle(.clearGlass)
                    Spacer()
                }
            }

            TimelineView(
                store: store,
                onOpen: { open($0) },
                onAddTask: { day, beside in picking = PickTarget(day: day, beside: beside) },
                onAddMilestone: { milestone = MilestoneDraft(day: $0) },
                onEditMilestone: { milestone = MilestoneDraft(existing: $0) },
                scrollRequest: scrollRequest,
                selectionBar: Platform.isMac && !store.selected.isEmpty ? AnyView(selectionBar) : nil)

            missedSection
        }
    }

    private func open(_ entry: RoadmapEntry) {
        store.selected = []
        if let onOpenTask {
            onOpenTask(entry.task.id)
        } else {
            openTask = entry.task.id
        }
    }

    // MARK: Selection

    /// What a press-and-hold selects is acted on here: the same commands the web puts in its bar menu.
    /// Under a thumb it sits at the bottom of the page. On a Mac the pointer is at the selection, so
    /// the timeline is handed it to hang there.
    private var selectionBar: some View {
        let targets = store.entries.filter { store.selected.contains($0.id) }
        let planned = targets.filter { $0.schedule != nil }
        return GlassEffectContainer {
            HStack(spacing: 14) {
                Text(targets.count == 1 ? store.serial(targets[0]) : "\(targets.count) selected")
                    .font(.footnote.weight(.semibold).monospacedDigit())
                    .lineLimit(1)
                if targets.count == 1 {
                    Button("Open", systemImage: "arrow.up.forward.app") { open(targets[0]) }
                    if store.isPersonal {
                        Button("Go to \(targets[0].boardName)", systemImage: "rectangle.split.3x1") {
                            navigator.open(.board(project: targets[0].projectAlias ?? "", board: targets[0].boardAlias))
                        }
                    }
                }
                if store.isPersonal, !planned.isEmpty {
                    Button(planned.count == 1 && targets.count == 1 ? "Use the task's dates" : "Use the tasks' dates",
                           systemImage: "arrow.uturn.backward") {
                        Task { await store.followTaskDates(planned) }
                    }
                }
                if store.canWrite {
                    Button("Remove from \(store.isPersonal ? "your roadmap" : "roadmap")", systemImage: "minus.circle",
                           role: .destructive) {
                        Task { await store.remove(targets) }
                    }
                    .tint(theme.error)
                }
                Button("Done", systemImage: "xmark") { store.selected = [] }
            }
            .labelStyle(.iconOnly)
            .padding(.horizontal, 18)
            .padding(.vertical, 12)
            .glassEffect(.regular.interactive(), in: .capsule)
        }
    }

    // MARK: Missed deadlines

    private var missedSection: some View {
        VStack(alignment: .leading, spacing: 8) {
            Text("Missed deadlines").font(.headline)
            let missed = store.missed
            if missed.isEmpty {
                Text("No missed deadlines.").font(.subheadline).foregroundStyle(theme.textSecondary)
            } else {
                VStack(spacing: 0) {
                    ForEach(missed) { entry in
                        let overdue = (entry.task.deadlineAt ?? store.today).days(until: store.today)
                        Button { open(entry) } label: {
                            HStack(spacing: 10) {
                                VStack(alignment: .leading, spacing: 2) {
                                    Text(entry.task.name).font(.subheadline).foregroundStyle(theme.textPrimary)
                                        .multilineTextAlignment(.leading)
                                    Text(store.isPersonal
                                        ? "\(store.serial(entry)) · \(entry.projectName ?? "") • \(entry.boardName)"
                                        : store.serial(entry))
                                        .font(.caption.monospaced())
                                        .foregroundStyle(theme.textSecondary)
                                }
                                Spacer(minLength: 8)
                                AvatarStack(members: store.assignees(entry.task), max: 3, size: 22)
                                Text("\(overdue) \(overdue == 1 ? "day" : "days")")
                                    .font(.footnote.weight(.semibold).monospacedDigit())
                                    .foregroundStyle(theme.error)
                            }
                            .padding(.horizontal, 14)
                            .padding(.vertical, 10)
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        if entry.id != missed.last?.id {
                            Divider().padding(.leading, 14)
                        }
                    }
                }
                .card(padding: 0)
            }
        }
    }

    // MARK: The roadmap itself

    private func rename() {
        store.report(editing: true)
        prompt = TextPrompt(title: "Rename roadmap", placeholder: "Title", initial: store.timeline?.title ?? "",
                            action: "Rename", limit: RoadmapLimits.title) { title in
            await store.rename(title)
            store.report(editing: false)
        }
    }

    private func confirmDelete() {
        confirmation = Confirmation(
            title: "Delete roadmap",
            message: "Delete \"\(store.timeline?.title ?? "")\"? Its tasks stay on their boards; only the roadmap goes.") {
            if await store.delete(), let alias = store.timeline?.projectAlias {
                navigator.open(.project(alias: alias))
            }
        }
    }
}

// MARK: - Milestones

struct MilestoneDraft: Identifiable {
    let id = UUID()
    var existing: RoadmapMilestone?
    var day: Day

    init(day: Day) {
        self.day = day
    }

    init(existing: RoadmapMilestone) {
        self.existing = existing
        day = existing.day
    }
}

struct MilestoneSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    var draft: MilestoneDraft
    var onSave: (String, Day, String) async -> Void
    var onDelete: () async -> Void

    @State private var name: String
    @State private var date: Date
    @State private var color: String
    @FocusState private var nameFocused: Bool

    init(draft: MilestoneDraft, onSave: @escaping (String, Day, String) async -> Void, onDelete: @escaping () async -> Void) {
        self.draft = draft
        self.onSave = onSave
        self.onDelete = onDelete
        _name = State(initialValue: draft.existing?.name ?? "")
        _date = State(initialValue: draft.day.date())
        _color = State(initialValue: draft.existing?.color ?? RoadmapLimits.defaultMilestoneColor)
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Name", text: $name).focused($nameFocused)
                    DatePicker("Day", selection: $date, displayedComponents: .date)
                }
                .themedRow()
                Section("Colour") {
                    HStack(spacing: 10) {
                        ForEach(RoadmapLimits.milestoneSwatches, id: \.self) { swatch in
                            Button {
                                color = swatch
                            } label: {
                                Circle()
                                    .fill(Color(hex: swatch))
                                    .frame(width: 30, height: 30)
                                    .overlay {
                                        if color.lowercased() == swatch {
                                            Image(systemName: "checkmark").font(.caption.weight(.bold))
                                                .foregroundStyle(Color.readable(on: swatch))
                                        }
                                    }
                            }
                            .buttonStyle(.plain)
                            .accessibilityLabel("Colour \(swatch)")
                        }
                    }
                }
                .themedRow()
                if draft.existing != nil {
                    Section {
                        Button("Delete Milestone", systemImage: "trash", role: .destructive) {
                            dismiss()
                            Task { await onDelete() }
                        }
                    }
                    .themedRow()
                }
            }
            .themedList()
            .navigationTitle(draft.existing == nil ? "New milestone" : "Edit milestone")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) { Button("Cancel") { dismiss() } }
                ToolbarItem(placement: .confirmationAction) {
                    Button(draft.existing == nil ? "Add" : "Save") {
                        let trimmed = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(RoadmapLimits.milestoneName))
                        dismiss()
                        Task { await onSave(trimmed, Day(date), color) }
                    }
                    .fontWeight(.semibold)
                    .disabled(name.nilIfBlank == nil)
                }
            }
            .onAppear { nameFocused = draft.existing == nil }
        }
        .presentationDetents([.medium, .large])
    }
}
