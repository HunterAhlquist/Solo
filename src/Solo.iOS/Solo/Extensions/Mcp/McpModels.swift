import Foundation

// The MCP extension's wire types and endpoints (api/ext/mcp/…).

nonisolated struct McpToken: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var description: String?
    var createdAt: Date
    var lastUsedAt: Date?
    var revokedAt: Date?
    var revokedReason: String?
}

/// A freshly minted token. `value` is shown once: the server keeps only a hash.
nonisolated struct McpTokenSecret: Codable, Sendable {
    var token: McpToken
    var value: String
}

nonisolated struct McpTokenStatus: Codable, Sendable {
    var active: McpToken?
    var history: [McpToken]
}

nonisolated struct McpTokenAudit: Codable, Hashable, Sendable, Identifiable {
    var id: UUID
    var userEmail: String?
    var userDisplayName: String?
    var description: String?
    var createdAt: Date
    var lastUsedAt: Date?
    var revokedAt: Date?
    var revokedReason: String?
    /// Authenticated requests per hour, oldest first.
    var usage: [Int]

    var owner: String { userDisplayName ?? userEmail ?? "unknown" }

    var revocationLabel: String {
        switch revokedReason {
        case "Regenerated": "replaced"
        case "RevokedByOwner": "revoked by owner"
        case "RevokedByAdmin": "revoked by admin"
        default: "revoked"
        }
    }
}

nonisolated struct McpTokenAdmin: Codable, Sendable {
    var windowStart: Date
    var tokens: [McpTokenAudit]
}

nonisolated struct McpInfo: Codable, Sendable {
    var protocolVersion: String
    var serverVersion: String
    var tools: [McpTool]
}

nonisolated struct McpTool: Codable, Hashable, Sendable, Identifiable {
    var name: String
    var title: String
    var description: String
    var id: String { name }
}

nonisolated struct McpRevokeAllResult: Codable, Sendable {
    var revoked: Int
}

/// Whether a task is flagged as work an LLM may pick up.
nonisolated struct LlmWorkState: Codable, Sendable {
    var taskId: UUID
    var eligible: Bool
    var llmWork: Bool
    var assigneeIds: [UUID]
    var version: UInt32
}

nonisolated private struct LlmWorkRequest: Encodable, Sendable {
    var llmWork: Bool
    var version: UInt32
}

extension APIClient {
    /// The address an MCP client is given: the instance's own, as a person would type it.
    var mcpEndpoint: URL { url("api/ext/mcp") }

    /// Nil when the package is installed but its server half is disabled.
    func mcpInfo() async throws -> McpInfo? {
        do {
            return try await get("api/ext/mcp/info")
        } catch let error as APIError where error.isNotFound || error.status == 503 {
            return nil
        }
    }

    func mcpTokens() async throws -> McpTokenStatus { try await get("api/ext/mcp/tokens") }

    func mcpCreateToken(description: String?) async throws -> McpTokenSecret {
        try await send("POST", "api/ext/mcp/tokens", body: ["description": description])
    }

    func mcpRevokeOwnToken() async throws { try await send("DELETE", "api/ext/mcp/tokens/current") }

    /// Super admin only; anyone else is told so with a 403.
    func mcpAudit() async throws -> McpTokenAdmin { try await get("api/ext/mcp/tokens/admin") }

    func mcpRevokeToken(_ id: UUID) async throws { try await send("POST", "api/ext/mcp/tokens/admin/\(id.api)/revoke") }

    func mcpRevokeAll(confirm: String) async throws -> McpRevokeAllResult {
        try await send("POST", "api/ext/mcp/tokens/admin/revoke-all", body: ["confirm": confirm])
    }

    func llmWork(_ taskId: UUID) async throws -> LlmWorkState { try await get("api/ext/mcp/tasks/\(taskId.api)/llm-work") }

    func setLlmWork(_ taskId: UUID, _ value: Bool, version: UInt32) async throws -> LlmWorkState {
        try await send("PUT", "api/ext/mcp/tasks/\(taskId.api)/llm-work", body: LlmWorkRequest(llmWork: value, version: version))
    }
}
