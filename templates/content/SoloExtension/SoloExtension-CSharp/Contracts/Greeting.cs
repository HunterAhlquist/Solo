namespace SoloExtension_CSharp;

/// <summary>
/// The greeting endpoint's response.
#if (Client)
/// Compiled into both halves — the client project links this folder — so the
/// package owns its API shapes without a third assembly.
#endif
/// </summary>
public sealed record Greeting(string Message);
