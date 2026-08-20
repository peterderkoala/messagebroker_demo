using NotificationPlatform.Contracts;

namespace NotificationPlatform.NotificationApi.Domain;

/// <summary>The accept endpoint's request body.</summary>
public sealed record CreateNotificationRequest(
    string Recipient,
    string Subject,
    string Body,
    IReadOnlyList<Channel> Channels);
