# MassTransit: Transactional Outbox, Retry, and Dead-Letter Handling
### RabbitMQ + EF Core + Postgres, .NET 10

Researched 2026-08-19.

---

## Answer summary

- **Outbox**: Use `AddEntityFrameworkOutbox<TDbContext>(o => { o.UsePostgres(); o.UseBusOutbox(); })` inside `AddMassTransit`. This makes `IPublishEndpoint`/`ISendEndpointProvider` write to an `OutboxMessage` table **in the same EF Core `DbContext`/transaction** as your `SaveChangesAsync()` — nothing is sent to RabbitMQ synchronously. A separate hosted delivery service polls `OutboxState`/`OutboxMessage` and publishes to the broker afterward, so the "publish + save" pair is atomic (all-or-nothing) even though broker delivery is eventually consistent.
- **Consistency guarantee**: transactional consistency comes purely from the outbox row(s) being written in the *same DB transaction* as your domain writes — if `SaveChangesAsync()` fails/rolls back, the outbox row never commits and nothing is ever published. The actual RabbitMQ publish happens later, out-of-band, by the outbox delivery hosted service.
- **Retry policy**: configured with `UseMessageRetry(r => r.Interval(...) / r.Intervals(...) / r.Exponential(...) / r.Incremental(...) / r.Immediate(...))`, settable bus-wide, per-`ReceiveEndpoint`, or per-consumer via `ConsumerDefinition.ConfigureConsumer`. This is **in-memory, fast retry** (holds the message/lock) meant for transient errors — it is orthogonal to the outbox's own delivery retry (the outbox delivery service retries publishing from the outbox table independently, governed by `MessageDeliveryLimit`/`MessageDeliveryTimeout` on `UseBusOutbox`).
- **DLQ/error queue**: MassTransit + RabbitMQ automatically creates a `<queue-name>_error` queue per receive endpoint (no explicit config needed) — a faulted message lands there once all in-memory retries (and, if configured, all redelivery attempts) are exhausted. There is also an automatic `<queue-name>_skipped` queue for messages with no matching consumer. Between "fast retry" and the error queue sits an optional second tier, **scheduled redelivery** (`UseDelayedRedelivery` / `UseScheduledRedelivery`), which re-queues the message after a longer delay (e.g. 5/15/30 min) before falling back to retry-then-error-queue.
- **Version / .NET 10**: As of this research (2026-08-19), MassTransit's GitHub org shows the last fully open-source (Apache-2.0) release as **v8.5.10** (tag `v8.5.10`, June 2026), which the maintainer confirmed targets `net10.0` as of `8.5.8`. In **April 2025 MassTransit announced a move to a commercial license starting with v9**; NuGet currently lists **MassTransit 9.2.0** (July 27, 2026) as the latest package, targeting `net8.0`/`net9.0`/`net10.0`/`netstandard2.0`/`net472`, but v9 requires a paid license (free tier only for orgs under ~$1M revenue, otherwise ~$400/mo minimum) — v8 remains free/OSS and is expected to receive security/critical fixes only through end of 2026.
- **Caveat**: the official docs domain `masstransit.io` now HTTP-redirects (307, verified independently via `curl -I`, not just via the fetch tool) to `masstransit.massient.com` — presumably the new site for the commercial entity behind v9. Content there is consistent with what I remember of the OSS v8 docs and corroborates independently found NuGet/GitHub data, so it's treated here as the current primary source, but this redirect is unusual enough to flag explicitly — verify it still points there before trusting links blindly in the future.

---

## 1. Outbox setup

### NuGet packages (pick versions based on which MassTransit major you adopt — see §4)

For the free/OSS line (MassTransit 8.5.x, last OSS tag `v8.5.10`):

```bash
dotnet add package MassTransit --version 8.5.10
dotnet add package MassTransit.RabbitMQ --version 8.5.10
dotnet add package MassTransit.EntityFrameworkCore --version 8.5.10
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL   # your normal EF Core/Postgres provider
```

For the commercial v9 line (requires a MassTransit license — see §4 caveats):

```bash
dotnet add package MassTransit --version 9.2.0
dotnet add package MassTransit.RabbitMQ --version 9.2.0
dotnet add package MassTransit.EntityFrameworkCore --version 9.2.0
```

Sources: [NuGet: MassTransit](https://www.nuget.org/packages/MassTransit), [NuGet: MassTransit.RabbitMQ](https://www.nuget.org/packages/MassTransit.RabbitMQ), [NuGet: MassTransit.EntityFrameworkCore](https://www.nuget.org/packages/MassTransit.EntityFrameworkCore), [GitHub tags](https://github.com/MassTransit/MassTransit/tags).

### DbContext: add the outbox tables

The EF outbox needs three tables added to your `DbContext` (`InboxState`, `OutboxMessage`, `OutboxState`):

```csharp
public class NotificationDbContext : DbContext
{
    public NotificationDbContext(DbContextOptions<NotificationDbContext> options) : base(options) { }

    // ... your domain DbSets (e.g. NotificationRequests) ...

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
    }
}
```

Source: [MassTransit Outbox Configuration docs](https://masstransit.massient.com/configuration/middleware/outbox) (current docs domain — see caveat in §4; `masstransit.io/advanced/transactional-outbox.html` redirects here).

Generate/apply an EF Core migration afterward as normal (`dotnet ef migrations add AddMassTransitOutbox`).

### `AddMassTransit` wiring

```csharp
builder.Services.AddDbContext<NotificationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("NotificationDb")));

builder.Services.AddMassTransit(x =>
{
    x.AddEntityFrameworkOutbox<NotificationDbContext>(o =>
    {
        o.UsePostgres();               // selects Postgres as the outbox lock/query provider
        o.UseBusOutbox(bo =>
        {
            bo.MessageDeliveryLimit = 100;                 // max messages delivered per poll batch
            bo.MessageDeliveryTimeout = TimeSpan.FromSeconds(45);
            bo.ConcurrentDeliveryLimit = 10;
        });

        o.QueryDelay = TimeSpan.FromSeconds(1);             // idle poll interval when no messages pending
        o.QueryMessageLimit = 10;                            // batch size read from OutboxMessage table
        o.DuplicateDetectionWindow = TimeSpan.FromMinutes(30); // inbox dedupe retention (consumer-side inbox)
        // o.DisableInboxCleanupService();                   // opt out of automatic InboxState cleanup hosted service
    });

    x.AddConsumer<NotificationRequestedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host("rabbitmq://localhost", h =>
        {
            h.Username("guest");
            h.Password("guest");
        });

        cfg.ConfigureEndpoints(context);
    });
});
```

Notes on the pieces:

- `UsePostgres()` — chooses the Postgres-specific SQL used by the outbox's row-locking/query logic (alternatives: `UseSqlServer()`, `UseMySql()`). It does **not** by itself configure your connection string — that comes from your normal `DbContext`/`UseNpgsql(...)` registration; the outbox reuses your `DbContext`.
- `UseBusOutbox()` — this is the piece that makes `IPublishEndpoint`/`ISendEndpointProvider`/`IRequestClient` calls made **outside of a consumer** (e.g. from a controller/service) buffer into the outbox table instead of going straight to RabbitMQ. Without `UseBusOutbox()`, `AddEntityFrameworkOutbox` only wires up the **consumer inbox** (dedupe + outbox-inside-a-consumer), not the "publish-from-a-web-request" pattern the question asks about.
- `QueryDelay` / `QueryMessageLimit` — control the polling hosted service that reads pending `OutboxMessage` rows and publishes them to the transport; this is the "separate delivery service" referenced below.
- `DuplicateDetectionWindow` and the inbox cleanup service — govern retention/cleanup of processed `InboxState` rows; `o.DisableInboxCleanupService()` turns that hosted service off if you want to manage cleanup yourself.

Source: [MassTransit Outbox Configuration docs](https://masstransit.massient.com/configuration/middleware/outbox).

### Publishing inside the same unit-of-work (controller/service pattern)

```csharp
public class NotificationRequestsController : ControllerBase
{
    private readonly NotificationDbContext _db;
    private readonly IPublishEndpoint _publishEndpoint;

    public NotificationRequestsController(NotificationDbContext db, IPublishEndpoint publishEndpoint)
    {
        _db = db;
        _publishEndpoint = publishEndpoint;
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateNotificationRequestDto dto, CancellationToken ct)
    {
        var entity = new NotificationRequest
        {
            Id = Guid.NewGuid(),
            Recipient = dto.Recipient,
            Payload = dto.Payload,
            CreatedAt = DateTime.UtcNow,
        };

        _db.NotificationRequests.Add(entity);

        // With UseBusOutbox() registered, this call does NOT hit RabbitMQ directly.
        // It writes a row into the OutboxMessage table via the same DbContext instance.
        await _publishEndpoint.Publish(new NotificationRequested
        {
            NotificationRequestId = entity.Id,
            Recipient = entity.Recipient,
            Payload = entity.Payload,
        }, ct);

        // Single SaveChangesAsync commits BOTH the domain row and the outbox row
        // in one DB transaction. If this throws, neither is persisted, and the
        // event is never delivered — no manual transaction management needed.
        await _db.SaveChangesAsync(ct);

        return Accepted();
    }
}
```

**Why this is transactionally consistent**: `AddEntityFrameworkOutbox<NotificationDbContext>` + `UseBusOutbox()` intercepts calls made through `IPublishEndpoint`/`ISendEndpointProvider` that are resolved from the same DI scope as `NotificationDbContext` and appends the outgoing message as a row in the `OutboxMessage` table using **that same `DbContext` instance** — no separate connection/transaction is opened for the publish call itself. Because both the domain entity add and the outbox message write go through the same `DbContext`, a single `SaveChangesAsync()` wraps both in one Postgres transaction: either both commit or neither does. Actual delivery to RabbitMQ is deliberately **not** part of that transaction — instead, a MassTransit-hosted background service (governed by `QueryDelay`/`QueryMessageLimit`/the `UseBusOutbox` settings) polls `OutboxMessage`/`OutboxState`, publishes to the broker, and marks the row delivered. This is the classic outbox pattern: DB write is atomic and synchronous; broker delivery is asynchronous and at-least-once (the delivery service retries until it succeeds, and `MessageDeliveryTimeout`/`MessageDeliveryLimit` bound that process).

Source: [MassTransit Outbox Configuration docs](https://masstransit.massient.com/configuration/middleware/outbox); general pattern confirmed by community write-up [Use MassTransit To Implement Outbox Pattern with EF Core](https://antondevtips.com/blog/use-masstransit-to-implement-outbox-pattern-with-ef-core-and-mongodb) (secondary source, used only to corroborate — not cited for API specifics).

---

## 2. Retry policy shape

### Fluent API

`UseMessageRetry(Action<IRetryConfigurator> configure)` accepts one of several named policies:

| Policy | Meaning |
|---|---|
| `r.None()` | no retry |
| `r.Immediate(retryLimit)` | retry immediately (no delay), up to `retryLimit` attempts |
| `r.Interval(retryLimit, interval)` | fixed delay between each retry, up to `retryLimit` |
| `r.Intervals(TimeSpan, TimeSpan, ...)` | explicit list of delays, one retry per interval given |
| `r.Exponential(retryLimit, minInterval, maxInterval, intervalDelta)` | exponentially increasing delay bounded by min/max, up to `retryLimit` |
| `r.Incremental(retryLimit, initialInterval, intervalIncrement)` | linearly increasing delay, up to `retryLimit` |

Example (bus-level, applied to all endpoints unless overridden):

```csharp
x.UsingRabbitMq((context, cfg) =>
{
    cfg.UseMessageRetry(r => r.Exponential(
        retryLimit: 5,
        minInterval: TimeSpan.FromMilliseconds(200),
        maxInterval: TimeSpan.FromSeconds(30),
        intervalDelta: TimeSpan.FromSeconds(2)));

    cfg.ConfigureEndpoints(context);
});
```

Example (endpoint-level with exception filtering):

```csharp
cfg.ReceiveEndpoint("notification-requested", e =>
{
    e.ConfigureConsumer<NotificationRequestedConsumer>(context);

    e.UseMessageRetry(r =>
    {
        r.Interval(5, TimeSpan.FromSeconds(1));
        r.Handle<SmtpException>();
        r.Ignore<ValidationException>();
    });
});
```

Example (per-consumer, via `ConsumerDefinition`, recommended so retry policy travels with the consumer):

```csharp
public class NotificationRequestedConsumerDefinition : ConsumerDefinition<NotificationRequestedConsumer>
{
    public NotificationRequestedConsumerDefinition()
    {
        EndpointName = "notification-requested";
        ConcurrentMessageLimit = 8;
    }

    protected override void ConfigureConsumer(
        IReceiveEndpointConfigurator endpointConfigurator,
        IConsumerConfigurator<NotificationRequestedConsumer> consumerConfigurator)
    {
        endpointConfigurator.UseMessageRetry(r => r.Intervals(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(10)));
    }
}
```

Register it alongside the consumer: `x.AddConsumer<NotificationRequestedConsumer, NotificationRequestedConsumerDefinition>();`

Source: [MassTransit Message Retry Configuration docs](https://masstransit.massient.com/configuration/middleware/retry); consumer-definition pattern corroborated by [MassTransit Discussion #4982 "Consumer Definition - how to set retry in 8.1.3"](https://github.com/MassTransit/MassTransit/discussions/4982).

### Relationship to the outbox's own delivery semantics

These are two **independent** retry mechanisms:

- `UseMessageRetry` retries **message consumption** — it holds the message lock in memory and re-invokes the consumer's `Consume` method on failure, without the message ever leaving the queue. Per the docs, this filter "execute[s] in memory and maintain[s] a lock on the message" and is meant only for short/transient failures, not long outages.
- The outbox's **delivery service** (configured via `UseBusOutbox`'s `MessageDeliveryLimit` / `MessageDeliveryTimeout` / `ConcurrentDeliveryLimit`) separately retries **publishing a buffered `OutboxMessage` row to RabbitMQ** if the broker is temporarily unreachable when the hosted delivery service tries to flush the outbox. This has nothing to do with consumer-side retry — it only concerns getting an already-committed outbox row onto the broker.

In other words: `UseMessageRetry`/redelivery protect the **consumer** side (processing `NotificationRequested`); the outbox delivery settings protect the **publish** side (getting `NotificationRequested` from your Postgres outbox table onto the RabbitMQ exchange). Both can be in play in the same system without conflicting.

Source: [MassTransit Outbox Configuration docs](https://masstransit.massient.com/configuration/middleware/outbox), [MassTransit Message Retry Configuration docs](https://masstransit.massient.com/configuration/middleware/retry).

---

## 3. Dead-letter / redelivery handling

### Error queue naming (RabbitMQ transport)

MassTransit automatically provisions, per receive endpoint, in addition to the main queue:

- `<queue-name>_error` — where a message lands once it has faulted and exhausted all configured retry (and redelivery, if configured) attempts. Exception details (`Fault<T>` info: exception type/message/stack, host info, timestamp) are attached as message headers for troubleshooting.
- `<queue-name>_skipped` — where a message lands if it's read off the queue but no consumer/handler/saga is registered to accept that message type.

This is **automatic** — no explicit topology configuration is required to get an error queue; MassTransit creates the exchange/queue bindings for both when the receive endpoint starts. You do not manually declare a "DLQ" the way you would with raw RabbitMQ dead-letter-exchange config — MassTransit's error queue is not a native RabbitMQ DLX by default, it's application-level (the bus explicitly publishes/moves the faulted message there after retries are exhausted), separate from RabbitMQ's own broker-native DLX feature (which MassTransit does use internally for `UseQueueBasedDelayedRedelivery`, described below).

Sources: [MassTransit Exceptions concepts docs](https://masstransit.massient.com/concepts/exceptions), [MassTransit RabbitMQ transport docs](https://masstransit.massient.com/configuration/transports/rabbitmq).

### How many retries before the error queue?

The primary-source docs describe the *behavior* (faulted messages "are moved to the `_error` queue... after retries are exhausted") but do not state a universal default retry count — it's whatever `UseMessageRetry` (and, if configured, `UseScheduledRedelivery`/`UseDelayedRedelivery`) you set. If you configure **no** retry policy at all, a single failed delivery attempt faults immediately and the message goes straight to `_error`. I could not find a documented "default" retry count applied automatically absent explicit configuration — treat "0 retries by default" as the safe assumption unless you call `UseMessageRetry`.

### Redelivery ("second-level retry") — the tier between retry and the error queue

Redelivery removes the message from the queue and re-publishes it back onto the same queue after a longer delay, as opposed to retry which holds it in memory. Current (2026) primary-source API surface is:

- `cfg.UseDelayedRedelivery(r => ...)` — uses RabbitMQ's delayed-message-exchange plugin (or transport-native scheduling) to redeliver after the given delays. Preferred over the Quartz/Hangfire-based option because it doesn't overload an external scheduler.
- `cfg.UseScheduledRedelivery(r => ...)` — requires a message scheduler (transport-based, Quartz.NET, or Hangfire) configured via `cfg.UseMessageScheduler(...)` / `cfg.UseDelayedMessageScheduler()`.
- `cfg.UseQueueBasedDelayedRedelivery(r => ...)` — a plugin-free RabbitMQ-specific alternative that implements the delay using native **message TTL + dead-letter exchanges**: it creates an `mt-delay-return` direct exchange plus per-interval `mt-delay-{ms}` exchange/queue pairs with TTL, and relies on RabbitMQ's own DLX to bounce the message back after the TTL expires. This is the closest thing to "true" broker-native DLX usage in the whole pipeline.

Example combining fast in-memory retry with longer scheduled redelivery (order of calls matters — `UseDelayedRedelivery`/`UseScheduledRedelivery` must be configured **before** `UseMessageRetry` in the pipe, per a documented gotcha):

```csharp
cfg.ReceiveEndpoint("notification-requested", e =>
{
    e.ConfigureConsumer<NotificationRequestedConsumer>(context);

    // Second-level: after in-memory retries below are exhausted, requeue
    // 3 more times at longer intervals before giving up.
    e.UseDelayedRedelivery(r => r.Intervals(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30)));

    // First-level: fast, in-memory retries for transient failures.
    e.UseMessageRetry(r => r.Immediate(5));
});
```

With this configuration: 5 immediate in-process retries, then if still failing the message is requeued (visible again on the broker) and retried again with another 5 immediate attempts after each of the 5/15/30-minute delays; only after all of that is exhausted does it fault to `notification-requested_error`.

Caveat: some older MassTransit (v7-era) documentation and blog posts describe an outer `UseMessageRedelivery(...)` wrapper call around `r.UseScheduledRedelivery(...)`. Current primary-source docs at `masstransit.massient.com/configuration/middleware/redelivery` present `UseDelayedRedelivery` / `UseScheduledRedelivery` / `UseQueueBasedDelayedRedelivery` as direct top-level `IReceiveEndpointConfigurator`/bus-config extension methods, not nested under a separate `UseMessageRedelivery` call — I could not find `UseMessageRedelivery` as a distinct current API surface and would not recommend relying on it without checking the installed package's IntelliSense/source, since this is exactly the kind of thing that shifts between majors.

Sources: [MassTransit Message Redelivery Configuration docs](https://masstransit.massient.com/configuration/middleware/redelivery), ordering gotcha corroborated by [GitHub Issue #1575 "Calls to UseMessageRetry and UseScheduledRedelivery require specific order"](https://github.com/MassTransit/MassTransit/issues/1575).

### `ConfigureConsumeTopology`

`ReceiveEndpoint`/bus-level `ConfigureConsumeTopology` (bool, default `true`) controls whether MassTransit creates/binds the *consume*-side exchange topology (the exchanges/bindings that route published messages into your endpoint's queue) — setting it `false` is used when you want to consume from a pre-existing/externally-managed queue without MassTransit creating exchange bindings for message types it doesn't own. It does **not** disable creation of the endpoint's own queue or its `_error`/`_skipped` companions — those are still created regardless, per the RabbitMQ transport docs and corroborating community discussion.

Source: [MassTransit RabbitMQ transport docs](https://masstransit.massient.com/configuration/transports/rabbitmq); corroborated by community discussion threads found via search (not independently primary-verified for this specific flag's exact default beyond what's stated in the transport doc).

---

## 4. Version / compatibility notes

- **GitHub tags** (`https://github.com/MassTransit/MassTransit/tags`, checked via `gh api` on 2026-08-19) show the newest tag as **`v8.5.10`**, with no `v9.x` tags present in the public repository. This is the last tag in the still-open-source (Apache 2.0) line.
- **NuGet** (`nuget.org/packages/MassTransit`, `.../MassTransit.RabbitMQ`, `.../MassTransit.EntityFrameworkCore`, checked 2026-08-19) lists **9.2.0** (released 2026-07-27) as the current stable package, with a `9.2.1-develop.167` prerelease also visible, and **8.5.10** as the latest package in the 8.x line. All of these list target frameworks including `net8.0`, `net9.0`, `net10.0`, `netstandard2.0`, and `net472` — i.e. MassTransit ships multi-targeted packages that explicitly include `net10.0`, it is not simply "net8.0 code that happens to run on .NET 10."
- **.NET 10 support confirmation**: in [GitHub Discussion #6205 ".net 10 Support"](https://github.com/MassTransit/MassTransit/discussions/6205), maintainer `phatboyg` states ".NET 10 has been supported since release," pointing to package version **8.5.8** (referencing issue #6149) as where `net10.0` was added as an explicit target.
- **Commercial licensing change**: MassTransit announced in **April 2025** that v9 moves to a commercial license (separate from the AutoMapper/MediatR announcements around the same time from the same author ecosystem). v8 remains Apache-2.0/free and is slated to receive security and critical bug fixes only through roughly end of 2026; v9 requires a paid MassTransit license (a free tier reportedly exists for organizations under ~$1M annual revenue; otherwise pricing starts around $400/month). This materially affects which version you should target for a new project — corroborated by multiple independent secondary sources: [Milan Jovanović — "MediatR and MassTransit Going Commercial"](https://milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you), [Hacker News discussion](https://news.ycombinator.com/item?id=43565690), [ABP.IO — "AutoMapper, MediatR and MassTransit Are Going Commercial"](https://medium.com/volosoft/automapper-mediatr-and-masstransit-are-going-commercial-whats-happening-3ed3d8a5d68c). I was not able to fetch MassTransit's own licensing page directly (`masstransit.massient.com/configuration/license` appeared in search results but was not independently re-fetched with full verbatim terms), so treat the *exact* pricing figures above as reported-by-secondary-sources, not independently confirmed against MassTransit's own pricing page.
- **Recommendation for this project**: given the RabbitMQ + EF Core + Postgres + .NET 10 stack described, and that this appears to be a demo/learning project, **MassTransit 8.5.10** is very likely the more appropriate choice — it is free, still actively receiving fixes through 2026, and already targets `net10.0`. All API surfaces documented above (`AddEntityFrameworkOutbox`, `UseBusOutbox`, `UseMessageRetry`, `UseDelayedRedelivery`/`UseScheduledRedelivery`) are present in the 8.x line per the docs fetched. Only move to 9.x if you specifically need post-8.5 features and are prepared to acquire a commercial license.

### Domain/trust caveat (important, please read)

While researching, `https://masstransit.io/...` consistently HTTP-redirected (`307`, verified with a direct `curl -I`, independent of the fetch tool) to `https://masstransit.massient.com/...`. This is unusual for what used to be the canonical OSS project domain, and is most plausibly explained by the April 2025 commercial relaunch (the commercial entity behind v9 appears to have taken over the docs domain and rebranded the docs site as "Massient"). The content fetched from `masstransit.massient.com` was internally consistent, matched community/secondary-source descriptions of MassTransit's actual v8 API, and was cross-corroborated by independent GitHub/NuGet lookups — so I've treated it as trustworthy primary-source content in this document. However, I could not verify the domain's ownership/authenticity through an independent channel (e.g. no WHOIS lookup was performed), so **flagging this explicitly**: before relying further on `masstransit.massient.com` content in production decision-making, it would be worth confirming from another independent channel (e.g. the official MassTransit GitHub `README.md`, which should still link to whatever the maintainers consider the canonical docs site) that this redirect is legitimate and not a hijacked/expired domain serving misleading content.

---

## Sources consulted

- [MassTransit GitHub repo — tags](https://github.com/MassTransit/MassTransit/tags)
- [MassTransit GitHub repo — releases](https://github.com/MassTransit/MassTransit/releases)
- [GitHub Discussion #6205 — .NET 10 Support](https://github.com/MassTransit/MassTransit/discussions/6205)
- [GitHub Issue #1575 — UseMessageRetry/UseScheduledRedelivery ordering](https://github.com/MassTransit/MassTransit/issues/1575)
- [GitHub Discussion #4982 — Consumer Definition retry config](https://github.com/MassTransit/MassTransit/discussions/4982)
- [NuGet — MassTransit](https://www.nuget.org/packages/MassTransit)
- [NuGet — MassTransit.RabbitMQ](https://www.nuget.org/packages/MassTransit.RabbitMQ)
- [NuGet — MassTransit.EntityFrameworkCore](https://www.nuget.org/packages/MassTransit.EntityFrameworkCore)
- [MassTransit docs — Outbox Configuration](https://masstransit.massient.com/configuration/middleware/outbox) (redirect target of `masstransit.io/advanced/transactional-outbox.html`)
- [MassTransit docs — Retry Configuration](https://masstransit.massient.com/configuration/middleware/retry)
- [MassTransit docs — Redelivery Configuration](https://masstransit.massient.com/configuration/middleware/redelivery)
- [MassTransit docs — Exceptions concepts](https://masstransit.massient.com/concepts/exceptions)
- [MassTransit docs — RabbitMQ transport](https://masstransit.massient.com/configuration/transports/rabbitmq)
- [Milan Jovanović — MediatR and MassTransit Going Commercial](https://milanjovanovic.tech/blog/mediatr-and-masstransit-going-commercial-what-this-means-for-you) (secondary, licensing context)
- [Hacker News — .NET library MassTransit going commercial with V9](https://news.ycombinator.com/item?id=43565690) (secondary, licensing context)
