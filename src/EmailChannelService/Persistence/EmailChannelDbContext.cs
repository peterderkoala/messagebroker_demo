using MassTransit;
using Microsoft.EntityFrameworkCore;
using NotificationPlatform.EmailChannelService.Delivery;

namespace NotificationPlatform.EmailChannelService.Persistence;

/// <summary>
/// This service's own logical database (ADR-0002: database-per-service on a
/// shared Postgres container). Holds Delivery Attempts plus MassTransit's
/// EF Core outbox tables. This service only uses the consumer inbox side of
/// that pattern for idempotent-consumer dedup (it never calls
/// UseBusOutbox(), since it never publishes) - but UseEntityFrameworkOutbox()
/// on the receive endpoint still requires OutboxState/OutboxMessage in the
/// model, not just InboxState, or the receive pipeline throws on every
/// message. See docs/research/masstransit-outbox-retry-dlq.md's example
/// DbContext, which adds all three regardless of the publish/inbox split.
/// </summary>
public sealed class EmailChannelDbContext(DbContextOptions<EmailChannelDbContext> options)
    : DbContext(options)
{
    public DbSet<DeliveryAttempt> DeliveryAttempts => Set<DeliveryAttempt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // MassTransit's consumer inbox: dedups a redelivered NotificationRequested
        // by its MessageId before the consumer runs again.
        modelBuilder.AddInboxStateEntity();

        // Required by UseEntityFrameworkOutbox() on the receive endpoint even
        // though this service never publishes and never calls UseBusOutbox().
        modelBuilder.AddOutboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();

        modelBuilder.Entity<DeliveryAttempt>(entity =>
        {
            entity.ToTable("delivery_attempts");
            entity.HasKey(attempt => attempt.Id);
            entity.Property(attempt => attempt.Recipient).IsRequired().HasMaxLength(320);
            entity.Property(attempt => attempt.Outcome).HasConversion<string>().HasMaxLength(32);
            entity.HasIndex(attempt => attempt.NotificationId);
        });
    }
}
