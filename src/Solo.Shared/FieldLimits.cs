namespace Solo.Shared;

/// <summary>
/// Character limits shared by the request DTOs (server-side validation) and the
/// client's text fields (maxlength), so both ends enforce the same numbers.
/// </summary>
public static class FieldLimits
{
    /// <summary>Project and board names.</summary>
    public const int EntityName = 200;

    /// <summary>Epic, task and sub-task names.</summary>
    public const int ItemName = 300;

    public const int LabelName = 100;

    /// <summary>Stored icon names (see SoloIcons on the client).</summary>
    public const int IconName = 100;

    /// <summary>RFC 5321's maximum address length.</summary>
    public const int Email = 320;

    /// <summary>Epic/task descriptions — the longest free-text field.</summary>
    public const int Description = 20_000;

    public const int Comment = 5_000;
}
