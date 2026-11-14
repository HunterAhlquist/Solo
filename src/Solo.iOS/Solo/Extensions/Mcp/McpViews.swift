import Charts
import SwiftUI

/// The MCP extension's page: generate a token, copy the config your client
/// wants — and, for a super admin, audit every token on the instance.
struct McpAccessView: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme

    @State private var info: McpInfo?
    @State private var status: McpTokenStatus?
    @State private var audit: McpTokenAdmin?
    @State private var secret: String?
    @State private var description = ""
    @State private var failed: String?
    @State private var loading = true
    @State private var busy = false
    @State private var client: Client = .claudeCode
    @State private var confirmation: Confirmation?
    @State private var prompt: TextPrompt?

    private static let revokeAllPhrase = "revoke all tokens"
    private var api: APIClient { session.api }

    enum Client: String, CaseIterable, Identifiable {
        case claudeCode = "Claude Code", claudeDesktop = "Claude Desktop", cursor = "Cursor & others"
        case vsCode = "VS Code", other = "Anything else"

        var id: String { rawValue }
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                if let failed {
                    InfoBanner(text: failed, symbol: "exclamationmark.triangle.fill")
                } else if loading {
                    ProgressView().frame(maxWidth: .infinity).padding(.top, 40)
                } else if let info {
                    Text("Connect an AI assistant to Solo to read your boards and create, edit, and move tasks. "
                        + "It acts as you and can access only projects you belong to.")
                        .font(.subheadline)
                        .foregroundStyle(theme.textSecondary)
                    tokenCard
                    connectCard(info)
                    toolsCard(info)
                    if let audit {
                        auditCard(audit)
                    }
                } else {
                    // The package is here but its server half is not: a failed
                    // migration disables it. Say so rather than offering a
                    // token form pointed at nothing.
                    InfoBanner(text: "The MCP package is installed, but its server is disabled. An admin can find the "
                        + "reason under Extensions in Admin settings.", symbol: "exclamationmark.triangle.fill")
                }
            }
            .padding(16)
            .frame(maxWidth: 820, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .themedBackground()
        .navigationTitle("MCP Access")
        .navigationBarTitleDisplayMode(.inline)
        .task { await load() }
        .refreshable { await load() }
        .confirmation($confirmation)
        .textPrompt($prompt)
    }

    // MARK: Your token

    private var tokenCard: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Your token").font(.headline)
            Text("Each account can have one token. Generating a new token immediately revokes the old one and "
                + "disconnects anything using it.")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)

            // Shown once, and never retrievable: the server keeps only a hash.
            if let secret {
                VStack(alignment: .leading, spacing: 8) {
                    Text("**Copy this token now.** It won't appear again. If you lose it, generate a new one.")
                        .font(.footnote)
                    HStack {
                        Text(secret)
                            .font(.footnote.monospaced())
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .textSelection(.enabled)
                        Spacer()
                        Button("Copy token", systemImage: "doc.on.doc") { copy(secret) }
                            .labelStyle(.iconOnly)
                            .buttonStyle(.glass)
                    }
                }
                .padding(12)
                .background(theme.success.opacity(0.16), in: theme.cardShape)
            }

            if let active = status?.active {
                VStack(spacing: 0) {
                    LabeledContent("Created", value: active.createdAt.stamp).padding(.vertical, 6)
                    Divider().overlay(theme.divider)
                    LabeledContent("Last used", value: active.lastUsedAt?.stamp ?? "never").padding(.vertical, 6)
                    if let label = active.description?.nilIfBlank {
                        Divider().overlay(theme.divider)
                        LabeledContent("Label", value: label).padding(.vertical, 6)
                    }
                }
                .font(.subheadline)
                HStack {
                    Button("Regenerate", systemImage: "arrow.trianglehead.2.clockwise") {
                        confirmation = Confirmation(
                            title: "Regenerate token",
                            message: "Your current token will stop working immediately. Connected assistants need the "
                                + "new token to regain access.", action: "Regenerate") { await mint() }
                    }
                    .buttonStyle(.glass)
                    Button("Revoke", systemImage: "nosign", role: .destructive) {
                        confirmation = Confirmation(
                            title: "Revoke token",
                            message: "This token will stop working immediately. You can generate a new one at any time.",
                            action: "Revoke") {
                            busy = true
                            if await session.perform({ try await api.mcpRevokeOwnToken() }) {
                                secret = nil
                            }
                            await reloadTokens()
                            busy = false
                        }
                    }
                    .buttonStyle(.glass)
                    .tint(theme.error)
                }
                .disabled(busy)
            } else {
                TextField("Label (optional)", text: $description)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 10)
                    .background(theme.backgroundGray, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                Text("Name the client or device, e.g. \"Claude Code on the laptop\".")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                Button("Generate Token", systemImage: "key") { Task { await mint() } }
                    .buttonStyle(.glass)
                    .disabled(busy)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 16)
    }

    // MARK: Connect a client

    private func connectCard(_ info: McpInfo) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Connect a client").font(.headline)
            Text("The MCP endpoint uses HTTP at `\(endpoint)` with protocol revision \(info.protocolVersion).")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)

            ChipPicker(options: Client.allCases, selection: $client) { Text($0.rawValue) }
                .padding(.horizontal, -16)

            switch client {
            case .claudeCode:
                snippet("Run this once", claudeCodeCommand)
            case .claudeDesktop:
                note("`claude_desktop_config.json` accepts only servers that Claude Desktop launches locally, so it "
                    + "rejects entries with a `url`. Use `mcp-remote` to connect Claude Desktop to Solo over HTTP. "
                    + "Node is required. Restart Claude Desktop after editing the configuration.")
                snippet("claude_desktop_config.json", claudeDesktopConfig)
                note("The token is passed through `env` because some clients do not escape spaces in `args`, which "
                    + "corrupts an inline authorization header.")
            case .cursor:
                note("Cursor and Windsurf support HTTP directly. Add Solo to the `mcpServers` object in their configuration:")
                snippet("mcp.json", jsonConfig(root: "mcpServers"))
            case .vsCode:
                note("Put this in `.vscode/mcp.json` in your workspace.")
                snippet(".vscode/mcp.json", jsonConfig(root: "servers"))
            case .other:
                note("Use these values with any MCP client that supports streamable HTTP and a custom header:")
                snippet("Endpoint", endpoint)
                snippet("Header", "Authorization: Bearer \(tokenOrPlaceholder)")
                note("Use POST requests only. The endpoint is stateless and does not offer an event stream. Clients "
                    + "that open a GET stream receive a 405 response.")
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 16)
    }

    private func note(_ markdown: String) -> some View {
        Text(LocalizedStringKey(markdown))
            .font(.footnote)
            .foregroundStyle(theme.textSecondary)
    }

    /// A block of configuration meant to be copied verbatim: monospaced,
    /// scrolled sideways rather than wrapped, with a copy button.
    private func snippet(_ title: String, _ text: String) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text(title).font(.caption).foregroundStyle(theme.textSecondary)
                Spacer()
                Button("Copy \(title)", systemImage: "doc.on.doc") { copy(text) }
                    .labelStyle(.iconOnly)
                    .font(.footnote)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            Divider().overlay(theme.divider)
            ScrollView(.horizontal) {
                Text(text)
                    .font(.caption.monospaced())
                    .textSelection(.enabled)
                    .padding(12)
            }
        }
        .background(theme.backgroundGray, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    // MARK: Tools

    private func toolsCard(_ info: McpInfo) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Available tools").font(.headline)
            Text("\(info.tools.count) tools in MCP server version \(info.serverVersion).")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)
            ForEach(info.tools) { tool in
                VStack(alignment: .leading, spacing: 2) {
                    Text(tool.name).font(.footnote.monospaced().weight(.semibold))
                    Text(tool.description).font(.footnote).foregroundStyle(theme.textSecondary)
                }
                .padding(.vertical, 2)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 16)
    }

    // MARK: Admin audit
    // Metadata only — the instance stores hashes, so there is no token here to read.

    private func auditCard(_ audit: McpTokenAdmin) -> some View {
        let live = audit.tokens.count { $0.revokedAt == nil }
        let hours = audit.tokens.map(\.usage.count).max() ?? 0
        let totals = (0..<hours).map { hour in audit.tokens.reduce(0) { $0 + (hour < $1.usage.count ? $1.usage[hour] : 0) } }

        return VStack(alignment: .leading, spacing: 12) {
            HStack {
                Text("All tokens").font(.headline)
                Spacer()
                Button("Revoke All", systemImage: "nosign", role: .destructive) { confirmRevokeAll(live) }
                    .buttonStyle(.glass)
                    .tint(theme.error)
                    .disabled(live == 0)
            }
            Text("Every token generated on this instance. Each token acts as its owner and can access only that "
                + "owner's projects. This shows token metadata and usage, but never the token value.")
                .font(.footnote)
                .foregroundStyle(theme.textSecondary)

            if audit.tokens.isEmpty {
                Text("Nobody has generated one yet.").font(.subheadline).foregroundStyle(theme.textSecondary)
            } else {
                Text("Requests, last 24 hours").font(.subheadline.weight(.semibold))
                Text("Authenticated requests per hour. A tool call can make multiple requests because the endpoint "
                    + "calls the API for each read and write.")
                    .font(.caption)
                    .foregroundStyle(theme.textSecondary)
                Chart(Array(totals.enumerated()), id: \.offset) { hour, count in
                    BarMark(x: .value("Hour", audit.windowStart.addingTimeInterval(TimeInterval(hour) * 3600), unit: .hour),
                            y: .value("Requests", count))
                        .foregroundStyle(theme.primary)
                        .cornerRadius(2)
                }
                .chartXAxis {
                    AxisMarks(values: .stride(by: .hour, count: 6)) { _ in
                        AxisGridLine().foregroundStyle(theme.divider)
                        AxisValueLabel(format: .dateTime.hour())
                    }
                }
                .chartYAxis {
                    AxisMarks(position: .leading) { _ in
                        AxisGridLine().foregroundStyle(theme.divider)
                        AxisValueLabel()
                    }
                }
                .frame(height: 140)
                .accessibilityLabel("\(totals.reduce(0, +)) requests in the last 24 hours")

                ForEach(audit.tokens) { token in
                    Divider().overlay(theme.divider)
                    HStack(alignment: .top) {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(token.owner).font(.subheadline.weight(.medium))
                            if let label = token.description?.nilIfBlank {
                                Text(label).font(.caption).foregroundStyle(theme.textSecondary)
                            }
                            Text("Created \(token.createdAt.stamp) · last used \(token.lastUsedAt?.stamp ?? "never")")
                                .font(.caption)
                                .foregroundStyle(theme.textSecondary)
                        }
                        Spacer()
                        if token.revokedAt != nil {
                            Badge(text: token.revocationLabel, color: theme.backgroundGray, foreground: theme.textSecondary)
                        } else {
                            Badge(text: "live", color: theme.success.opacity(0.25), foreground: theme.textPrimary)
                            // Only a live token has anything to cut off; a revoked row is history.
                            Button("Revoke \(token.owner)'s token", systemImage: "nosign", role: .destructive) {
                                confirmRevoke(token)
                            }
                            .labelStyle(.iconOnly)
                            .foregroundStyle(theme.error)
                            .disabled(busy)
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .card(padding: 16)
    }

    // MARK: Actions

    private func load() async {
        do {
            info = try await api.mcpInfo()
            if info != nil {
                await reloadTokens()
            }
            failed = nil
        } catch {
            failed = (error as? LocalizedError)?.errorDescription ?? "Couldn't load MCP access."
        }
        loading = false
    }

    private func reloadTokens() async {
        status = try? await api.mcpTokens()
        // A 403 here is the answer for everyone who isn't a super admin.
        audit = try? await api.mcpAudit()
    }

    private func mint() async {
        busy = true
        defer { busy = false }
        if let minted = await session.save({ try await api.mcpCreateToken(description: description.nilIfBlank) }) {
            secret = minted.value
            description = ""
        }
        await reloadTokens()
    }

    private func confirmRevoke(_ token: McpTokenAudit) {
        confirmation = Confirmation(
            title: "Revoke \(token.owner)'s token",
            message: "This token will stop working immediately, disconnecting anything \(token.owner) connected. "
                + "They can generate a new token from this page.", action: "Revoke") {
            busy = true
            if await session.perform({ try await api.mcpRevokeToken(token.id) }) {
                session.toasts.show("Revoked \(token.owner)'s token.", .success)
            }
            // Their own token is in this list too, so the card above can go stale from a tap down here.
            await reloadTokens()
            busy = false
        }
    }

    private func confirmRevokeAll(_ live: Int) {
        let noun = live == 1 ? "token" : "tokens"
        confirmation = Confirmation(
            title: "Revoke every MCP token",
            message: "This revokes all \(live) active MCP \(noun) on the instance. Every connected assistant will stop "
                + "working immediately. Each owner must generate a new token from this page.", action: "Continue") {
            prompt = TextPrompt(title: "Type \"\(Self.revokeAllPhrase)\" to confirm", placeholder: "Confirmation",
                                action: "Revoke All", limit: 64) { typed in
                guard typed.lowercased() == Self.revokeAllPhrase else {
                    session.toasts.show("That didn't match. Nothing was revoked.", .warning)
                    return
                }
                if let result = await session.save({ try await api.mcpRevokeAll(confirm: Self.revokeAllPhrase) }) {
                    session.toasts.show("Revoked \(result.revoked) MCP \(result.revoked == 1 ? "token" : "tokens").", .success)
                }
                await reloadTokens()
            }
        }
    }

    private func copy(_ text: String) {
        UIPasteboard.general.string = text
        session.toasts.show("Copied.", .success)
    }

    // MARK: Snippets

    private var endpoint: String { api.mcpEndpoint.absoluteString }
    private var tokenOrPlaceholder: String { secret ?? "YOUR_TOKEN" }

    private var claudeCodeCommand: String {
        "claude mcp add --transport http solo \(endpoint) \\\n  --header \"Authorization: Bearer \(tokenOrPlaceholder)\""
    }

    private var claudeDesktopConfig: String {
        """
        {
          "mcpServers": {
            "solo": {
              "command": "npx",
              "args": [
                "-y",
                "mcp-remote",
                "\(endpoint)",
                "--header",
                "Authorization:${SOLO_TOKEN}"
              ],
              "env": {
                "SOLO_TOKEN": "Bearer \(tokenOrPlaceholder)"
              }
            }
          }
        }
        """
    }

    private func jsonConfig(root: String) -> String {
        """
        {
          "\(root)": {
            "solo": {
              "type": "http",
              "url": "\(endpoint)",
              "headers": {
                "Authorization": "Bearer \(tokenOrPlaceholder)"
              }
            }
          }
        }
        """
    }
}

/// On a task's detail: whether an LLM may pick this task up. Only offered
/// where the extension says the task is eligible.
struct LlmWorkSection: View {
    @Environment(Session.self) private var session
    @Environment(\.theme) private var theme
    var task: TaskItem
    var store: BoardStore

    @State private var state: LlmWorkState?
    @State private var failed = false
    @State private var saving = false

    var body: some View {
        ExtensionSection(shown: state?.eligible == true || failed) {
            if let state, state.eligible {
                Toggle(isOn: Binding(get: { state.llmWork }, set: { save($0) })) {
                    Label("Work for an LLM", systemImage: "cpu")
                        .font(.subheadline)
                }
                .disabled(!store.canWrite || saving)
                .padding(.horizontal, 12)
                .frame(minHeight: 46)
                .card(padding: 0)
            } else if failed {
                Text("Could not load LLM work settings.").font(.caption).foregroundStyle(theme.error)
            }
        }
        // Assignees decide eligibility, so a change to them asks again.
        .task(id: [task.id.uuidString, "\(task.version)", task.assigneeIds.map(\.uuidString).joined()]) {
            do {
                state = try await session.api.llmWork(task.id)
                failed = false
            } catch {
                failed = state == nil
            }
        }
    }

    private func save(_ value: Bool) {
        guard let current = state, store.canWrite, !saving else { return }
        saving = true
        Task {
            defer { saving = false }
            do {
                state = try await session.api.setLlmWork(task.id, value, version: current.version)
            } catch {
                session.toasts.error(error)
                state = try? await session.api.llmWork(task.id)
            }
            await store.refetchTask(task.id)
        }
    }
}
