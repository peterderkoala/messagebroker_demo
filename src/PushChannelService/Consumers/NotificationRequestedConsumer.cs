using MassTransit;
using NotificationPlatform.Contracts;
using NotificationPlatform.PushChannelService.Delivery;
using NotificationPlatform.PushChannelService.Persistence;

namespace NotificationPlatform.PushChannelService.Consumers;

/// <summary>
/// Simulates Push delivery for a <see cref="NotificationRequested"/> Event
/// and persists the result as a Delivery Attempt. Mirrors the Email Channel
/// Service's reference template (#9).
/// </summary>
/// <remarks>
/// Reached only for Events routed with the <c>notification.push</c> key -
/// the receive endpoint in Program.cs binds explicitly per ADR-0003, so this
/// consumer never has to filter by Channel itself.
/// <para>
/// Duplicate delivery of the same Event is handled by the EF Core consumer
/// inbox configured on the receive endpoint (keyed by MassTransit's own
/// MessageId): a redelivery never reaches this method a second time. The
/// Idempotency Key is still recorded on the Delivery Attempt for visibility.
/// </para>
/// </remarks>
public sealed class NotificationRequestedConsumer(
    PushChannelDbContext dbContext,
    ILogger<NotificationRequestedConsumer> logger) : IConsumer<NotificationRequested>
{
    public async Task Consume(ConsumeContext<NotificationRequested> context)
    {
        var message = context.Message;

        var idempotencyKey = context.MessageId
            ?? throw new InvalidOperationException(
                "NotificationRequested arrived without a MessageId; the platform derives the Idempotency Key from it.");

        // Simulated delivery: no real push provider, always succeeds. A
        // genuine unhandled exception here is what would exercise retry/DLQ.
        logger.LogInformation(
            "Simulated push delivery for Notification {NotificationId} to {Recipient}: {Subject}",
            message.NotificationId,
            message.Recipient,
            message.Subject);

        dbContext.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = NewId.NextGuid(),
            NotificationId = message.NotificationId,
            Recipient = message.Recipient,
            Outcome = DeliveryOutcome.Delivered,
            AttemptedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey,
        });

        await dbContext.SaveChangesAsync(context.CancellationToken);
    }
}
