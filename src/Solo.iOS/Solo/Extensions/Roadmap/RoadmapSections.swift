import SwiftUI

/// A project's roadmaps, for the places that list them: kept current by the
/// extension's list topic and by this device's own changes.
@Observable
final class RoadmapListModel {
    private(set) var roadmaps: [RoadmapSummary]?
    private(set) var failed = false

    @ObservationIgnored private let session: Session
    @ObservationIgnored private let projectId: UUID
    @ObservationIgnored private var subscriptions: [EventSubscription] = []
    @ObservationIgnored private var live = false

    init(session: Session, projectId: UUID) {
        self.session = session
        self.projectId = projectId
    }

    /// `live` subscribes to the project's list topic; a card in a long list of
    /// projects skips it, a connection only holding so many topics.
    func start(live: Bool) async {
        await load()
        guard subscriptions.isEmpty else { return }
        subscriptions = [
            RoadmapLists.didChange.subscribe { [weak self] changed in
                guard let self, changed == self.projectId else { return }
                Task { await self.load() }
            },
        ]
        if live {
            self.live = true
            subscriptions.append(session.hub.topicMessage.subscribe { [weak self] message in
                guard let self, message.topic == RoadmapTopics.list, message.projectId == self.projectId else { return }
                Task { await self.load() }
            })
            await session.hub.subscribe(projectId: projectId, topic: RoadmapTopics.list)
        }
    }

    func stop() async {
        subscriptions = []
        if live {
            live = false
            await session.hub.unsubscribe(projectId: projectId, topic: RoadmapTopics.list)
        }
    }

    func load() async {
        do {
            roadmaps = try await session.api.roadmaps(projectId)
            failed = false
        } catch {
            failed = roadmaps == nil
        }
    }
}

private struct RoadmapCreator: ViewModifier {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    var project: ProjectSummary
    @Binding var prompt: TextPrompt?

    func body(content: Content) -> some View {
        content.textPrompt($prompt)
    }

    static func prompt(session: Session, navigator: Navigator, project: ProjectSummary) -> TextPrompt {
        TextPrompt(title: "New roadmap in \(project.name)", placeholder: "Title", limit: RoadmapLimits.title) { title in
            guard let created = await session.save({ try await session.api.createRoadmap(project.id, title: title) })
            else { return }
            RoadmapLists.changed(project.id)
            navigator.open(.roadmap(project: project.alias, roadmapId: created.id))
        }
    }
}

/// Under a project in the sidebar: its roadmaps, and — for a super admin — a way to add one.
struct RoadmapSidebarSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    @State private var model: RoadmapListModel?
    @State private var prompt: TextPrompt?

    var body: some View {
        Group {
            Text("Roadmaps")
                .font(.caption.weight(.semibold))
                .foregroundStyle(theme.textSecondary)
            if let roadmaps = model?.roadmaps {
                ForEach(roadmaps) { roadmap in
                    SidebarRow(title: roadmap.title, symbol: "chart.line.uptrend.xyaxis",
                               selected: navigator.current == .roadmap(project: project.alias, roadmapId: roadmap.id)) {
                        navigator.open(.roadmap(project: project.alias, roadmapId: roadmap.id))
                    }
                }
                if roadmaps.isEmpty {
                    Text("No roadmaps.").font(.caption).foregroundStyle(theme.textSecondary)
                }
            } else if model?.failed == true {
                Text("Roadmaps couldn't load.").font(.caption).foregroundStyle(theme.error)
            }
            if session.manages(project) {
                Button("Add Roadmap", systemImage: "plus") {
                    prompt = RoadmapCreator.prompt(session: session, navigator: navigator, project: project)
                }
                .font(.subheadline)
                .foregroundStyle(theme.textSecondary)
            }
        }
        .task(id: project.id) {
            let fresh = model ?? RoadmapListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: true)
        }
        .onDisappear { Task { await model?.stop() } }
        .textPrompt($prompt)
    }
}

/// On a project's page: its roadmaps as cards, after its boards.
struct RoadmapHomeSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary
    var canWrite: Bool

    @State private var model: RoadmapListModel?
    @State private var prompt: TextPrompt?
    @State private var confirmation: Confirmation?

    private let columns = [GridItem(.adaptive(minimum: 240), spacing: 12, alignment: .top)]

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SectionTitle("Roadmaps")
            if let roadmaps = model?.roadmaps {
                if roadmaps.isEmpty && !session.manages(project) {
                    Text("No roadmaps yet.").foregroundStyle(theme.textSecondary)
                }
                LazyVGrid(columns: columns, spacing: 12) {
                    ForEach(roadmaps) { roadmap in
                        HStack(spacing: 12) {
                            Button {
                                navigator.open(.roadmap(project: project.alias, roadmapId: roadmap.id))
                            } label: {
                                HStack(spacing: 12) {
                                    Image(systemName: "chart.line.uptrend.xyaxis")
                                        .font(.title3)
                                        .foregroundStyle(theme.primary)
                                        .frame(width: 28)
                                    Text(roadmap.title).font(.headline).foregroundStyle(theme.textPrimary).lineLimit(1)
                                    Spacer(minLength: 0)
                                }
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            if canWrite || session.manages(project) {
                                Menu("Roadmap actions", systemImage: "ellipsis") {
                                    if canWrite {
                                        Button("Rename", systemImage: "pencil") { rename(roadmap) }
                                    }
                                    if session.manages(project) {
                                        Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(roadmap) }
                                    }
                                }
                                .labelStyle(.iconOnly)
                                .frame(width: 32, height: 32)
                            }
                        }
                        .card(padding: 14)
                    }
                    if session.manages(project) {
                        Button {
                            prompt = RoadmapCreator.prompt(session: session, navigator: navigator, project: project)
                        } label: {
                            Label("New Roadmap", systemImage: "plus")
                                .font(.subheadline.weight(.medium))
                                .frame(maxWidth: .infinity, minHeight: 34)
                                .padding(14)
                                .foregroundStyle(theme.textSecondary)
                                .overlay(theme.cardShape.strokeBorder(theme.divider, style: StrokeStyle(lineWidth: 1.5, dash: [6, 5])))
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                }
            } else if model?.failed == true {
                Text("Roadmaps couldn't load.").font(.footnote).foregroundStyle(theme.error)
            } else {
                ProgressView()
            }
        }
        .task(id: project.id) {
            let fresh = model ?? RoadmapListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: true)
        }
        .onDisappear { Task { await model?.stop() } }
        .textPrompt($prompt)
        .confirmation($confirmation)
    }

    private func rename(_ roadmap: RoadmapSummary) {
        prompt = TextPrompt(title: "Rename roadmap", placeholder: "Title", initial: roadmap.title, action: "Rename",
                            limit: RoadmapLimits.title) { title in
            guard title != roadmap.title else { return }
            do {
                _ = try await session.api.renameRoadmap(project.id, roadmap.id, title: title, version: roadmap.version)
            } catch let error as APIError where error.isConflict {
                session.toasts.show("Someone else changed this roadmap first. It's been refreshed; try again.", .warning)
            } catch {
                session.toasts.error(error)
            }
            RoadmapLists.changed(project.id)
        }
    }

    private func confirmDelete(_ roadmap: RoadmapSummary) {
        confirmation = Confirmation(
            title: "Delete roadmap",
            message: "Delete \"\(roadmap.title)\"? Its tasks stay on their boards; only the roadmap goes.") {
            await session.perform { try await session.api.deleteRoadmap(project.id, roadmap.id) }
            RoadmapLists.changed(project.id)
        }
    }
}

/// On a project's card in the projects list: its roadmaps, one line each.
struct RoadmapCardSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var project: ProjectSummary

    @State private var model: RoadmapListModel?

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            SectionTitle("Roadmaps")
            if let roadmaps = model?.roadmaps {
                if roadmaps.isEmpty {
                    Text("No roadmaps.").font(.caption).foregroundStyle(theme.textSecondary).padding(.vertical, 4)
                }
                ForEach(roadmaps) { roadmap in
                    Button {
                        navigator.open(.roadmap(project: project.alias, roadmapId: roadmap.id))
                    } label: {
                        HStack(spacing: 8) {
                            Image(systemName: "chart.line.uptrend.xyaxis").foregroundStyle(theme.primary).frame(width: 18)
                            Text(roadmap.title).foregroundStyle(theme.textPrimary).lineLimit(1)
                            Spacer(minLength: 0)
                        }
                        .font(.subheadline)
                        .padding(.vertical, 7)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            } else if model?.failed == true {
                Text("Roadmaps couldn't load.").font(.caption).foregroundStyle(theme.error)
            } else {
                ProgressView().padding(.vertical, 6)
            }
        }
        .task(id: project.id) {
            let fresh = model ?? RoadmapListModel(session: session, projectId: project.id)
            model = fresh
            await fresh.start(live: false)
        }
    }
}

/// On Your Tasks: the person's own roadmap, folding away under its header and
/// staying folded for them. Folded, it isn't built at all.
struct RoadmapDashboardSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    @Binding var openTask: UUID?

    private static let collapsedKey = "solo.dashboard.roadmapCollapsed"
    @State private var collapsed = UserDefaults.standard.bool(forKey: collapsedKey)
    @State private var store: RoadmapStore?
    @State private var lastOpened: UUID?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Button {
                withAnimation(.snappy) { collapsed.toggle() }
                UserDefaults.standard.set(collapsed, forKey: Self.collapsedKey)
            } label: {
                HStack(spacing: 8) {
                    Image(systemName: "chevron.down")
                        .rotationEffect(.degrees(collapsed ? -90 : 0))
                        .font(.footnote.weight(.bold))
                    Text("Your Roadmap").font(.title3.weight(.semibold))
                    Spacer()
                }
                .foregroundStyle(theme.textPrimary)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityHint(collapsed ? "Expands your roadmap" : "Collapses your roadmap")

            if !collapsed {
                if let store, store.timeline != nil {
                    RoadmapPage(store: store) { openTask = $0 }
                } else if let error = store?.loadError {
                    Text(error).font(.footnote).foregroundStyle(theme.error)
                } else {
                    ProgressView().frame(maxWidth: .infinity).padding()
                }
            }
        }
        .task(id: collapsed) {
            guard !collapsed else { return }
            // The store is kept, and asked again for as long as it has nothing to show: the
            // page is taken away and put back as it is gone to from another, which cuts the
            // first asking short, and one that was only made once was never made good.
            let current = store ?? RoadmapStore(session: session, scope: .personal)
            store = current
            if current.timeline == nil {
                await current.load()
            }
        }
        .onChange(of: openTask) { _, now in
            if now == nil, let lastOpened {
                Task { await store?.taskChanged(lastOpened) }
            }
            lastOpened = now ?? lastOpened
        }
    }
}

/// On a task's detail: the roadmaps it is on, each a way there. Nothing at all when it is on none.
struct RoadmapMembershipsSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme
    var task: TaskItem

    @State private var roadmaps: [TaskRoadmap] = []

    var body: some View {
        ExtensionSection(shown: !roadmaps.isEmpty) {
            if !roadmaps.isEmpty {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) {
                        Text("Roadmaps").font(.caption).foregroundStyle(theme.textSecondary)
                        ForEach(roadmaps) { roadmap in
                            Button {
                                navigator.open(.roadmap(project: roadmap.projectAlias, roadmapId: roadmap.id))
                            } label: {
                                Label(roadmap.title, systemImage: "chart.line.uptrend.xyaxis")
                                    .font(.caption.weight(.medium))
                            }
                            .buttonStyle(.clearGlass)
                        }
                    }
                }
                .scrollIndicators(.hidden)
            }
        }
        .task(id: task.id) {
            roadmaps = (try? await session.api.roadmaps(ofTask: task.id)) ?? []
        }
    }
}

/// The timeline's instance-wide settings, in Admin settings. Changes apply the
/// next time a roadmap loads.
struct RoadmapSettingsPanel: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State private var saved: RoadmapSettings?
    @State private var form = RoadmapSettings.standard
    @State private var failed = false
    @State private var saving = false

    private static let week: [Weekday] = [.monday, .tuesday, .wednesday, .thursday, .friday, .saturday, .sunday]

    private var valid: Bool {
        RoadmapLimits.boundDays.contains(form.pastDays) && RoadmapLimits.boundDays.contains(form.futureDays)
            && RoadmapLimits.sprintWeeks.contains(form.sprintLengthWeeks)
    }

    var body: some View {
        Section {
            if saved != nil {
                LabeledContent("Days before today") { number($form.pastDays) }
                LabeledContent("Days after today") { number($form.futureDays) }
                Picker("Sprints start on", selection: $form.sprintStartDay) {
                    ForEach(Self.week) { Text($0.rawValue).tag($0) }
                }
                Stepper("Sprint length: \(form.sprintLengthWeeks) \(form.sprintLengthWeeks == 1 ? "week" : "weeks")",
                        value: $form.sprintLengthWeeks, in: RoadmapLimits.sprintWeeks)
                Button("Save Roadmap Settings") { Task { await save() } }
                    .disabled(saving || !valid || form == saved)
            } else if failed {
                Text("Couldn't load the roadmap settings.").foregroundStyle(theme.error)
            } else {
                ProgressView()
            }
        } header: {
            Text("Roadmaps")
        } footer: {
            if saved != nil {
                Text(readBack)
            }
        }
        .themedRow()
        .task {
            do {
                let fetched = try await session.api.roadmapSettings()
                saved = fetched
                form = fetched
            } catch {
                failed = true
            }
        }
    }

    private func number(_ value: Binding<Int>) -> some View {
        TextField("Days", value: value, format: .number.grouping(.never))
            .labelsHidden()
            .keyboardType(.numberPad)
            .multilineTextAlignment(.trailing)
    }

    /// Says what the sprint settings mean in days the reader can check against a calendar.
    private var readBack: String {
        guard valid else {
            return "Bounds are 1–3650 days; a sprint lasts 1–52 weeks."
        }
        let start = RoadmapSprints.sprintStart(.today, startDay: form.sprintStartDay, weeks: form.sprintLengthWeeks)
        let end = start.adding(days: form.sprintLengthWeeks * 7 - 1)
        return "The current sprint runs \(RoadmapDates.range(start, end)). A change applies the next time a roadmap loads."
    }

    private func save() async {
        saving = true
        defer { saving = false }
        if let result = await session.save({ try await session.api.saveRoadmapSettings(form) }) {
            saved = result
            form = result
            session.toasts.show("Saved.", .success)
        } else if let fresh = try? await session.api.roadmapSettings() {
            saved = fresh
            form = fresh
        }
    }
}
