namespace Solo.Api.Updates;

/// <summary>
/// Whether this process is installing extension updates or about to restart
/// for them, which <c>api/ready</c> reports as not ready.
/// </summary>
/// <remarks>
/// Installing several updates can outlast a reverse proxy's read timeout. The
/// client then sees a gateway error rather than the answer, assumes a restart
/// is under way and polls <c>api/ready</c> — which must not say "ready" from
/// this process while it is still downloading, or the page reloads into the
/// old instance just before it goes down. An update that ends in a restart
/// never ends its activity: the process exits instead.
/// </remarks>
public sealed class UpdateActivity
{
    private int _active;

    public bool Active => Volatile.Read(ref _active) > 0;

    public void Begin() => Interlocked.Increment(ref _active);

    public void End() => Interlocked.Decrement(ref _active);
}
