using MassTransit;
using Microsoft.EntityFrameworkCore;
using NotificationPlatform.BuildingBlocks.HealthChecks;
using NotificationPlatform.BuildingBlocks.Observability;
using NotificationPlatform.BuildingBlocks.Persistence;
using NotificationPlatform.Contracts;
using NotificationPlatform.SmsChannelService.Consumers;
using NotificationPlatform.SmsChannelService.Persistence;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatformObservability("sms-channel-service");

var postgresUser = builder.Configuration["POSTGRES_USER"]
    ?? throw new InvalidOperationException("POSTGRES_USER is not configured.");
var postgresPassword = builder.Configuration["POSTGRES_PASSWORD"]
    ?? throw new InvalidOperationException("POSTGRES_PASSWORD is not configured.");

// Internal container port (5432), not the host-published POSTGRES_PORT - this
// connection never leaves the compose network. "sms_channel" is this
// service's own logical database on the shared Postgres container (ADR-0002).
var connectionString =
    $"Host=postgres;Port=5432;Database=sms_channel;Username={postgresUser};Password={postgresPassword}";

builder.Services.AddDbContext<SmsChannelDbContext>(options => options.UseNpgsql(connectionString));

var rabbitUser = builder.Configuration["RABBITMQ_USER"]
    ?? throw new InvalidOperationException("RABBITMQ_USER is not configured.");
var rabbitPassword = builder.Configuration["RABBITMQ_PASSWORD"]
    ?? throw new InvalidOperationException("RABBITMQ_PASSWORD is not configured.");

builder.Services.AddMassTransit(x =>
{
    // Inbox only (no UseBusOutbox()): this service never publishes, it only
    // needs the consumer inbox for idempotent processing of NotificationRequested.
    x.AddEntityFrameworkOutbox<SmsChannelDbContext>(o => o.UsePostgres());

    x.AddConsumer<NotificationRequestedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq", "/", h =>
        {
            h.Username(rabbitUser);
            h.Password(rabbitPassword);
        });

        // Fast, bounded in-memory retry; exhausted retries fault to the
        // automatic sms-notification-requested_error queue (issue #2 findings).
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(5)));

        cfg.ReceiveEndpoint("sms-notification-requested", e =>
        {
            // Stops MassTransit from also auto-binding this queue to the
            // default per-type exchange - without this, every Channel
            // Service would receive every NotificationRequested regardless
            // of Channel, exactly what ADR-0003 rejects.
            e.ConfigureConsumeTopology = false;

            e.UseEntityFrameworkOutbox<SmsChannelDbContext>(context);
            e.ConfigureConsumer<NotificationRequestedConsumer>(context);

            e.Bind(ChannelRouting.ExchangeName, x =>
            {
                x.ExchangeType = ExchangeType.Topic;
                x.RoutingKey = ChannelRouting.RoutingKeyFor(Channel.Sms);
            });
        });
    });
});

builder.Services.AddHealthChecks().AddDbContextCheck<SmsChannelDbContext>("postgres");

var app = builder.Build();

app.MapPlatformHealthChecks();

await app.MigrateDatabaseAsync<SmsChannelDbContext>();

app.Run();
