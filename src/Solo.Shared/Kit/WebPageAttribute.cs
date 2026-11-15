namespace Solo.Shared.Kit;

/// <summary>
/// Marks a page as one for the web client to draw, wherever it is shown
/// (contract 1.17).
/// </summary>
/// <remarks>
/// <para>A client that is not a browser draws a page itself when the page is
/// built from the kit, with controls of its own, and that is what a page gets
/// by saying nothing. A page that needs more than the kit has — a chart, a
/// canvas, an editor, a MudBlazor table — says so with this, and such a client
/// shows it from the web client instead, inside its own bars.</para>
///
/// <para>It is a statement about the whole page; there is no mixing the two on
/// one screen. A page that leaves this off and still renders something that is
/// not a kit component is shown from the web as well, once that is seen, so
/// nothing is lost — but the person watches it change over, where a marked page
/// opens as what it is. A page that takes anything from its query string should
/// be marked too: only a page the web client itself is showing has one.</para>
///
/// <para>In a browser this changes nothing.</para>
/// </remarks>
/// <example><code>
/// @page "/burndown/{ProjectAlias}"
/// @attribute [WebPage]
/// </code></example>
[AttributeUsage(AttributeTargets.Class)]
public sealed class WebPageAttribute : Attribute;
