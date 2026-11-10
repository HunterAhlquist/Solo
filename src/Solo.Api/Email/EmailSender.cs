using Microsoft.EntityFrameworkCore;
using Solo.Data;
using Solo.Shared.Dtos;

namespace Solo.Api.Email;

/// <summary>
/// Drains the outbox. A failed send is retried after roughly 1 minute, 5
/// minutes, 30 minutes and 2 hours, then marked failed for the admin's Email
/// section to show. While email is off, queued mail simply waits.
/// </summary>
public sealed class EmailSender(
    IServiceScopeFactory scopes,
    EmailOutbox outbox,
    IEmailTransport transport,
    SmtpPassword password,
    TimeProvider time,
    ILogger<EmailSender> logger) : BackgroundService
{
    public static readonly TimeSpan[] RetryDelays =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];

    private const int BatchSize = 20;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small startup delay so migrations finish first.
        await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueAsync(time.GetUtcNow(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Sending queued email failed; retrying next interval.");
            }

            try
            {
                await outbox.WaitAsync(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task SendDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SoloDbContext>();
        if (await EmailOutbox.ReadySettingsAsync(db, ct) is not { } settings)
        {
            return;
        }

        var options = EmailOutbox.OptionsFor(settings, password.Resolve(settings));
        while (true)
        {
            var due = await db.EmailMessages
                .Where(m => m.SentAt == null && m.FailedAt == null && m.NextAttemptAt <= now)
                .OrderBy(m => m.CreatedAt)
                .Take(BatchSize)
                .ToListAsync(ct);
            foreach (var message in due)
            {
                try
                {
                    await transport.SendAsync(options,
                        EmailOutbox.Compose(settings, message.To, message.Subject, message.HtmlBody, message.TextBody, message.UnsubscribeUrl), ct);
                    message.SentAt = now;
                }
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    message.Attempts++;
                    message.LastError = e.Message.Length <= NotificationLimits.BodyLength ? e.Message : e.Message[..NotificationLimits.BodyLength];
                    if (message.Attempts > RetryDelays.Length)
                    {
                        message.FailedAt = now;
                    }
                    else
                    {
                        message.NextAttemptAt = now + RetryDelays[message.Attempts - 1];
                    }

                    logger.LogWarning(e, "Couldn't send {Kind} email {Id} (attempt {Attempts}).", message.Kind, message.Id, message.Attempts);
                }

                await db.SaveChangesAsync(ct);
            }

            if (due.Count < BatchSize)
            {
                return;
            }
        }
    }
}
