using MassTransit;
using NotificationPlatform.Contracts;
using NotificationPlatform.NotificationApi.Data;

namespace NotificationPlatform.NotificationApi.Domain;

/// <summary>The accept endpoint (ADR-0003, <c>CONTEXT.md</c> "Notification API").</summary>
public static class NotificationsEndpoint
{
    public static IEndpointRouteBuilder MapNotificationsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/notifications", CreateNotification).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> CreateNotification(
        CreateNotificationRequest request,
        NotificationDbContext db,
        IPublishEndpoint publishEndpoint,
        CancellationToken cancellationToken)
    {
        // A Notification that can produce no Delivery Attempt is a caller
        // error and must never reach the outbox (ADR-0003).
        if (request.Channels.Count == 0 || request.Channels.Any(channel => !ChannelRouting.All.Contains(channel)))
        {
            return Results.Problem(
                title: "At least one valid Targeted Channel is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            Recipient = request.Recipient,
            Subject = request.Subject,
            Body = request.Body,
            TargetedChannels = request.Channels,
            RequestedAt = DateTimeOffset.UtcNow,
        };

        db.Notifications.Add(notification);

        // One NotificationRequested per Targeted Channel, not per Notification
        // (ADR-0003) - each routed by its own key, each sharing the
        // Notification's id as the correlation key tying the fan-out back
        // together (CONTEXT.md "Notification API state and correlation").
        foreach (var channel in request.Channels)
        {
            await publishEndpoint.Publish(
                new NotificationRequested(
                    notification.Id,
                    channel,
                    request.Recipient,
                    request.Subject,
                    request.Body,
                    notification.RequestedAt),
                cancellationToken);
        }

        // Single SaveChangesAsync commits the Notification row and the
        // outbox rows staged by the Publish calls above in one transaction -
        // this is the point of the outbox here (CONTEXT.md "Outbox").
        await db.SaveChangesAsync(cancellationToken);

        return Results.Created($"/notifications/{notification.Id}", new { notification.Id });
    }
}
