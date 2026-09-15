using System.ComponentModel.DataAnnotations;

namespace Solo.Shared.Dtos;

/// <summary>Identifies a stored image; the bytes are served from <c>GET /api/images/{id}</c>.</summary>
public sealed record ImageAssetDto(
    Guid Id,
    string ContentType);
