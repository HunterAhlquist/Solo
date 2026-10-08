using Solo.Extensibility;

namespace Solo.Extensions;

/// <summary>
/// Who <see cref="SoloQuery"/> answers for in a scope that background work
/// opened, in place of the request's caller. Null everywhere else.
/// </summary>
/// <remarks>
/// Scoped, and set only by <see cref="ExtensionBackground.RunAsAsync"/> on the
/// scope it creates, so it can't leak into a request or another extension's
/// work.
/// </remarks>
public sealed class ExtensionCallerOverride
{
    public ExtensionCaller? Caller { get; set; }
}
