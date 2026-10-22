namespace Solo.Shared.Extensibility;

[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownEditorActionAttribute : Attribute
{
    public int Order { get; set; }
}

/// <summary>Contributes insertion actions to descriptions and comments (contract 1.10).</summary>
public interface ISoloMarkdownEditorAction
{
    /// <summary>Cheap buttons for this editor; a failure or more than one second omits this contributor.</summary>
    ValueTask<IReadOnlyList<MarkdownEditorButton>> ButtonsAsync(MarkdownEditorContext context, CancellationToken ct);

    /// <summary>Text to replace the selection, or null to leave the editor unchanged.</summary>
    Task<string?> OnChosenAsync(string buttonId, MarkdownEditorContext context, CancellationToken ct);
}

public sealed record MarkdownEditorButton(string Id, string Label)
{
    public string? Icon { get; init; }
}

public sealed record MarkdownEditorContext(Guid ProjectId, MarkdownSurface Surface, Guid EntityId, string? SelectedText);
public enum MarkdownSurface { TaskDescription, EpicDescription, Comment }

[AttributeUsage(AttributeTargets.Class)]
public sealed class MarkdownLinkProviderAttribute : Attribute
{
    public int Order { get; set; }
}

/// <summary>Supplies current labels for an extension's links. Core calls on every render; providers own caching.</summary>
public interface ISoloMarkdownLinkProvider
{
    /// <summary>Cheap and synchronous; never do I/O here.</summary>
    bool Claims(string url);

    /// <summary>Plain-text labels keyed by URL; omitted URLs retain their written labels.</summary>
    ValueTask<IReadOnlyDictionary<string, string>> LabelsAsync(IReadOnlyList<string> urls, CancellationToken ct);
}
