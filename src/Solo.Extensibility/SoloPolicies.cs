namespace Solo.Extensibility;

/// <summary>
/// Core authorization policy names, re-exported so an extension's controllers
/// authorize exactly the way core's do rather than inventing their own checks.
/// </summary>
/// <remarks>
/// These values must match <c>Solo.Api.Auth.AuthConstants</c>. They are
/// duplicated rather than referenced because this assembly may not depend on
/// Solo.Api — <c>ExtensionPolicyNamesTests</c> asserts the two agree, because a
/// silent drift here shows up as a 500 on every extension endpoint.
///
/// <para>The project policies resolve a project from the route, so an endpoint
/// using them must carry a project, board, epic, task or sub-task id. An
/// extension endpoint with no such route value should use a plain
/// <c>[Authorize]</c> and scope its reads through <see cref="ISoloQuery"/>.</para>
/// </remarks>
public static class SoloPolicies
{
    public const string ProjectRead = "Project:Read";
    public const string ProjectWrite = "Project:Write";
    public const string ProjectOwner = "Project:Owner";
    public const string SuperAdmin = "SuperAdmin";
}
