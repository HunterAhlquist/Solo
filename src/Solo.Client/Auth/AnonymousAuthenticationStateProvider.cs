using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Solo.Client.Auth;

/// <summary>
/// Stands in for the OIDC provider on an instance that has none configured yet.
/// Registering the real provider with an empty authority would throw inside
/// oidc-client-ts on the very first authentication-state query and take the
/// whole app down — including the setup wizard that tells the operator to
/// configure it. Everyone is anonymous; only <c>/setup</c> is reachable.
/// </summary>
public sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Anonymous;
}
