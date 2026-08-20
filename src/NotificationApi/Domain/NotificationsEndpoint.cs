using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using NotificationPlatform.BuildingBlocks.Caching;
using NotificationPlatform.Contracts;
using NotificationPlatform.NotificationApi.Data;

namespace NotificationPlatform.NotificationApi.Domain;

/// <summary>
/// The accept and read endpoints (ADR-0003, <c>CONTEXT.md</c>
/// "Notification API"). The read side is served cache-aside (#16, "Cache
/// Entry"): Redis first, Postgres on a miss.
/// </summary>
public static class NotificationsEndpoint
{
    /// <summary>
    /// How long a Cache Entry may serve a Notification before falling back
    /// to Postgres again. Short enough that a bug in the invalidation-on-write
    /// path below self-heals quickly rather than serving stale data for long.
    /// </summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapNotificationsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/notifications", CreateNotification).RequireAuthorization();
        endpoints.MapGet("/notifications/{id:guid}", GetNotification).RequireAuthorization();

        return endpoints;
    }

    private static async Task<IResult> CreateNotification(
        CreateNotificationRequest request,
        NotificationDbContext db,
        IPublishEndpoint publishEndpoint,
        IDistributedCache cache,
        ILogger<Notification> logger,
        CancellationToken cancellationToken)
    {
        // A Notification that can produce no Delivery Attempt is a caller
        // error and must never reach the outbox (ADR-0003).
        if (request.Channels is null || request.Channels.Count == 0
            || request.Channels.Any(channel => !ChannelRouting.All.Contains(channel)))
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

        // A fresh Guid can never already hold a Cache Entry, so this is a
        // no-op today - Notifications are never updated (CONTEXT.md). It's
        // still the correct integration point: every write path invalidates
        // before returning, so a Cache Entry can never go stale even if a
        // future change makes Notifications mutable. Best-effort (#17): a
        // Redis fault here must never turn an already-committed write into
        // a failed request.
        await cache.InvalidateAsync(logger, CacheKeyFor(notification.Id), cancellationToken);

        return Results.Created($"/notifications/{notification.Id}", new { notification.Id });
    }

    private static async Task<IResult> GetNotification(
        Guid id,
        NotificationDbContext db,
        IDistributedCache cache,
        ILogger<Notification> logger,
        CancellationToken cancellationToken)
    {
        var response = await cache.GetOrCreateAsync(
            logger,
            CacheKeyFor(id),
            CacheTtl,
            async ct =>
            {
                var notification = await db.Notifications
                    .AsNoTracking()
                    .FirstOrDefaultAsync(n => n.Id == id, ct);

                return notification is null ? null : NotificationResponse.From(notification);
            },
            cancellationToken);

        return response is null ? Results.NotFound() : Results.Ok(response);
    }

    private static string CacheKeyFor(Guid id) => $"notification:{id}";
}
