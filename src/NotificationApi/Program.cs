using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationPlatform.BuildingBlocks.Caching;
using NotificationPlatform.BuildingBlocks.HealthChecks;
using NotificationPlatform.BuildingBlocks.Observability;
using NotificationPlatform.BuildingBlocks.Persistence;
using NotificationPlatform.Contracts;
using NotificationPlatform.NotificationApi.Auth;
using NotificationPlatform.NotificationApi.Data;
using NotificationPlatform.NotificationApi.Domain;
using RabbitMQ.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddPlatformObservability("notification-api");
builder.AddPlatformJwtAuthentication();
builder.AddPlatformCache("notification-api");

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// One shared Postgres superuser (ADR-0002); this service's own logical
// database, reached over the compose network - never the host-published
// POSTGRES_PORT, which is for host access only.
var postgresConnectionString = new NpgsqlConnectionStringBuilder
{
    Host = builder.Configuration["POSTGRES_HOST"] ?? "postgres",
    Port = 5432,
    Username = builder.Configuration["POSTGRES_USER"]
        ?? throw new InvalidOperationException("Missing required configuration value 'POSTGRES_USER'."),
    Password = builder.Configuration["POSTGRES_PASSWORD"]
        ?? throw new InvalidOperationException("Missing required configuration value 'POSTGRES_PASSWORD'."),
    Database = "notification_api",
}.ConnectionString;

builder.Services.AddDbContext<NotificationDbContext>(options => options.UseNpgsql(postgresConnectionString));

var rabbitMqHost = builder.Configuration["RABBITMQ_HOST"] ?? "rabbitmq";
var rabbitMqUser = builder.Configuration["RABBITMQ_USER"]
    ?? throw new InvalidOperationException("Missing required configuration value 'RABBITMQ_USER'.");
var rabbitMqPassword = builder.Configuration["RABBITMQ_PASSWORD"]
    ?? throw new InvalidOperationException("Missing required configuration value 'RABBITMQ_PASSWORD'.");

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<NotificationDbContext>(o =>
    {
        o.UsePostgres();
        o.UseBusOutbox();
    });

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(rabbitMqHost, "/", h =>
        {
            h.Username(rabbitMqUser);
            h.Password(rabbitMqPassword);
        });

        // Publish every NotificationRequested to the shared "notification"
        // topic exchange (not MassTransit's default per-type exchange), with
        // a per-Channel routing key - ADR-0003. The EF outbox preserves this
        // routing key end to end (docs/research/masstransit-topic-topology.md).
        cfg.Message<NotificationRequested>(m => m.SetEntityName(ChannelRouting.ExchangeName));
        cfg.Publish<NotificationRequested>(p => p.ExchangeType = ExchangeType.Topic);
        cfg.Send<NotificationRequested>(s => s.UseRoutingKeyFormatter(
            sendContext => ChannelRouting.RoutingKeyFor(sendContext.Message.Channel)));

        cfg.ConfigureEndpoints(context);
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<NotificationDbContext>("postgres")
    .AddPlatformCacheHealthCheck();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapPlatformHealthChecks();
app.MapTokenEndpoint();
app.MapNotificationsEndpoint();

await app.MigrateDatabaseAsync<NotificationDbContext>();

app.Run();
