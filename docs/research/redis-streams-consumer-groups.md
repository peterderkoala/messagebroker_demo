# Redis Streams with Consumer Groups via StackExchange.Redis (.NET)

## Summary

Redis Streams provide a durable, append-only log (`XADD`) that multiple independent readers can consume via **consumer groups** (`XGROUP CREATE` / `XREADGROUP` / `XACK`), giving at-least-once, competing-consumer delivery roughly analogous to a RabbitMQ queue with manual ack. In StackExchange.Redis (NuGet `StackExchange.Redis`, current stable **3.1.13**), the equivalent C# API is `IDatabase.StreamAdd` (produce), `IDatabase.StreamCreateConsumerGroup` (create the group, i.e. `XGROUP CREATE`), `IDatabase.StreamReadGroup` (consume as part of a group, i.e. `XREADGROUP`), and `IDatabase.StreamAcknowledge` (i.e. `XACK`). Unacknowledged messages sit in each group's Pending Entries List (PEL) and can be inspected with `StreamPendingMessages` (`XPENDING`) and reclaimed with `StreamAutoClaim` (`XAUTOCLAIM`) — the rough analog of RabbitMQ's nack/requeue/dead-lettering, but poll-based rather than push-based.

## Package / Version

- NuGet package: `StackExchange.Redis`
- Latest stable version as of 2026-08-19: **3.1.13** (released 2026-08-06), confirmed via both the NuGet package page and the matching GitHub release tag `3.1.13`.
- Target frameworks: .NET 6.0+ (includes .NET 10), .NET Standard 2.0, .NET Framework 4.6.1+.

`.csproj` reference:

```xml
<ItemGroup>
  <PackageReference Include="StackExchange.Redis" Version="3.1.13" />
</ItemGroup>
```

(Sources: NuGet package page, GitHub `releases/latest` API — see Sources section.)

## Concepts

### Stream

A Redis Stream is an append-only log identified by a key. Each entry has an auto-generated (or explicit) monotonically increasing ID of the form `<milliseconds>-<sequence>` (e.g. `1692632086370-0`) and a set of field/value pairs, similar to a lightweight hash. Entries are appended with `XADD` and are never mutated. [redis.io/streams]

### Consumer group

A consumer group (`XGROUP CREATE key group id|$ [MKSTREAM]`) is a named cursor plus a Pending Entries List (PEL) attached to a stream. Multiple named *consumers* within the same group compete for messages: Redis hands each new message to exactly one consumer in the group (round-robin-ish, not fan-out), analogous to multiple competing consumers on one RabbitMQ queue. The `id|$` argument sets where the group's read cursor starts:
- `$` — only messages added *after* the group is created (new messages only)
- `0` / `0-0` — replay the whole existing stream
- a specific ID — start from that point

`MKSTREAM` atomically creates the stream itself if it doesn't already exist, avoiding a "no such key" error when the producer hasn't sent anything yet. [redis.io/streams, XGROUP CREATE command page]

### XREADGROUP / StreamReadGroup and the PEL

`XREADGROUP GROUP group consumer [COUNT n] [BLOCK ms] [NOACK] STREAMS key id` reads on behalf of a named consumer inside a named group:
- Passing `>` as the id means "give me messages never yet delivered to any consumer in this group" — this is the normal "get new work" call.
- Passing an explicit ID (e.g. `0` or `0-0`) means "replay what's already pending/delivered to *this consumer*" — used for recovering a specific consumer's own in-flight messages after a crash/restart.
- Unless `NOACK` is specified, every message returned is added to the group's PEL (pending entries list) with the consumer's name, a delivery timestamp, and a delivery counter, and stays there until acknowledged. [XREADGROUP command page]

### XACK / StreamAcknowledge

`XACK key group id [id ...]` removes the given message ID(s) from the group's PEL, i.e. marks them as successfully processed. It does **not** delete the entry from the stream itself — it only clears the pending marker for that group. Returns the count of IDs actually removed. This is the functional equivalent of RabbitMQ's `basic.ack`. [XACK command page]

### Failure/retry: XPENDING, XCLAIM, XAUTOCLAIM

Because Streams are pull-based, there's no broker-side redelivery timer like RabbitMQ's consumer-ack-timeout+nack. Instead:
- `XPENDING key group [...]` inspects the PEL — how many messages are pending, for which consumers, and (in detailed mode) each message's idle time and delivery count. StackExchange.Redis exposes this as `StreamPending` (summary) and `StreamPendingMessages` (detailed, equivalent to `XPENDING key group IDLE min-idle-time start end count consumer`).
- `XCLAIM` / `XAUTOCLAIM` reassign ownership of messages that have been pending too long (idle beyond a threshold) to a different (often a "recovery") consumer, so they get worked again — the rough equivalent of a dead-letter/retry sweep you'd otherwise get from RabbitMQ's redelivery + DLX. `XAUTOCLAIM` is the more efficient, cursor-based bulk form (Redis 6.2+); `XCLAIM` operates on explicit message IDs. [XPENDING, XCLAIM command pages; StreamAutoClaim doc comment]

## Concrete C# API (StackExchange.Redis 3.1.13, `IDatabase`)

Pulled directly from `src/StackExchange.Redis/Interfaces/IDatabase.cs` and `IDatabaseAsync.cs` on the `main` branch (matches the 3.1.13 release tag):

```csharp
// Produce — XADD
RedisValue StreamAdd(
    RedisKey key,
    RedisValue streamField,
    RedisValue streamValue,
    RedisValue? messageId = null,
    long? maxLength = null,
    bool useApproximateMaxLength = false,
    long? limit = null,
    StreamTrimMode trimMode = StreamTrimMode.KeepReferences,
    CommandFlags flags = CommandFlags.None);

RedisValue StreamAdd(
    RedisKey key,
    NameValueEntry[] streamPairs,          // multiple field/value pairs in one entry
    RedisValue? messageId = null,
    long? maxLength = null,
    bool useApproximateMaxLength = false,
    long? limit = null,
    StreamTrimMode trimMode = StreamTrimMode.KeepReferences,
    CommandFlags flags = CommandFlags.None);

// Create/attach a consumer group — XGROUP CREATE
bool StreamCreateConsumerGroup(
    RedisKey key,
    RedisValue groupName,
    RedisValue? position = null,           // defaults to StreamPosition.NewMessages
    bool createStream = true,              // == MKSTREAM
    CommandFlags flags = CommandFlags.None);

// Consume within a group — XREADGROUP
StreamEntry[] StreamReadGroup(
    RedisKey key,
    RedisValue groupName,
    RedisValue consumerName,
    RedisValue? position = null,           // ">" for new, or an explicit ID to replay pending
    int? count = null,
    bool noAck = false,
    TimeSpan? claimMinIdleTime = null,
    CommandFlags flags = CommandFlags.None);

// Acknowledge — XACK
long StreamAcknowledge(RedisKey key, RedisValue groupName, RedisValue messageId, CommandFlags flags = CommandFlags.None);
long StreamAcknowledge(RedisKey key, RedisValue groupName, RedisValue[] messageIds, CommandFlags flags = CommandFlags.None);

// Inspect the PEL — XPENDING
StreamPendingInfo StreamPending(RedisKey key, RedisValue groupName, CommandFlags flags = CommandFlags.None);
StreamPendingMessageInfo[] StreamPendingMessages(
    RedisKey key, RedisValue groupName, int count, RedisValue consumerName,
    RedisValue? minId = null, RedisValue? maxId = null, long? minIdleTimeInMs = null,
    CommandFlags flags = CommandFlags.None);

// Reclaim stuck messages — XAUTOCLAIM
StreamAutoClaimResult StreamAutoClaim(
    RedisKey key, RedisValue consumerGroup, RedisValue claimingConsumer,
    long minIdleTimeInMs, RedisValue startAtId, int? count = null,
    CommandFlags flags = CommandFlags.None);
```

Every sync method above has a `...Async` twin on `IDatabaseAsync` with the same parameters wrapped in `Task<...>` (e.g. `Task<RedisValue> StreamAddAsync(...)`, `Task<bool> StreamCreateConsumerGroupAsync(...)`, `Task<StreamEntry[]> StreamReadGroupAsync(...)`, `Task<long> StreamAcknowledgeAsync(...)`).

Relevant supporting types:

```csharp
public readonly struct StreamEntry
{
    public RedisValue Id { get; }
    public NameValueEntry[] Values { get; }
    public RedisValue this[RedisValue fieldName] { get; } // indexer to read a field by name
}

public struct StreamPosition
{
    public static RedisValue Beginning => StreamConstants.ReadMinValue;  // "0-0" — replay whole stream
    public static RedisValue NewMessages => StreamConstants.NewMessages; // "$"   — only future entries
}

public readonly struct StreamPendingMessageInfo
{
    public RedisValue MessageId { get; }
    public RedisValue ConsumerName { get; }
    public long IdleTimeInMilliseconds { get; }
    public int DeliveryCount { get; }
}
```

Note: `StreamReadGroup` with `position: ">"` is the literal string StackExchange.Redis passes through to Redis for "new, undelivered messages" — pass the raw `RedisValue` `">"` (there is no named constant for it in the library; `StreamPosition.NewMessages`/`"$"` is only meaningful to `XGROUP CREATE`/starting cursor, not to `XREADGROUP`'s per-call read position).

`StreamCreateConsumerGroup` throws a `RedisServerException` (Redis `BUSYGROUP` error) if the group already exists, and also throws `RedisServerException` if the stream key doesn't exist and `createStream` is `false` — confirmed against StackExchange.Redis's own test suite (`tests/StackExchange.Redis.Tests/StreamTests.cs`, `StreamCreateConsumerGroupFailsIfKeyDoesntExist`). A demo worker should catch and ignore `RedisServerException` on group creation (idempotent "create group if not exists") or pass `createStream: true` and check the message for `BUSYGROUP`.

## Minimal standalone demo: producer + consumer worker

This is deliberately independent of any existing app flow — a pair of small console loops you'd run side-by-side with a RabbitMQ/MassTransit demo for comparison. Both connect via a single shared `ConnectionMultiplexer`.

```csharp
// Program.cs — shared connection setup
using StackExchange.Redis;

var redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");
IDatabase db = redis.GetDatabase();

const string StreamKey = "demo:orders";
const string GroupName = "order-processors";
```

### Producer loop (`StreamAdd` — XADD equivalent)

```csharp
async Task RunProducerAsync(IDatabase db, CancellationToken ct)
{
    var i = 0;
    while (!ct.IsCancellationRequested)
    {
        var orderId = Guid.NewGuid().ToString("N");
        RedisValue messageId = await db.StreamAddAsync(
            StreamKey,
            new NameValueEntry[]
            {
                new("orderId", orderId),
                new("amount", (100 + i).ToString()),
                new("createdAtUtc", DateTimeOffset.UtcNow.ToString("O")),
            });

        Console.WriteLine($"[producer] added {messageId} orderId={orderId}");
        i++;
        await Task.Delay(500, ct);
    }
}
```

### Consumer group setup + consume loop (`StreamCreateConsumerGroup` / `StreamReadGroup` / `StreamAcknowledge`)

```csharp
async Task EnsureConsumerGroupAsync(IDatabase db)
{
    try
    {
        // "0" replays the whole stream from the start; use StreamPosition.NewMessages ("$")
        // to only see entries added after the group is created.
        await db.StreamCreateConsumerGroupAsync(StreamKey, GroupName, StreamPosition.Beginning, createStream: true);
    }
    catch (RedisServerException ex) when (ex.Message.StartsWith("BUSYGROUP"))
    {
        // Group already exists — fine, this call is meant to be idempotent.
    }
}

async Task RunConsumerAsync(IDatabase db, string consumerName, CancellationToken ct)
{
    await EnsureConsumerGroupAsync(db);

    while (!ct.IsCancellationRequested)
    {
        // ">" = only messages never delivered to any consumer in this group (XREADGROUP ... STREAMS key >)
        StreamEntry[] entries = await db.StreamReadGroupAsync(
            StreamKey, GroupName, consumerName, position: ">", count: 10);

        if (entries.Length == 0)
        {
            await Task.Delay(1000, ct); // StackExchange.Redis has no BLOCK-based async read helper;
            continue;                   // poll instead (or use db.Multiplexer + a manual XREAD BLOCK via Execute if needed).
        }

        foreach (var entry in entries)
        {
            try
            {
                var orderId = entry["orderId"];
                var amount = entry["amount"];
                Console.WriteLine($"[{consumerName}] processing {entry.Id} orderId={orderId} amount={amount}");

                // ... do the actual work here ...

                await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id); // XACK
            }
            catch (Exception ex)
            {
                // Deliberately do NOT ack — message stays in the PEL and can be
                // reclaimed later via StreamAutoClaim (see below), analogous to a
                // RabbitMQ nack without requeue-to-front / a retry queue.
                Console.WriteLine($"[{consumerName}] failed {entry.Id}: {ex.Message}");
            }
        }
    }
}
```

### Recovering stuck/failed messages (`StreamAutoClaim` — XAUTOCLAIM equivalent)

Run this periodically (e.g. every N seconds) from a "sweeper" consumer to reclaim messages that some consumer read but never acked (crashed, threw, etc.):

```csharp
async Task ReclaimStuckMessagesAsync(IDatabase db, string sweeperConsumerName, TimeSpan minIdle)
{
    RedisValue cursor = "0-0";
    do
    {
        StreamAutoClaimResult result = await db.StreamAutoClaimAsync(
            StreamKey, GroupName, sweeperConsumerName,
            minIdleTimeInMs: (long)minIdle.TotalMilliseconds,
            startAtId: cursor);

        foreach (var entry in result.ClaimedEntries)
        {
            Console.WriteLine($"[sweeper] reclaimed {entry.Id}, reprocessing...");
            // ... reprocess, then ack ...
            await db.StreamAcknowledgeAsync(StreamKey, GroupName, entry.Id);
        }

        cursor = result.NextStartId;
    } while (cursor != "0-0"); // non-"0-0" cursor means "there may be more, keep going"
}
```

### Wiring it together

```csharp
var cts = new CancellationTokenSource();
var producer = RunProducerAsync(db, cts.Token);
var consumer1 = RunConsumerAsync(db, "consumer-1", cts.Token);
var consumer2 = RunConsumerAsync(db, "consumer-2", cts.Token); // competing consumer, same group

await Task.WhenAll(producer, consumer1, consumer2);
```

`consumer-1` and `consumer-2` share `GroupName`, so Redis hands each new stream entry to only one of them — the direct analog of two competing MassTransit consumers reading off the same RabbitMQ queue.

## Comparison notes vs. RabbitMQ/MassTransit ack semantics

| Concept | RabbitMQ/MassTransit | Redis Streams |
|---|---|---|
| Enqueue | `IPublishEndpoint.Publish` / `basic.publish` | `StreamAdd` (`XADD`) |
| Delivery to a competing-consumer set | Queue bound to consumers | Consumer group (`XGROUP CREATE`) + named consumers |
| Receive | Push-based delivery to consumer | Pull-based `StreamReadGroup` (`XREADGROUP`), typically polled |
| Ack success | `ReceiveContext`/auto-ack on successful consumer return (`basic.ack`) | `StreamAcknowledge` (`XACK`) |
| Nack / retry | `basic.nack` / MassTransit retry middleware / redelivery, optional DLX | Simply don't ack; message stays in the PEL; sweep with `StreamAutoClaim`/`XCLAIM` after an idle threshold — retry/DLX logic must be built by the consumer, not the broker |
| Poison message handling | MassTransit built-in retry + error queue | Manual: track `DeliveryCount` from `StreamPendingMessageInfo`/`StreamAutoClaimResult`, decide to drop/dead-letter yourself |

This makes clear that Redis Streams gives you the primitives (log + group cursor + PEL) but none of the retry/backoff/dead-lettering policy that MassTransit provides out of the box on top of RabbitMQ — that logic has to be hand-rolled around `StreamPendingMessages`/`StreamAutoClaim` in a demo worker.

## Sources

- https://redis.io/docs/latest/develop/data-types/streams/ — Stream data model, XADD/XGROUP CREATE/XREADGROUP/XACK/XPENDING/XCLAIM/XAUTOCLAIM command syntax, behavior, and Python-style producer/consumer/recovery example pattern used to confirm the overall workflow shape.
- https://seredis.dev/Streams (canonical redirect target of https://stackexchange.github.io/StackExchange.Redis/Streams) — StackExchange.Redis's own Streams doc page: `StreamAdd`, `StreamCreateConsumerGroup`, `StreamReadGroup`, `StreamPendingMessages`, `StreamAcknowledge`, `StreamClaim` C# usage examples.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/Interfaces/IDatabase.cs — authoritative sync method signatures and XML doc comments for `StreamAdd`, `StreamCreateConsumerGroup`, `StreamReadGroup`, `StreamAcknowledge`, `StreamPendingMessages`, `StreamAutoClaim`, `StreamClaim`, `StreamPending`.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/Interfaces/IDatabaseAsync.cs — authoritative async (`...Async`) method signatures confirming a 1:1 async counterpart for every sync stream method used above.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/APITypes/StreamEntry.cs — `StreamEntry.Id`, `StreamEntry.Values`, indexer-by-field-name.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/APITypes/StreamPosition.cs — `StreamPosition.Beginning` (`0-0`) and `StreamPosition.NewMessages` (`$`) constants.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/APITypes/StreamPendingMessageInfo.cs — `MessageId`, `ConsumerName`, `IdleTimeInMilliseconds`, `DeliveryCount` fields used for PEL inspection/retry logic.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/src/StackExchange.Redis/APITypes/StreamAutoClaimResult.cs — `NextStartId`, `ClaimedEntries`, `DeletedIds` fields for the `StreamAutoClaim` cursor loop.
- https://raw.githubusercontent.com/StackExchange/StackExchange.Redis/main/tests/StackExchange.Redis.Tests/StreamTests.cs — confirms real call shapes (`StreamCreateConsumerGroup(key, group, createStream: true)`, `StreamReadGroup(key, group, consumer, ">")`, etc.) and confirms `StreamCreateConsumerGroup` throws `RedisServerException` when the stream key doesn't exist and `createStream` is not set (`StreamCreateConsumerGroupFailsIfKeyDoesntExist` test).
- https://www.nuget.org/packages/StackExchange.Redis — confirms latest stable NuGet version **3.1.13** and supported target frameworks (.NET 6.0+, .NET Standard 2.0, .NET Framework 4.6.1+).
- https://api.github.com/repos/StackExchange/StackExchange.Redis/releases/latest — confirms GitHub release tag `3.1.13`, published 2026-08-06, matching the NuGet version.
