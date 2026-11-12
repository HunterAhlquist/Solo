using Solo.Shared.Dtos;

namespace Solo.Client.Api;

public sealed partial class ApiClient
{
    public Task<NotificationInboxDto> GetNotificationsAsync(CancellationToken ct = default) =>
        GetAsync<NotificationInboxDto>("api/notifications", ct);

    public Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"api/notifications/{notificationId}/read", null, ct);

    public Task MarkAllNotificationsReadAsync(CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "api/notifications/read", null, ct);

    public Task<NotificationPreferencesDto> GetNotificationPreferencesAsync(CancellationToken ct = default) =>
        GetAsync<NotificationPreferencesDto>("api/notifications/preferences", ct);

    public Task<NotificationPreferencesDto> UpdateNotificationPreferencesAsync(
        UpdateNotificationPreferencesRequest request, CancellationToken ct = default) =>
        SendAsync<NotificationPreferencesDto>(HttpMethod.Put, "api/notifications/preferences", request, ct);

    public Task<IReadOnlyList<string>> GetTimeZonesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<string>>("api/notifications/time-zones", ct);

    public Task<EmailSettingsDto> GetEmailSettingsAsync(CancellationToken ct = default) =>
        GetAsync<EmailSettingsDto>("api/settings/email", ct);

    public Task<EmailSettingsDto> UpdateEmailSettingsAsync(UpdateEmailSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<EmailSettingsDto>(HttpMethod.Put, "api/settings/email", request, ct);

    public Task<TestEmailResultDto> SendTestEmailAsync(UpdateEmailSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<TestEmailResultDto>(HttpMethod.Post, "api/settings/email/test", request, ct);
}
