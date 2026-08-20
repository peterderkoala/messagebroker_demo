using NotificationPlatform.Contracts;

namespace NotificationPlatform.NotificationApi.Domain;

/// <summary>
/// The read endpoint's response body. Also what gets cached as this
/// Notification's Cache Entry (#16) - the same shape serves both, so there
/// is only one JSON contract to keep in sync.
/// </summary>
public sealed record NotificationResponse(
    Guid Id,
    string Recipient,
    string Subject,
    string Body,
    IReadOnlyList<Channel> Channels,
    DateTimeOffset RequestedAt)
{
    public static NotificationResponse From(Notification notification) => new(
        notification.Id,
        notification.Recipient,
        notification.Subject,
        notification.Body,
        notification.TargetedChannels,
        notification.RequestedAt);
}
