using Microsoft.EntityFrameworkCore;
using MimeKit;
using Solo.Api.Theming;
using Solo.Data;
using Solo.Data.Entities;
using Solo.Shared.Dtos;

namespace Solo.Api.Email;

/// <summary>
/// Where emails wait to be sent. Writers add a row with their own save and then
/// <see cref="Wake"/> the sender, so an invite goes out in seconds rather than
/// at the next poll.
/// </summary>
public sealed class EmailOutbox(ThemeCatalog themes)
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public static void Queue(SoloDbContext db, string to, EmailContent content, string kind, string? unsubscribeUrl = null) =>
        db.EmailMessages.Add(new EmailMessage
        {
            To = to,
            Subject = content.Subject,
            HtmlBody = content.Html,
            TextBody = content.Text,
            Kind = kind,
            UnsubscribeUrl = unsubscribeUrl,
        });

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
        {
            try
            {
                _wake.Release();
            }
            catch (SemaphoreFullException)
            {
                // Woken twice at once: one wake is all the sender needs.
            }
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct) => await _wake.WaitAsync(timeout, ct);

    /// <summary>Settings complete enough to send with. Anything less and email stays off, whatever the switch says.</summary>
    public static bool IsReady(EmailSettings? settings) =>
        settings is { Enabled: true, PublicAddress: not null, SmtpHost: not null, FromAddress: not null };

    public static async Task<EmailSettings?> ReadySettingsAsync(SoloDbContext db, CancellationToken ct)
    {
        var settings = await db.EmailSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        return IsReady(settings) ? settings : null;
    }

    /// <summary>The instance's name and active theme, which every email is dressed in.</summary>
    public async Task<EmailBrand> BrandAsync(SoloDbContext db, CancellationToken ct)
    {
        var instance = await db.InstanceSettings.AsNoTracking().Select(s => new { s.DisplayName, s.ThemeId }).FirstOrDefaultAsync(ct);
        return new EmailBrand(instance?.DisplayName ?? InstanceBranding.ProductName, themes.Resolve(instance?.ThemeId).Manifest);
    }

    /// <summary>The public address with one trailing slash, ready to have app paths appended.</summary>
    public static string BaseAddress(EmailSettings settings) => settings.PublicAddress!.TrimEnd('/') + "/";

    public static SmtpOptions OptionsFor(EmailSettings settings, string? password) =>
        new(settings.SmtpHost!, settings.SmtpPort, settings.Security, settings.Username, password);

    public static MimeMessage Compose(EmailSettings settings, string to, string subject, string html, string text, string? unsubscribeUrl)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName ?? "", settings.FromAddress!));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;
        if (unsubscribeUrl is not null)
        {
            // RFC 8058 one-click: mail apps POST to it without opening a page.
            message.Headers.Add("List-Unsubscribe", $"<{unsubscribeUrl}>");
            message.Headers.Add("List-Unsubscribe-Post", "List-Unsubscribe=One-Click");
        }

        var body = new BodyBuilder { TextBody = text, HtmlBody = html };
        message.Body = body.ToMessageBody();
        return message;
    }
}
