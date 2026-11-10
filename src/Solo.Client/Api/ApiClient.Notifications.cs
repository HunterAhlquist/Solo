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
}
