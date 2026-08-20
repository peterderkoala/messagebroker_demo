namespace NotificationPlatform.PushChannelService.Delivery;

/// <summary>
/// The outcome of one Delivery Attempt.
/// </summary>
public enum DeliveryOutcome
{
    Delivered = 1,
    Failed = 2,
}

/// <summary>
/// This service's persisted record of trying to deliver a Notification over
/// Push. See CONTEXT.md's "Delivery Attempt" entry - the <see cref="NotificationId"/>
/// is the only thread tying a fan-out together across the platform's four
/// separate databases, so it is a real column here, not just something logged.
/// </summary>
public sealed class DeliveryAttempt
{
    public Guid Id { get; init; }

    /// <summary>
    /// The Notification this attempt realizes. Shared by every Event fanned
    /// out from the same request (ADR-0003).
    /// </summary>
    public Guid NotificationId { get; init; }

    /// <summary>Where delivery was attempted - a device token for this Channel.</summary>
    public required string Recipient { get; init; }

    public DeliveryOutcome Outcome { get; init; }

    public DateTimeOffset AttemptedAt { get; init; }

    /// <summary>
    /// Derived from the consumed event's MassTransit MessageId (CONTEXT.md's
    /// "Idempotency Key"). Persisted here for visibility even though the EF
    /// Core consumer inbox is what actually prevents a redelivery from
    /// producing a second row.
    /// </summary>
    public Guid IdempotencyKey { get; init; }
}
