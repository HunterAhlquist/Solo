using Solo.Extensions;

namespace Solo.Api.Auth;

/// <summary>
/// Populates <see cref="CurrentUserAccessor"/> (and thereby the audit
/// interceptor) after authentication. Runs provisioning on the first request
/// of a new SSO identity.
/// </summary>
public sealed class CurrentUserMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentUserAccessor accessor,
        UserProvisioningService provisioning)
    {
        if (context.User.Identity?.IsAuthenticated == true &&
            context.User.Identity.AuthenticationType != AuthConstants.SetupScheme)
        {
            accessor.User = await provisioning.ResolveAsync(context.User, context.RequestAborted);
            accessor.IsBackground = context.User.Identity.AuthenticationType == BackgroundTokens.Scheme;
        }

        await next(context);
    }
}
