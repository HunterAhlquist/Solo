using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Solo.Shared;

namespace Solo.Api.Email;

public sealed record SmtpOptions(string Host, int Port, SmtpSecurity Security, string? Username, string? Password)
{
    public const string UnencryptedSignIn =
        "Signing in to an SMTP server needs an encrypted connection. Pick STARTTLS or SSL/TLS, " +
        "or leave the username empty if the server asks for no sign-in.";

    /// <summary>
    /// Whether these would sign in over a connection that isn't encrypted,
    /// which sends the username and password where anything between here and
    /// the server can read them. A server on this machine is the exception:
    /// there is nothing in between.
    /// </summary>
    public bool SignsInUnencrypted => SignsInUnencryptedTo(Host, Security, Username);

    public static bool SignsInUnencryptedTo(string? host, SmtpSecurity security, string? username) =>
        security == SmtpSecurity.None && !string.IsNullOrWhiteSpace(username) && !IsThisMachine(host);

    private static bool IsThisMachine(string? host) =>
        host is not null &&
        (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
         (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)));
}

/// <summary>The one call that leaves the building, so tests can stand in for the SMTP server.</summary>
public interface IEmailTransport
{
    Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct);
}

public sealed class MailKitTransport : IEmailTransport
{
    public async Task SendAsync(SmtpOptions options, MimeMessage message, CancellationToken ct)
    {
        // Settings are stopped short of this (EmailSettingsController); said
        // here as well, for any saved before they were.
        if (options.SignsInUnencrypted)
        {
            throw new InvalidOperationException(SmtpOptions.UnencryptedSignIn);
        }

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
