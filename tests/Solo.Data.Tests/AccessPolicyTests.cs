using Solo.Data.Domain;
using Solo.Shared;
using Xunit;

namespace Solo.Data.Tests;

public class AccessPolicyTests
{
    [Theory]
    [InlineData(ProjectRole.Read, ProjectAccessLevel.Read, true)]
    [InlineData(ProjectRole.Read, ProjectAccessLevel.Write, false)]
    [InlineData(ProjectRole.Read, ProjectAccessLevel.Owner, false)]
    [InlineData(ProjectRole.ReadWrite, ProjectAccessLevel.Read, true)]
    [InlineData(ProjectRole.ReadWrite, ProjectAccessLevel.Write, true)]
    [InlineData(ProjectRole.ReadWrite, ProjectAccessLevel.Owner, false)]
    public void MemberRoles_GateReadAndWrite(ProjectRole role, ProjectAccessLevel level, bool expected) =>
        Assert.Equal(expected, AccessPolicy.Allows(role, isOwner: false, isSuperAdmin: false, level));

    [Theory]
    [InlineData(ProjectAccessLevel.Read)]
    [InlineData(ProjectAccessLevel.Write)]
    [InlineData(ProjectAccessLevel.Owner)]
    public void Owners_PassEveryLevel(ProjectAccessLevel level) =>
        Assert.True(AccessPolicy.Allows(ProjectRole.Read, isOwner: true, isSuperAdmin: false, level));

    [Theory]
    [InlineData(ProjectAccessLevel.Read, false)]
    [InlineData(ProjectAccessLevel.Write, false)]
    [InlineData(ProjectAccessLevel.Owner, true)]
    public void SuperAdmin_WithoutMembership_OnlyReachesOwnerSurface(ProjectAccessLevel level, bool expected) =>
        Assert.Equal(expected, AccessPolicy.Allows(null, isOwner: false, isSuperAdmin: true, level));

    [Theory]
    [InlineData(ProjectAccessLevel.Read)]
    [InlineData(ProjectAccessLevel.Write)]
    [InlineData(ProjectAccessLevel.Owner)]
    public void NonMembers_AreDeniedEverywhere(ProjectAccessLevel level) =>
        Assert.False(AccessPolicy.Allows(null, isOwner: false, isSuperAdmin: false, level));
}
