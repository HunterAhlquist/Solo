using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

public sealed record ProjectDto(
    Guid Id,
    string Name,
    string Alias,
    string? Icon,
    Guid? ImageId,
    uint Version,
    IReadOnlyList<ProjectMemberDto> Members);

public sealed record ProjectSummaryDto(
    Guid Id,
    string Name,
    string Alias,
    string? Icon,
    Guid? ImageId,
    ProjectRole Role,
    bool IsOwner);

public sealed record ProjectMemberDto(
    Guid Id,
    Guid? UserId,
    string Email,
    string? DisplayName,
    string? AvatarUrl,
    ProjectRole Role,
    bool IsOwner,
    bool IsPending);

public sealed class CreateProjectRequest
{
    [Required, MaxLength(200)]
    public required string Name { get; init; }

    [Required, RegularExpression(Aliases.Pattern, ErrorMessage = Aliases.PatternError)]
    public required string Alias { get; init; }

    [MaxLength(100)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }
}

public sealed class UpdateProjectRequest
{
    [Required, MaxLength(200)]
    public required string Name { get; init; }

    [Required, RegularExpression(Aliases.Pattern, ErrorMessage = Aliases.PatternError)]
    public required string Alias { get; init; }

    [MaxLength(100)]
    public string? Icon { get; init; }

    public Guid? ImageId { get; init; }

    public uint Version { get; init; }
}

public sealed class InviteMemberRequest
{
    [Required, EmailAddress, MaxLength(320)]
    public required string Email { get; init; }

    public ProjectRole Role { get; init; } = ProjectRole.Read;
}

public sealed class UpdateMemberRequest
{
    public ProjectRole Role { get; init; }

    public bool IsOwner { get; init; }
}
