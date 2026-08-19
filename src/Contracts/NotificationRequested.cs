namespace NotificationPlatform.Contracts;

/// <summary>
/// A Notification has been accepted and should be delivered over one Channel.
/// </summary>
/// <remarks>
/// Scoped to a single Targeted Channel, not to a Notification: a Notification
/// targeting Email and SMS produces two of these, each routed to one Channel
/// Service by its own routing key (ADR-0003).
/// </remarks>
/// <param name="NotificationId">
/// The Notification this event realizes. Shared by every event fanned out from
/// the same request, and persisted on the resulting Delivery Attempt - this is
/// the only thread tying the fan-out back together, since each Channel Service
/// keeps its Delivery Attempts in its own database.
/// </param>
/// <param name="Channel">The Targeted Channel this event is bound for.</param>
/// <param name="Recipient">
/// Where to deliver, in whatever form the Channel uses: an email address, a
/// phone number, a device token. The Channel Service interprets it.
/// </param>
/// <param name="Subject">Short headline. Not every Channel renders it.</param>
/// <param name="Body">The message shown to the recipient.</param>
/// <param name="RequestedAt">When the Notification API accepted the request.</param>
public sealed record NotificationRequested(
    Guid NotificationId,
    Channel Channel,
    string Recipient,
    string Subject,
    string Body,
    DateTimeOffset RequestedAt);
