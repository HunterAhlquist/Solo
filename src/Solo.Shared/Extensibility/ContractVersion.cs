namespace Solo.Shared.Extensibility;

/// <summary>
/// The version of this contract. Extensions declare the range they support in
/// their manifest and the host refuses to load a package outside it.
/// </summary>
/// <remarks>
/// Two ranges exist because they move independently. <c>soloApiVersion</c> is
/// checked on the server against this constant; <c>soloClientVersion</c> is
/// checked in the browser against the running client build, which in the
/// Electron shell is bundled with the app and can be older than the instance.
/// On the hosted web build the two are always the same number.
/// </remarks>
public static class ContractVersion
{
    public const string Current = "1.0.0";

    public static readonly Version Parsed = new(1, 0, 0);
}
