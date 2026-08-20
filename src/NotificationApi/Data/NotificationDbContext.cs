using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using NotificationPlatform.Contracts;
using NotificationPlatform.NotificationApi.Domain;

namespace NotificationPlatform.NotificationApi.Data;

/// <summary>
/// The Notification API's own database. Holds the Notification table and,
/// via <c>AddEntityFrameworkOutbox</c>, the MassTransit outbox tables that
/// make publishing <see cref="NotificationRequested"/> atomic with the
/// Notification row that caused it (ADR-0002, <c>CONTEXT.md</c> "Outbox").
/// </summary>
public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var targetedChannelsComparer = new ValueComparer<IReadOnlyList<Channel>>(
            (left, right) => left!.SequenceEqual(right!),
            channels => channels.Aggregate(0, HashCode.Combine),
            channels => channels.ToList());

        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(notification => notification.Id);
            entity.Property(notification => notification.Recipient).IsRequired();
            entity.Property(notification => notification.Subject).IsRequired();
            entity.Property(notification => notification.Body).IsRequired();
            entity.Property(notification => notification.TargetedChannels)
                .HasConversion(
                    channels => string.Join(',', channels.Select(channel => channel.ToString())),
                    csv => (IReadOnlyList<Channel>)csv
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(Enum.Parse<Channel>)
                        .ToList())
                .Metadata.SetValueComparer(targetedChannelsComparer);
        });

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
