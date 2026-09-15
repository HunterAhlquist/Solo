using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>Entities carrying the xmin optimistic-concurrency token.</summary>
public interface IVersioned
{
    uint Version { get; set; }
}
