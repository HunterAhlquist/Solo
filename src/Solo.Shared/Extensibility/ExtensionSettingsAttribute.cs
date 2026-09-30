namespace Solo.Shared.Extensibility;

/// <summary>
/// Marks a component rendered inside its own extension's entry on Admin
/// settings. Components receive ExtensionId (string). Each panel has an error
/// boundary; only loaded extension assemblies can contribute, and a disabled
/// extension or one whose package is missing gets none. The page is for super
/// admins, but the panel's endpoints must still guard themselves with
/// <c>SoloPolicies.SuperAdmin</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ExtensionSettingsAttribute : Attribute;
