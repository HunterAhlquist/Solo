import SwiftUI

// MARK: - Members

struct MembersSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var projectId: UUID
    var isOwner: Bool
    var onChanged: () async -> Void

    @State private var members: [ProjectMember]?
    @State private var inviteEmail = ""
    @State private var inviteRole: ProjectRole = .read
    @State private var busy = false
    @State private var confirmation: Confirmation?

    private var api: APIClient { session.api }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            if isOwner {
                VStack(alignment: .leading, spacing: 10) {
                    SectionTitle("Invite by email")
                    TextField("name@example.com", text: $inviteEmail)
                        .textContentType(.emailAddress)
                        .keyboardType(.emailAddress)
                        .textInputAutocapitalization(.never)
                        .autocorrectionDisabled()
                        .submitLabel(.send)
                        .onSubmit { Task { await invite() } }
                    HStack {
                        Picker("Role", selection: $inviteRole) {
                            Text("Read only").tag(ProjectRole.read)
                            Text("Read/write").tag(ProjectRole.readWrite)
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        Button("Invite") { Task { await invite() } }
                            .buttonStyle(.clearGlass)
                            .disabled(inviteEmail.nilIfBlank == nil || busy)
                    }
                }
                .card(padding: 14)
            }

            if let members {
                VStack(spacing: 0) {
                    ForEach(members) { member in
                        row(member, isLastOwner: member.isOwner && members.count(where: \.isOwner) == 1)
                        if member.id != members.last?.id {
                            Divider().overlay(theme.divider).padding(.leading, 58)
                        }
                    }
                }
                .card(padding: 0)
            } else {
                ProgressView()
            }
        }
        .task(id: projectId) { await load() }
        .confirmation($confirmation)
    }

    private func row(_ member: ProjectMember, isLastOwner: Bool) -> some View {
        HStack(spacing: 12) {
            UserAvatar(member: member, size: 34)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(member.name).font(.subheadline.weight(.medium)).lineLimit(1)
                    if member.isPending {
                        Badge(text: "Pending", color: theme.warning.opacity(0.25), foreground: theme.textPrimary)
                            .accessibilityHint("Invited, but hasn't signed in yet.")
                    }
                }
                if member.displayName != nil {
                    Text(member.email).font(.caption).foregroundStyle(theme.textSecondary).lineLimit(1)
                }
            }
            Spacer(minLength: 8)
            if isOwner {
                Menu {
                    Picker("Role", selection: Binding(get: { member.role }, set: { role in
                        Task { await update(member, role: role, isOwner: member.isOwner) }
                    })) {
                        Text("Read only").tag(ProjectRole.read)
                        Text("Read/write").tag(ProjectRole.readWrite)
                    }
                    // The server refuses to demote or remove the last owner;
                    // the rule is mirrored here instead of offering a doomed toggle.
                    Toggle("Owner", isOn: Binding(get: { member.isOwner }, set: { owner in
                        Task { await update(member, role: member.role, isOwner: owner) }
                    }))
                    .menuToggles()
                    .disabled(isLastOwner)
                    if member.isPending, session.config.emailEnabled == true {
                        Button("Resend Invite", systemImage: "envelope.arrow.triangle.branch") {
                            Task {
                                if await session.perform({ try await api.resendInvite(projectId, member.id) }) {
                                    session.toasts.show("Invite sent to \(member.email).", .success)
                                }
                            }
                        }
                    }
                    Divider()
                    Button("Remove from Project", systemImage: "person.badge.minus", role: .destructive) {
                        confirmation = Confirmation(title: "Remove member",
                                                    message: "Remove \(member.name) from this project?", action: "Remove") {
                            if await session.perform({ try await api.removeMember(projectId, member.id) }) {
                                await load()
                                await onChanged()
                            }
                        }
                    }
                    .disabled(isLastOwner)
                    if isLastOwner {
                        Text("A project must keep at least one owner.")
                    }
                } label: {
                    roleSummary(member)
                }
            } else {
                roleSummary(member)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    private func roleSummary(_ member: ProjectMember) -> some View {
        HStack(spacing: 6) {
            if member.isOwner {
                Badge(text: "Owner", color: theme.accentWash, foreground: theme.primary)
            }
            Text(member.role == .readWrite ? "Read/write" : "Read only")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)
            if isOwner {
                Image(systemName: "chevron.up.chevron.down").font(.caption2).foregroundStyle(theme.textSecondary)
            }
        }
    }

    private func load() async {
        guard let fetched = try? await api.members(projectId) else {
            members = members ?? []
            return
        }
        members = fetched.sorted {
            ($0.isOwner ? 0 : 1, $0.name.lowercased()) < ($1.isOwner ? 0 : 1, $1.name.lowercased())
        }
    }

    private func invite() async {
        guard let email = inviteEmail.nilIfBlank else { return }
        busy = true
        defer { busy = false }
        if let invited = await session.save({ try await api.inviteMember(projectId, email: email, role: inviteRole) }) {
            session.toasts.show(session.config.emailEnabled == true
                ? "Invite sent to \(invited.email)."
                : "Invite saved. Email is off on this instance, so let \(invited.email) know yourself.", .success)
            inviteEmail = ""
            await load()
            await onChanged()
        }
    }

    private func update(_ member: ProjectMember, role: ProjectRole, isOwner: Bool) async {
        let saved = await session.save { try await api.updateMember(projectId, member.id, role: role, isOwner: isOwner) }
        await load()
        if saved != nil {
            await onChanged()
        }
    }
}

// MARK: - Labels

/// Labels, split by what they apply to. Scoped to a board this edits that
/// board's own labels — the ones its items can carry — and offers a reset back
/// to the project defaults. Scoped to a project it edits the defaults: the
/// template new boards copy, which no existing board follows.
struct LabelsSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var scope: LabelScope
    var canWrite: Bool

    @State private var labels: [SoloLabel]?
    @State private var editing: LabelDraft?
    @State private var confirmation: Confirmation?

    private var api: APIClient { session.api }

    private var boardId: UUID? {
        if case .board(let id) = scope { return id }
        return nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text(boardId != nil
                ? "Only this board uses these labels."
                : "New boards start from these. Boards that already exist keep their own.")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)

            if let labels {
                ForEach([LabelKind.task, .subTask], id: \.self) { kind in
                    let ofKind = labels.filter { $0.kind == kind }
                    VStack(alignment: .leading, spacing: 0) {
                        HStack {
                            Text(kind == .task ? "Task labels" : "Sub-task labels").font(.headline)
                            Spacer()
                            if canWrite {
                                Button("Add label", systemImage: "plus") {
                                    editing = LabelDraft(kind: kind)
                                }
                                .labelStyle(.iconOnly)
                            }
                        }
                        .padding(12)
                        if ofKind.isEmpty {
                            Text("None yet.").font(.subheadline).foregroundStyle(theme.textSecondary)
                                .padding([.horizontal, .bottom], 12)
                        }
                        ForEach(ofKind) { label in
                            Divider().overlay(theme.divider)
                            HStack {
                                LabelChip(label: label)
                                Spacer()
                                if canWrite {
                                    Menu("Label actions", systemImage: "ellipsis") {
                                        Button("Edit", systemImage: "pencil") { editing = LabelDraft(label: label) }
                                        Button("Delete", systemImage: "trash", role: .destructive) { confirmDelete(label) }
                                    }
                                    .labelStyle(.iconOnly)
                                    .frame(width: 32, height: 32)
                                }
                            }
                            .padding(.horizontal, 12)
                            .padding(.vertical, 8)
                        }
                    }
                    .card(padding: 0)
                }

                if let boardId, canWrite {
                    Button("Reset to Project Defaults", systemImage: "arrow.counterclockwise") {
                        confirmation = Confirmation(
                            title: "Reset labels",
                            message: "Put this board's labels back to the project defaults? Edits to them are undone; "
                                + "labels this board added itself are deleted, and items using those lose their label.",
                            action: "Reset") {
                            await session.save { try await api.resetBoardLabels(boardId) }
                            await load()
                        }
                    }
                    .buttonStyle(.clearGlass)
                }
            } else {
                ProgressView()
            }
        }
        .task(id: scope) { await load() }
        .confirmation($confirmation)
        .sheet(item: $editing) { draft in
            LabelEditorSheet(draft: draft) { edited in
                await save(edited)
            }
        }
    }

    private func load() async {
        do {
            switch scope {
            case .project(let id): labels = try await api.projectLabels(id)
            case .board(let id): labels = try await api.boardLabels(id)
            }
        } catch {
            labels = labels ?? []
        }
    }

    private func save(_ draft: LabelDraft) async {
        if let existing = draft.existing {
            await session.save {
                try await api.updateLabel(scope, existing.id, LabelRequest(
                    name: draft.name, color: draft.color, icon: draft.icon, imageId: draft.imageId, version: existing.version))
            }
        } else {
            await session.save {
                try await api.createLabel(scope, LabelRequest(
                    kind: draft.kind, name: draft.name, color: draft.color, icon: draft.icon, imageId: draft.imageId))
            }
        }
        await load()
    }

    private func confirmDelete(_ label: SoloLabel) {
        confirmation = Confirmation(
            title: "Delete label",
            message: "Delete the label \"\(label.name)\"? Items using it keep their other fields.") {
            await session.perform { try await api.deleteLabel(scope, label.id) }
            await load()
        }
    }
}

struct LabelDraft: Identifiable {
    let id = UUID()
    var existing: SoloLabel?
    var kind: LabelKind
    var name = ""
    var color = "#8d7ffa"
    var icon: String?
    var imageId: UUID?

    init(kind: LabelKind) {
        self.kind = kind
    }

    init(label: SoloLabel) {
        existing = label
        kind = label.kind
        name = label.name
        color = label.color
        icon = label.icon
        imageId = label.imageId
    }
}

struct LabelEditorSheet: View {
    @Environment(\.dismiss) private var dismiss
    @Environment(\.theme) private var theme

    @State var draft: LabelDraft
    var onSave: (LabelDraft) async -> Void

    @State private var colour: Color
    @FocusState private var nameFocused: Bool

    init(draft: LabelDraft, onSave: @escaping (LabelDraft) async -> Void) {
        _draft = State(initialValue: draft)
        _colour = State(initialValue: Color(hex: draft.color))
        self.onSave = onSave
    }

    var body: some View {
        NavigationStack {
            Form {
                Section {
                    TextField("Name", text: $draft.name)
                        .focused($nameFocused)
                    ColorPicker("Colour", selection: $colour, supportsOpacity: false)
                }
                .themedRow()

                Section("Icon (optional)") {
                    IconImagePicker(icon: $draft.icon, imageId: $draft.imageId)
                }
                .themedRow()

                Section("Preview") {
                    LabelChip(label: SoloLabel(
                        id: UUID(), projectId: UUID(), boardId: nil, kind: draft.kind,
                        name: draft.name.nilIfBlank ?? "label", color: draft.color, icon: draft.icon,
                        imageId: draft.imageId, version: 0))
                }
                .themedRow()
            }
            .themedList()
            .navigationTitle(draft.existing == nil ? "New label" : "Edit label")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button("Cancel") { dismiss() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button(draft.existing == nil ? "Create" : "Save") {
                        var saved = draft
                        saved.name = String(draft.name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(FieldLimits.labelName))
                        dismiss()
                        Task { await onSave(saved) }
                    }
                    .fontWeight(.semibold)
                    .disabled(draft.name.nilIfBlank == nil)
                }
            }
            .onChange(of: colour) { _, value in
                // Stored as #rrggbb, whatever colour space the picker worked in.
                let resolved = PlatformColor(value).cgColor.converted(to: CGColorSpace(name: CGColorSpace.sRGB)!,
                                                                intent: .defaultIntent, options: nil)
                if let parts = resolved?.components, parts.count >= 3 {
                    draft.color = CSSColor(red: parts[0], green: parts[1], blue: parts[2]).hex
                }
            }
            .onAppear { nameFocused = draft.existing == nil }
        }
    }
}

// MARK: - Audit

/// The audit trail is column-level: one row per changed column, carrying the
/// previous value. Paged by the server — it grows without bound until
/// retention prunes it — and read here as an endless list.
struct AuditSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    var projectId: UUID
    var members: [ProjectMember]

    private static let tables = ["Projects", "ProjectMembers", "Boards", "Epics", "Tasks", "SubTasks", "Labels",
                                 "Comments", "Assignments"]
    private static let pageSize = 50

    @State private var entries: [AuditEntry] = []
    @State private var total: Int?
    @State private var page = 0
    @State private var loading = false
    @State private var table: String?
    @State private var userId: UUID?
    @State private var from: Day?
    @State private var to: Day?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(spacing: 0) {
                FieldRow(title: "Table", symbol: "tablecells") {
                    Menu {
                        Picker("Table", selection: $table) {
                            Text("Any").tag(String?.none)
                            ForEach(Self.tables, id: \.self) { Text($0).tag(String?.some($0)) }
                        }
                    } label: { FieldValue(text: table ?? "Any", dimmed: table == nil) }
                }
                Divider().overlay(theme.divider)
                FieldRow(title: "User", symbol: "person") {
                    Menu {
                        Picker("User", selection: $userId) {
                            Text("Anyone").tag(UUID?.none)
                            ForEach(members.filter { $0.userId != nil }) { Text($0.name).tag($0.userId) }
                        }
                    } label: {
                        FieldValue(text: members.first { $0.userId == userId && userId != nil }?.name ?? "Anyone",
                                   dimmed: userId == nil)
                    }
                }
                Divider().overlay(theme.divider)
                FieldRow(title: "From", symbol: "calendar") {
                    DayField(day: from, placeholder: "Any time") { from = $0 }
                }
                Divider().overlay(theme.divider)
                FieldRow(title: "To", symbol: "calendar") {
                    DayField(day: to, placeholder: "Now") { to = $0 }
                }
            }
            .card(padding: 0)

            if let total {
                Text("\(total.formatted()) \(total == 1 ? "entry" : "entries")")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
            }

            LazyVStack(spacing: 8) {
                ForEach(entries) { entry in
                    row(entry)
                        .onAppear {
                            if entry.id == entries.last?.id {
                                Task { await loadMore() }
                            }
                        }
                }
                if loading {
                    ProgressView().padding()
                } else if entries.isEmpty, total != nil {
                    Text("No audit entries match those filters.")
                        .font(.subheadline)
                        .foregroundStyle(theme.textSecondary)
                        .padding(.vertical, 20)
                }
            }
        }
        .task(id: FilterKey(table: table, userId: userId, from: from, to: to)) { await reload() }
    }

    private struct FilterKey: Hashable {
        var table: String?
        var userId: UUID?
        var from: Day?
        var to: Day?
    }

    private func row(_ entry: AuditEntry) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Text(entry.tableName).font(.footnote.weight(.semibold))
                if let column = entry.columnName {
                    Text("· \(column)").font(.footnote).foregroundStyle(theme.textSecondary)
                }
                Spacer()
                Text(entry.timestamp.stamp).font(.caption).foregroundStyle(theme.textSecondary)
            }
            HStack(spacing: 6) {
                Image(systemName: "person.crop.circle").font(.caption).foregroundStyle(theme.textSecondary)
                Text(entry.userDisplayName ?? "System").font(.caption)
                ForEach((entry.annotations ?? [:]).values.sorted(), id: \.self) { annotation in
                    Text(annotation).font(.caption).foregroundStyle(theme.textSecondary)
                }
            }
            if let previous = entry.previousValue {
                Text(previous)
                    .font(.caption.monospaced())
                    .foregroundStyle(theme.textSecondary)
                    .lineLimit(3)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(6)
                    .background(theme.backgroundGray, in: RoundedRectangle(cornerRadius: 6))
                    .accessibilityLabel("Previous value: \(previous)")
            }
        }
        .card(padding: 10)
    }

    private func reload() async {
        entries = []
        total = nil
        page = 0
        await loadMore()
    }

    private func loadMore() async {
        guard !loading, total.map({ entries.count < $0 }) ?? true else { return }
        loading = true
        defer { loading = false }
        do {
            let result = try await session.api.audit(projectId, table: table, userId: userId, from: from?.date(),
                                                     to: to?.adding(days: 1).date(), page: page + 1, pageSize: Self.pageSize)
            guard !Task.isCancelled else { return }
            page = result.page
            total = result.totalCount
            entries += result.items.filter { new in !entries.contains { $0.id == new.id } }
        } catch {
            total = total ?? 0
            session.toasts.error(error, fallback: "Couldn't load the audit trail.")
        }
    }
}

// MARK: - Settings

struct ProjectSettingsSection: View {
    @Environment(Session.self) private var session
    @Environment(Navigator.self) private var navigator
    @Environment(\.theme) private var theme

    var project: Project
    var onSaved: (Project) -> Void

    @State private var name = ""
    @State private var alias = ""
    @State private var icon: String?
    @State private var imageId: UUID?
    @State private var confirmation: Confirmation?
    @FocusState private var focus: Field?

    private enum Field { case name, alias }

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            VStack(alignment: .leading, spacing: 12) {
                SectionTitle("Project")
                // Edits commit on leaving the field, never per keystroke.
                TextField("Name", text: $name)
                    .focused($focus, equals: .name)
                    .submitLabel(.done)
                Divider().overlay(theme.divider)
                TextField("Alias", text: Binding(get: { alias }, set: { alias = String($0.uppercased().prefix(Aliases.maxLength)) }))
                    .font(.body.monospaced())
                    .textInputAutocapitalization(.characters)
                    .autocorrectionDisabled()
                    .focused($focus, equals: .alias)
                    .submitLabel(.done)
                Text("Stands in for the id in links and serial numbers — capital letters and digits, max 8.")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                Divider().overlay(theme.divider)
                IconImagePicker(icon: $icon, imageId: $imageId)
            }
            .card(padding: 14)

            // What extensions add to a project's settings: below core's fields and above the
            // danger zone, as on the web.
            KitContributions(seam: .projectSettings, project: project.id) { SectionTitle($0) }

            VStack(alignment: .leading, spacing: 8) {
                SectionTitle("Danger zone")
                Text("Deleting a project removes its boards, tasks and history. There is no undo.")
                    .font(.footnote)
                    .foregroundStyle(theme.textSecondary)
                Button("Delete Project", systemImage: "trash", role: .destructive) {
                    confirmation = Confirmation(title: "Delete project",
                                                message: "Permanently delete \"\(project.name)\" and everything in it?",
                                                action: "Delete Forever") {
                        if await session.perform({ try await session.api.deleteProject(project.id) }) {
                            await session.projects.refreshProjects()
                            navigator.popToRoot(.projects)
                        }
                    }
                }
                .buttonStyle(.clearGlass)
                .tint(theme.error)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .card(padding: 14, outline: theme.error.opacity(0.5))
        }
        .frame(maxWidth: 560, alignment: .leading)
        .onAppear(perform: seed)
        .onChange(of: project.version) { _, _ in seed() }
        .onChange(of: focus) { _, value in
            if value == nil { Task { await save() } }
        }
        .onChange(of: icon) { _, _ in Task { await save() } }
        .onChange(of: imageId) { _, _ in Task { await save() } }
        .confirmation($confirmation)
    }

    private func seed() {
        name = project.name
        alias = project.alias
        icon = project.icon
        imageId = project.imageId
    }

    private func save() async {
        let newAlias = Aliases.normalize(alias)
        let newName = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(FieldLimits.entityName))
        guard !newName.isEmpty, Aliases.isValid(newAlias) else {
            seed()
            return
        }
        guard newName != project.name || newAlias != project.alias || icon != project.icon || imageId != project.imageId
        else { return }
        do {
            let updated = try await session.api.updateProject(project.id, ProjectRequest(
                name: newName, alias: newAlias, icon: icon, imageId: imageId, version: project.version))
            await session.projects.refreshProjects()
            onSaved(updated)
        } catch let error as APIError where error.isConflict {
            session.toasts.show("Updated by someone else — your view was refreshed.", .info)
            if let fresh = try? await session.api.project(project.id) {
                onSaved(fresh)
            }
        } catch {
            session.toasts.error(error, fallback: "That didn't save.")
            seed()
        }
    }
}
