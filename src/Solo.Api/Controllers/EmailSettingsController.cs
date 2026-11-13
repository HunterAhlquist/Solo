using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MimeKit;
using Solo.Api.Auth;
using Solo.Api.Email;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Controllers;

/// <summary>The instance's outgoing mail, for the super admin.</summary>
[Route("api/settings/email")]
[Authorize(Policy = AuthConstants.SuperAdminPolicy)]
public sealed class EmailSettingsController(
    SoloDbContext db, SmtpPassword password, IEmailTransport transport, EmailOutbox outbox, CurrentUserAccessor currentUser,
    ILogger<EmailSettingsController> logger) : ApiControllerBase
{
    private const int RecentFailureCount = 10;

    [HttpGet]
    public async Task<ActionResult<EmailSettingsDto>> Get(CancellationToken ct) =>
        await ToDtoAsync(await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new EmailSettings(), ct);

    [HttpPut]
    public async Task<ActionResult<EmailSettingsDto>> Update(UpdateEmailSettingsRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } invalid)
        {
            return invalid;
        }

        var settings = await db.EmailSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new EmailSettings();
            db.EmailSettings.Add(settings);
        }
        else
        {
            UseClientVersion(db, settings, request.Version);
        }

        var previousHost = settings.SmtpHost;
        Apply(settings, request);
        if (request.Password is { } newPassword)
        {
            settings.ProtectedPassword = newPassword.Length == 0 ? null : password.Protect(newPassword);
        }
        else if (settings.SmtpHost != previousHost)
        {
            // A stored password only ever goes to the server it was typed for,
            // so pointing the settings elsewhere can't be used to collect it.
            settings.ProtectedPassword = null;
        }

        return await SaveOrConflictAsync(db, ct) ?? (ActionResult<EmailSettingsDto>)await ToDtoAsync(settings, ct);
    }

    /// <summary>
    /// Sends a test email to the admin with the values in the form, saved or
    /// not, so a typo shows up before it is saved. Sent directly, not queued:
    /// the point is to see the server's answer.
    /// </summary>
    [HttpPost("test")]
    public async Task<ActionResult<TestEmailResultDto>> SendTest(UpdateEmailSettingsRequest request, CancellationToken ct)
    {
        if (Validate(request, requireComplete: true) is { } invalid)
        {
            return invalid;
        }

        var stored = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new EmailSettings();
        var trial = new EmailSettings { ProtectedPassword = stored.ProtectedPassword };
        Apply(trial, request);
        var to = currentUser.User!.Email;
        var content = EmailTemplates.Test(await outbox.BrandAsync(db, ct));
        try
        {
            // Same rule as saving: the stored password goes only to its own server.
            var smtpPassword = request.Password is { Length: > 0 } typed ? typed
                : trial.SmtpHost == stored.SmtpHost ? password.Resolve(trial)
                : null;
            await transport.SendAsync(EmailOutbox.OptionsFor(trial, smtpPassword),
                EmailOutbox.Compose(trial, to, content.Subject, content.Html, content.Text, null), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogInformation(e, "Test email to {To} failed.", to);
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: "Couldn't send", detail: e.Message);
        }

        return new TestEmailResultDto(to);
    }

    private ObjectResult? Validate(UpdateEmailSettingsRequest request, bool requireComplete = false)
    {
        var mustBeComplete = requireComplete || request.Enabled;
        string? problem = Enum.IsDefined(request.Security) ? null : "Pick how the connection is secured.";
        if (Clean(request.PublicAddress) is { } address)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                problem ??= "The public address must be a full web address, such as https://solo.example.com.";
            }
        }
        else if (mustBeComplete)
        {
            problem ??= "Enter the address people use to reach this instance.";
        }

        if (Clean(request.FromAddress) is { } from)
        {
            if (!MailboxAddress.TryParse(from, out _))
            {
                problem ??= "The sender address isn't a valid email address.";
            }
        }
        else if (mustBeComplete)
        {
            problem ??= "Enter the address emails are sent from.";
        }

        if (mustBeComplete && Clean(request.SmtpHost) is null)
        {
            problem ??= "Enter the SMTP server.";
        }

        return problem is null ? null : Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid email settings", detail: problem);
    }

    private static void Apply(EmailSettings settings, UpdateEmailSettingsRequest request)
    {
        settings.Enabled = request.Enabled;
        settings.PublicAddress = Clean(request.PublicAddress);
        settings.FromName = Clean(request.FromName);
        settings.FromAddress = Clean(request.FromAddress);
        settings.SmtpHost = Clean(request.SmtpHost);
        settings.SmtpPort = request.SmtpPort;
        settings.Security = request.Security;
        settings.Username = Clean(request.Username);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<EmailSettingsDto> ToDtoAsync(EmailSettings settings, CancellationToken ct)
    {
        var failures = await db.EmailMessages.AsNoTracking()
            .Where(m => m.FailedAt != null)
            .OrderByDescending(m => m.FailedAt)
            .Take(RecentFailureCount)
            .Select(m => new EmailFailureDto(m.To, m.Subject, m.LastError, m.FailedAt!.Value))
            .ToListAsync(ct);
        return new EmailSettingsDto(
            settings.Enabled, settings.PublicAddress, settings.FromName, settings.FromAddress,
            settings.SmtpHost, settings.SmtpPort, settings.Security, settings.Username,
            settings.ProtectedPassword is not null, password.FromEnvironment, failures, settings.Version);
    }
}
