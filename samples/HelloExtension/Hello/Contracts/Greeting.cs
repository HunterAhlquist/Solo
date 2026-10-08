namespace Hello;

/// <summary>
/// The greeting endpoint's response. Compiled into both halves — the client
/// project links this folder — so the package owns its API shapes without a
/// third assembly.
/// </summary>
public sealed record Greeting(string Message);
