using System.Data.Common;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;

namespace NotificationPlatform.BuildingBlocks.Persistence;

/// <summary>
/// Startup provisioning, shared so all four services do it identically.
/// </summary>
/// <remarks>
/// ADR-0002: there is no init SQL. Each service creates and migrates its own
/// database at startup, which is why this needs a retry - compose can report
/// Postgres healthy a moment before it will accept this connection.
/// </remarks>
public static class DatabaseMigrationExtensions
{
    /// <summary>
    /// Applies this service's migrations, creating the database if it does not
    /// exist, retrying while the Postgres server is still coming up.
    /// </summary>
    /// <remarks>
    /// Only failures to <em>reach the server</em> are retried. A migration that
    /// fails on its own SQL throws a <see cref="DbException"/> the server
    /// answered with, which is not retryable and fails immediately - retrying
    /// it would turn a clear error into a long silence that looks like a hang.
    /// <para>
    /// Note this waits on the server, not on the database: on a first run the
    /// database is absent, and creating it is <c>Migrate</c>'s job. That is
    /// also why this is <c>Migrate</c> and never <c>EnsureCreated</c> - the
    /// MassTransit outbox ships its own migrations, and <c>EnsureCreated</c>
    /// skips the migrations history table they depend on.
    /// </para>
    /// </remarks>
    public static async Task MigrateDatabaseAsync<TContext>(
        this IHost host,
        CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(host);

        await using var scope = host.Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TContext>();
        var logger = scope.ServiceProvider
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseMigrationExtensions));

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsServerUnreachable),
                MaxRetryAttempts = 12,
                Delay = TimeSpan.FromSeconds(2),
                BackoffType = DelayBackoffType.Constant,
                OnRetry = args =>
                {
                    logger.LogWarning(
                        "Postgres not reachable yet for {Context} (attempt {Attempt}), retrying in {Delay}.",
                        typeof(TContext).Name,
                        args.AttemptNumber + 1,
                        args.RetryDelay);
                    return ValueTask.CompletedTask;
                },
            })
            .Build();

        logger.LogInformation("Applying migrations for {Context}.", typeof(TContext).Name);

        await pipeline.ExecuteAsync(
            async token => await context.Database.MigrateAsync(token),
            cancellationToken);

        logger.LogInformation("Migrations for {Context} are up to date.", typeof(TContext).Name);
    }

    /// <summary>
    /// True when the exception chain says we never got an answer from the
    /// server, as opposed to the server answering with an error.
    /// </summary>
    private static bool IsServerUnreachable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // No TCP connection at all: Postgres has not started listening yet.
            if (current is SocketException)
            {
                return true;
            }

            // The provider answered, but flagged the failure as transient
            // (connection reset mid-startup, too many connections while the
            // other three services migrate at the same moment).
            if (current is DbException { IsTransient: true })
            {
                return true;
            }
        }

        return false;
    }
}
