using NotificationPlatform.Contracts;

namespace NotificationPlatform.NotificationApi.Domain;

/// <summary>
/// The persisted record of an accepted Notification. Carries no delivery
/// outcome of its own - status lives only on each Channel Service's Delivery
/// Attempt, keyed back to this row's <see cref="Id"/> (<c>CONTEXT.md</c>).
/// </summary>
public sealed class Notification
{
    public required Guid Id { get; init; }

    public required string Recipient { get; init; }

    public required string Subject { get; init; }

    public required string Body { get; init; }

    /// <summary>
    /// The Channels this Notification targets. At least one, validated
    /// against <see cref="ChannelRouting.All"/> before this row is ever
    /// created (ADR-0003).
    /// </summary>
    public required IReadOnlyList<Channel> TargetedChannels { get; init; }

    public required DateTimeOffset RequestedAt { get; init; }
}
