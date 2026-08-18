using System.Collections.Concurrent;
using Solo.Extensibility;

namespace Solo.Extension.Fixture;

/// <summary>Records what the extension was told about, so a test can assert delivery.</summary>
public sealed class FixtureEventLog
{
    private readonly ConcurrentQueue<SoloDomainEvent> _events = new();

    public void Add(SoloDomainEvent domainEvent) => _events.Enqueue(domainEvent);

    public IReadOnlyList<SoloDomainEvent> Snapshot() => _events.ToList();
}

public sealed class FixtureEventHandler(FixtureEventLog log) : ISoloEventHandler
{
    public ValueTask HandleAsync(SoloDomainEvent domainEvent, CancellationToken ct)
    {
        log.Add(domainEvent);

        // A handler that throws must not be able to fail the core write; the
        // publisher swallows it. FixtureEventThrows asserts that.
        if (domainEvent.ChangedFields.Contains("__fixture_throw"))
        {
            throw new InvalidOperationException("Fixture event handler failed on purpose.");
        }

        return ValueTask.CompletedTask;
    }
}
