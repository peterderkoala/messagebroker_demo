// The Comparison Stream (CONTEXT.md): a standalone Redis Streams producer +
// consumer group, built solely to contrast the mechanics with the
// RabbitMQ/MassTransit path used everywhere else in this platform. It shares
// no code, no data, and no lifecycle with the Notification flow - findings
// live in docs/research/redis-streams-consumer-groups.md (issue #5).
using StackExchange.Redis;

const string StreamKey = "comparison:events";
const string GroupName = "comparison-consumers";

var redisHost = Environment.GetEnvironmentVariable("REDIS_HOST") ?? "redis";
var redisPort = Environment.GetEnvironmentVariable("REDIS_PORT") ?? "6379";

var redis = await ConnectionMultiplexer.ConnectAsync($"{redisHost}:{redisPort}");
IDatabase db = redis.GetDatabase();

await EnsureConsumerGroupAsync(db);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

Log("startup", $"stream={StreamKey} group={GroupName} redis={redisHost}:{redisPort}");

await Task.WhenAll(
    RunProducerAsync(db, cts.Token),
    RunConsumerAsync(db, "consumer-1", failEveryNth: 0, cts.Token),
    RunConsumerAsync(db, "consumer-2", failEveryNth: 4, cts.Token),
    RunSweeperAsync(db, cts.Token));

return;

async Task EnsureConsumerGroupAsync(IDatabase database)
{
    try
    {
        // XGROUP CREATE ... MKSTREAM: creates the stream too if this is a
        // fresh Redis instance, replaying the whole log from the start so a
        // restarted demo doesn't depend on the producer having run first.
        await database.StreamCreateConsumerGroupAsync(
            StreamKey, GroupName, StreamPosition.Beginning, createStream: true);
    }
    catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP", StringComparison.Ordinal))
    {
        // Group already exists from a previous run. StreamCreateConsumerGroup
        // isn't idempotent on its own - this is the documented way to make it so.
    }
}

async Task RunProducerAsync(IDatabase database, CancellationToken cancellationToken)
{
    var sequence = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        sequence++;

        // XADD - append-only, no routing key or exchange to configure; every
        // consumer group attached to this stream sees every entry.
        var messageId = await database.StreamAddAsync(
            StreamKey,
            [
                new NameValueEntry("sequence", sequence),
                new NameValueEntry("createdAtUtc", DateTimeOffset.UtcNow.ToString("O")),
            ]);

        Log("producer", $"XADD {messageId} sequence={sequence}");

        await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
    }
}

async Task RunConsumerAsync(
    IDatabase database, string consumerName, int failEveryNth, CancellationToken cancellationToken)
{
    var received = 0;

    while (!cancellationToken.IsCancellationRequested)
    {
        // ">" = only entries never yet delivered to any consumer in this
        // group (XREADGROUP ... STREAMS key >) - the "give me new work" read.
        // StackExchange.Redis has no BLOCK-based async read, so this polls.
        var entries = await database.StreamReadGroupAsync(
            StreamKey, GroupName, consumerName, position: ">", count: 5);

        if (entries.Length == 0)
        {
            await DelayAsync(TimeSpan.FromSeconds(1), cancellationToken);
            continue;
        }

        foreach (var entry in entries)
        {
            received++;

            // Deliberately drop every Nth message to demonstrate what Redis
            // Streams does NOT give you for free: no broker-side retry or
            // DLQ. An un-acked entry just sits in the group's PEL until the
            // sweeper below reclaims it - contrast with MassTransit's
            // automatic UseMessageRetry + dead-letter queue on RabbitMQ.
            if (failEveryNth > 0 && received % failEveryNth == 0)
            {
                Log(consumerName, $"simulated failure on {entry.Id} sequence={entry["sequence"]} - leaving unacked");
                continue;
            }

            Log(consumerName, $"processed {entry.Id} sequence={entry["sequence"]}");

            // XACK - only clears the group's pending marker, it never
            // deletes the stream entry itself (other groups may still want it).
            await database.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
        }
    }
}

async Task RunSweeperAsync(IDatabase database, CancellationToken cancellationToken)
{
    while (!cancellationToken.IsCancellationRequested)
    {
        await DelayAsync(TimeSpan.FromSeconds(10), cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            break;
        }

        RedisValue cursor = "0-0";
        do
        {
            // XAUTOCLAIM - reassigns entries idle in the PEL past
            // minIdleTimeInMs to "sweeper" and hands them back for
            // reprocessing: the manual equivalent of the redelivery/DLX
            // sweep MassTransit performs automatically on the RabbitMQ side.
            var result = await database.StreamAutoClaimAsync(
                StreamKey, GroupName, "sweeper", minIdleTimeInMs: 5000, startAtId: cursor);

            foreach (var entry in result.ClaimedEntries)
            {
                Log("sweeper", $"reclaimed {entry.Id} sequence={entry["sequence"]} - reprocessing");
                await database.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
            }

            cursor = result.NextStartId;
        }
        while (cursor != "0-0");
    }
}

async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
{
    try
    {
        await Task.Delay(delay, cancellationToken);
    }
    catch (OperationCanceledException)
    {
        // Expected on shutdown - the caller's loop condition ends things.
    }
}

void Log(string role, string message) =>
    Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.fff} [{role,-10}] {message}");
