using MassTransit;
using Microsoft.EntityFrameworkCore;
using NotificationPlatform.EmailChannelService.Delivery;

namespace NotificationPlatform.EmailChannelService.Persistence;

/// <summary>
/// This service's own logical database (ADR-0002: database-per-service on a
/// shared Postgres container). Holds Delivery Attempts plus MassTransit's
/// consumer inbox tables, used only for idempotent-consumer dedup - this
/// service never publishes, so no outbox tables are needed.
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
