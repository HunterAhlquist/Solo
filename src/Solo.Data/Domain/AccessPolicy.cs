using Solo.Shared;

namespace Solo.Data.Domain;

public enum ProjectAccessLevel
{
    /// <summary>GETs and SignalR board-group membership.</summary>
    Read = 0,

    /// <summary>All mutations.</summary>
    Write = 1,

    /// <summary>Member management, project settings, audit queries (owner or super admin).</summary>
    Owner = 2,
}

/// <summary>Pure access decisions; kept free of EF so they unit-test trivially.</summary>
public static class AccessPolicy
{
    /// <param name="role">The caller's membership role, or null when not a member.</param>
    /// <param name="isOwner">True when the caller's membership carries the owner flag.</param>
    /// <param name="isSuperAdmin">Instance super admin (audit/owner surface only).</param>
    public static bool Allows(ProjectRole? role, bool isOwner, bool isSuperAdmin, ProjectAccessLevel level) =>
        level switch
        {
            ProjectAccessLevel.Read => isOwner || role is not null,
            ProjectAccessLevel.Write => isOwner || role == ProjectRole.ReadWrite,
            ProjectAccessLevel.Owner => isOwner || isSuperAdmin,
            _ => false,
        };
}
