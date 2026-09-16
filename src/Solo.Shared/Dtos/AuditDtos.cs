using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record AuditEntryDto(
    long Id,
    DateTime Timestamp,
    Guid? UserId,
    string? UserDisplayName,
    string TableName,
    string RowId,
    string? ColumnName,
    string? PreviousValue)
{
    /// <summary>Display labels contributed by loaded extensions, keyed by extension id.</summary>
    public IReadOnlyDictionary<string, string> Annotations { get; init; } = new Dictionary<string, string>();
}

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);
