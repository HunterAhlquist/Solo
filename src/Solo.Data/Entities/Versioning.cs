using Solo.Shared;

namespace Solo.Data.Entities;

/// <summary>Entities carrying the xmin optimistic-concurrency token.</summary>
public interface IVersioned
{
    uint Version { get; set; }
}

/// <summary>
/// Kept out of the audit log: a person's own inbox and settings, whose churn
/// (every reminder raised, every entry read) is nobody's history.
/// </summary>
public interface INotAudited;
