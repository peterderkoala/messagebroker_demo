namespace NotificationPlatform.Contracts;

/// <summary>
/// One delivery medium for a Notification. Each Channel is implemented by
/// exactly one Channel Service.
/// </summary>
public enum Channel
{
    Email = 1,
    Sms = 2,
    Push = 3,
}

/// <summary>
/// The routing vocabulary shared by the Notification API (which publishes with
/// these keys) and the Channel Services (which bind their queues to them).
/// Both sides must agree, so the convention lives in Contracts rather than
/// being spelled out separately at each end.
/// </summary>
public static class ChannelRouting
{
    /// <summary>
    /// The topic exchange every <see cref="NotificationRequested"/> is published to.
    /// </summary>
    public const string ExchangeName = "notification";

    /// <summary>
    /// The routing key for one Channel, e.g. <c>notification.email</c>. A
    /// Notification targeting several Channels produces one message per
    /// Channel, each with its own key (ADR-0003).
    /// </summary>
    public static string RoutingKeyFor(Channel channel) =>
        $"{ExchangeName}.{channel.ToString().ToLowerInvariant()}";

    /// <summary>
    /// Every Channel the platform knows about. The Notification API validates
    /// requested Channels against this set, and rejects anything outside it.
    /// </summary>
    public static IReadOnlyCollection<Channel> All { get; } = Enum.GetValues<Channel>();
}
