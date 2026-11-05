namespace Solo.Client.Auth;

/// <summary>
/// One shared signal: the session we believed in is gone — the API rejected the
/// credential we sent. The layout listens and offers an
/// in-place re-sign-in, so in-progress work isn't lost to a redirect.
/// </summary>
public sealed class SessionExpiry
{
    public event Action? Expired;

    public void NotifyExpired() => Expired?.Invoke();
}
