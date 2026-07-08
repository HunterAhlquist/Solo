namespace Solo.Api.Auth;

public static class AuthConstants
{
    public const string BearerScheme = "Bearer";
    public const string SetupScheme = "Setup";

    public const string SetupAdminRole = "SetupAdmin";

    public const string SuperAdminPolicy = "SuperAdmin";
    public const string SetupPolicy = "SetupAdmin";
    public const string ProjectReadPolicy = "Project:Read";
    public const string ProjectWritePolicy = "Project:Write";
    public const string ProjectOwnerPolicy = "Project:Owner";
}
