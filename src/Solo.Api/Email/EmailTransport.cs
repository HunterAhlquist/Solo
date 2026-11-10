using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Solo.Shared;

namespace Solo.Api.Email;

public sealed record SmtpOptions(string Host, int Port, SmtpSecurity Security, string? Username, string? Password);

/// <summary>The one call that leaves the building, so tests can stand in for the SMTP server.</summary>
public interface IEmailTransport
{
    Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct);
}

public sealed class MailKitTransport : IEmailTransport
{
    public async Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        using var client = new SmtpClient { Timeout = (int)TimeSpan.FromSeconds(30).TotalMilliseconds };
        await client.ConnectAsync(options.Host, options.Port, options.Security switch
        {
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        }, ct);
        if (!string.IsNullOrEmpty(options.Username))
        {
            await client.AuthenticateAsync(options.Username, options.Password ?? "", ct);
        }

        await client.SendAsync(message, ct);
        await client.DisconnectAsync(quit: true, ct);
    }
}
