# MassTransit: Topic Exchange Publish Topology, Routing Keys, and the Outbox
### RabbitMQ + EF Core Outbox, MassTransit 8.5.10, .NET 10 (ADR-0003)

Researched 2026-08-19.

---

## Answer summary

- **Publish to a named topic exchange (not the type exchange)**: override the *message topology* entity name with `cfg.Message<NotificationRequested>(x => x.SetEntityName("notification"))`, then set the exchange kind with `cfg.Publish<NotificationRequested>(x => x.ExchangeType = ExchangeType.Topic)`. `SetEntityName` lives in generic `MassTransit` (`IMessageTopologyConfigurator<T>`); `ExchangeType` is RabbitMQ-specific (`IRabbitMqMessagePublishTopologyConfigurator<T>` → `IRabbitMqExchangeConfigurator`). Together they replace the default "one fanout exchange per message type, named after the fully-qualified type" behaviour with a single named topic exchange.
- **Per-message routing key**: the fluent method in 8.5.10 is `UseRoutingKeyFormatter`, **not** `SetRoutingKeyFormatter` (that name doesn't exist in this codebase — the issue's phrasing was speculative). It is configured on **Send topology**, not Publish topology — `cfg.Send<NotificationRequested>(x => x.UseRoutingKeyFormatter(context => ChannelRouting.RoutingKeyFor(context.Message.Channel)))` — because `IRabbitMqMessagePublishTopologyConfigurator<T>` does not expose it; only `IMessageSendTopologyConfigurator<T>` does. This works for `Publish()` calls anyway because RabbitMQ's publish path resolves the exchange to a `SendContext` and runs it through the same `SetRoutingKeyFilter<T>` that a direct `Send()` would.
- **Receive endpoint binding + suppressing the default type-exchange bind**: `e.Bind("notification", x => { x.ExchangeType = ExchangeType.Topic; x.RoutingKey = ChannelRouting.RoutingKeyFor(Channel.Email); })` declares the topic exchange and binds the endpoint's queue to it with a specific binding key. Separately, `e.ConfigureConsumeTopology = false` stops MassTransit from *also* auto-binding the endpoint's queue to the default per-message-type exchange for every consumed message type — this is the exact switch that prevents ADR-0003's failure mode (every Channel Service receiving every `NotificationRequested`, topic routing or not).
- **Q4 — does the EF Core outbox preserve the custom routing key on delivery? Yes, confidently, confirmed by source.** The routing key (and the destination exchange name/type) are captured as RabbitMQ transport **"send properties"** — not as an ordinary header — at the moment the message is staged into the `OutboxMessage` row (`TransportSendContext.WritePropertiesTo`, called from `EntityFrameworkOutboxExtensions.AddSend<T>`), stored in the `OutboxMessage.Properties` column, and restored onto a brand-new `SendContext` the moment the outbox's background delivery service actually ships the message (`OutboxMessageSendPipe.Send` → `TransportSendContext.ReadPropertiesFrom`, which for RabbitMQ resolves to `RabbitMqMessageSendContext<T>.RoutingKey = ReadString(properties, RoutingKey, "")`). Delivery does not re-run the routing-key formatter and does not need to — it round-trips the already-computed value end to end. See §4 for the full call chain with line numbers.
- **Q5 — do `_error`/`_skipped` still get created under custom topology? Yes, unconditionally.** `RabbitMqReceiveEndpointBuilder.CreateReceiveEndpointContext()` calls `CreateErrorTransport()` and `CreateDeadLetterTransport()` (source of the `_skipped` queue, MassTransit's internal name for what the docs call the "skipped"/no-consumer queue) every time a receive endpoint is built, with no reference to `ConfigureConsumeTopology` at all. That flag only gates `RabbitMqReceiveEndpointBuilder.ConnectConsumePipe<T>`'s call to `topology.Bind()` — the per-message-type auto-bind to the default exchange. Error/skipped provisioning is driven entirely by **Send topology** (`_configuration.Topology.Send.GetErrorSettings(...)`), a different object graph than Consume topology, so it is structurally incapable of being switched off by the same flag.

---

## 1. Publishing to a named topic exchange instead of the type exchange

By default, MassTransit's RabbitMQ publish topology creates one exchange per published message's CLR type (name derived from the type's namespace+name, fanout by default) and fans out to every bound queue — this is exactly what ADR-0003 rejects (`docs/adr/0003-caller-selected-channels-via-topic-exchange.md:7`).

Two independent settings override this, and both are necessary:

```csharp
using MassTransit;
using NotificationPlatform.Contracts;

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        // 1. Entity name: which exchange this message type maps to (generic MassTransit,
        //    not RabbitMQ-specific — same call shape works for every transport).
        cfg.Message<NotificationRequested>(m => m.SetEntityName(ChannelRouting.ExchangeName)); // "notification"

        // 2. Exchange kind: RabbitMQ-specific, only reachable via cfg.Publish<T>.
        cfg.Publish<NotificationRequested>(p => p.ExchangeType = ExchangeType.Topic);

        cfg.ConfigureEndpoints(context);
    });
});
```

- `SetEntityName` is declared on `IMessageTopologyConfigurator<TMessage>` (generic topology, `src/MassTransit.Abstractions/Configuration/Topology/IMessageTopologyConfigurator.cs`) — it is what the docs page describes as overriding "default naming for messaging entities." Confirmed live on `masstransit.massient.com/documentation/configuration/topology` (the current canonical docs host — `masstransit.io` 307-redirects there; see the caveat in the prior outbox/retry/DLQ research, `docs/research/masstransit-outbox-retry-dlq.md` §"Domain/trust caveat").
- `ExchangeType` on `cfg.Publish<T>(...)` is defined by `IRabbitMqExchangeConfigurator.ExchangeType { set; }` (`src/Transports/MassTransit.RabbitMqTransport/Configuration/Topology/IRabbitMqPublishTopologyConfigurator.cs`, inherited by `IRabbitMqMessagePublishTopologyConfigurator<T>`), backed by `RabbitMQ.Client.ExchangeType.Topic` (the literal string `"topic"`).
- Source proof this actually changes what gets declared on the broker: `RabbitMqMessagePublishTopology<TMessage>` (constructor) reads `messageTopology.EntityName` and an `IMessageExchangeTypeSelector<T>` to build the `RabbitMqExchangeConfigurator` used at `Apply()` time —

  ```csharp
  // src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Topology/RabbitMqMessagePublishTopology.cs:29-37
  var exchangeName = messageTopology.EntityName;
  var exchangeType = exchangeTypeSelector.GetExchangeType(exchangeName);
  ...
  _exchange = new RabbitMqExchangeConfigurator(exchangeName, exchangeType, durable, autoDelete);
  ```

  `Apply(IPublishEndpointBrokerTopologyBuilder builder)` (same file, lines 45-62) then does `builder.ExchangeDeclare(_exchange.ExchangeName, _exchange.ExchangeType, ...)` — this is the exact call that puts a topic exchange named `notification` on the broker instead of one named after `NotificationPlatform.Contracts.NotificationRequested`.
- Corroborated independently by community usage in [GitHub Discussion #5329 "Using topic exchanges and routing keys with RabbitMQ"](https://github.com/MassTransit/MassTransit/discussions/5329), where the producer side is configured with exactly this shape (`cfg.Message<T>(x => x.SetEntityName(...))` + `cfg.Publish<T>(x => x.ExchangeType = "topic")`).

Every `NotificationRequested` instance — regardless of which `Channel` it targets — publishes to the **same** `notification` topic exchange; the channel-specific routing happens entirely via the routing key (§2), not via separate exchanges. This matches `ChannelRouting.ExchangeName` in `src/Contracts/Channel.cs:25`, which is already `"notification"`.

---

## 2. Per-message routing key (`notification.email` vs `.sms` vs `.push`)

The API in 8.5.10 is **`UseRoutingKeyFormatter`**, defined generically (not RabbitMQ-specific) in `src/MassTransit/Configuration/RoutingKeyConventionExtensions.cs`:

```csharp
// lines 39-43 (delegate overload, the one you'll actually use)
public static void UseRoutingKeyFormatter<T>(this ISendTopologyConfigurator configurator, Func<SendContext<T>, string> formatter)
    where T : class
{
    configurator.GetMessageTopology<T>().UseRoutingKeyFormatter(new DelegateRoutingKeyFormatter<T>(formatter));
}
```

Crucially, this extension is defined on `ISendTopologyConfigurator` / `IMessageSendTopologyConfigurator<T>` — i.e. **Send topology, not Publish topology**. Checked directly against the RabbitMQ publish-topology configurator interface, which does *not* carry it:

```csharp
// src/Transports/MassTransit.RabbitMqTransport/Configuration/Topology/IRabbitMqMessagePublishTopologyConfigurator.cs
public interface IRabbitMqMessagePublishTopologyConfigurator<TMessage> :
    IMessagePublishTopologyConfigurator<TMessage>,
    IRabbitMqMessagePublishTopology<TMessage>,
    IRabbitMqMessagePublishTopologyConfigurator
    where TMessage : class
{ }
// no UseRoutingKeyFormatter anywhere in this hierarchy
```

So the routing key has to be configured through `cfg.Send<T>(...)`, even though the call site publishing the message is `IPublishEndpoint.Publish`, not `ISendEndpoint.Send`:

```csharp
cfg.Send<NotificationRequested>(s =>
    s.UseRoutingKeyFormatter(sendContext => ChannelRouting.RoutingKeyFor(sendContext.Message.Channel)));
```

This works for `Publish()` because of how the filter is wired: `UseRoutingKeyFormatter` ultimately calls `SetFormatter` on `RoutingKeyMessageSendTopologyConvention<T>` (`src/MassTransit/Topology/Configuration/RoutingKeyMessageSendTopologyConvention.cs`), which installs a `SetRoutingKeyMessageSendTopology<T>` (`src/MassTransit/Topology/Configuration/SetRoutingKeyMessageSendTopology.cs:22-25`) that adds a `SetRoutingKeyFilter<T>` onto the pipe for `SendContext<TMessage>` — not `PublishContext<TMessage>` specifically. Since `PublishContext<T> : SendContext<T>` and RabbitMQ's actual publish mechanics resolve the exchange address and dispatch through the same context/filter machinery a `Send()` to that address would use (`RabbitMqMessageSendContext<T>` implements `RabbitMqSendContext<T> : SendContext<T>, RoutingKeySendContext` regardless of whether the call originated from `Publish` or `Send`), the filter fires either way:

```csharp
// src/MassTransit/Middleware/SetRoutingKeyFilter.cs:18-26
public Task Send(SendContext<TMessage> context, IPipe<SendContext<TMessage>> next)
{
    var routingKey = _routingKeyFormatter.FormatRoutingKey(context);
    if (context.TryGetPayload(out RoutingKeySendContext routingKeySendContext))
        routingKeySendContext.RoutingKey = routingKey;
    return next.Send(context);
}
```

`RoutingKey` itself is declared on the transport-agnostic `RoutingKeySendContext` (`src/MassTransit.Abstractions/Contexts/RoutingKeySendContext.cs`), and `RabbitMqSendContext : SendContext, RoutingKeySendContext` (`src/Transports/MassTransit.RabbitMqTransport/RabbitMqSendContext.cs:6-9`) is what carries it through to the actual `BasicPublish` call, defaulting to `""` if never set.

Concretely, for this project:

```csharp
cfg.Send<NotificationRequested>(s => s.UseRoutingKeyFormatter(
    sendContext => ChannelRouting.RoutingKeyFor(sendContext.Message.Channel)));
```

reusing `ChannelRouting.RoutingKeyFor` already defined in `src/Contracts/Channel.cs:32-33` (`$"{ExchangeName}.{channel...ToLowerInvariant()}"`) keeps the routing-key convention in exactly one place, shared by both the publisher and every consumer's binding key (§3).

Corroborated by the same GitHub discussion's producer snippet (`cfg.Send<BoughtMilk>(x => { x.UseRoutingKeyFormatter(ctx => ctx.Message.EventType); });`) and by a WebSearch-surfaced summary of the current `masstransit.io`/massient.com docs describing `UseRoutingKeyFormatter` with the identical delegate shape (`x.UseRoutingKeyFormatter(context => context.Message.CustomerType)`).

---

## 3. Receive endpoint binding, and suppressing the default type-exchange bind

### Binding a queue to the topic exchange with its own binding key

`IRabbitMqReceiveEndpointConfigurator.Bind` (`src/Transports/MassTransit.RabbitMqTransport/Configuration/IRabbitMqReceiveEndpointConfigurator.cs:29`):

```csharp
void Bind(string exchangeName, Action<IRabbitMqExchangeToExchangeBindingConfigurator> callback = null);
```

and the configurator surface it exposes, `IRabbitMqExchangeBindingConfigurator` (`.../Configuration/IRabbitMqExchangeBindingConfigurator.cs`):

```csharp
public interface IRabbitMqExchangeBindingConfigurator : IRabbitMqExchangeConfigurator
{
    string RoutingKey { set; }          // the binding key
    void SetBindingArgument(string key, object value);
}
// IRabbitMqExchangeConfigurator additionally carries: ExchangeType, Durable, AutoDelete
```

Applied for the Email Channel Service:

```csharp
cfg.ReceiveEndpoint("email-notification-requested", e =>
{
    e.ConfigureConsumer<NotificationRequestedConsumer>(context);

    e.Bind(ChannelRouting.ExchangeName, x =>            // "notification"
    {
        x.ExchangeType = ExchangeType.Topic;
        x.RoutingKey = ChannelRouting.RoutingKeyFor(Channel.Email); // "notification.email"
    });
});
```

At topology-build time this flows through `RabbitMqReceiveEndpointBuilder.BuildTopology`, whose final step, `_configuration.Topology.Consume.Apply(topologyBuilder)` (`src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Configuration/RabbitMqReceiveEndpointBuilder.cs:92`), applies exactly this `Bind()` call as an `ExchangeToExchangeBindingConsumeTopologySpecification` — i.e. it declares `notification` as a topic exchange (if not already declared) and issues `queue.BindExchange("notification", "notification.email")` on the endpoint's own queue.

### Stopping the default type-exchange auto-bind

The failure mode ADR-0003 flags — every Channel Service still gets bound to (and therefore still receives) the default `NotificationPlatform.Contracts.NotificationRequested` fanout exchange **in addition to** the explicit topic binding — is governed by a *different* flag than `Bind()`, and it lives on the endpoint, not the message:

```csharp
cfg.ReceiveEndpoint("email-notification-requested", e =>
{
    e.ConfigureConsumeTopology = false;   // <-- suppresses the per-message-type auto-bind
    e.ConfigureConsumer<NotificationRequestedConsumer>(context);
    e.Bind(ChannelRouting.ExchangeName, x => { x.ExchangeType = ExchangeType.Topic; x.RoutingKey = "notification.email"; });
});
```

Proof this is the exact switch, straight from `RabbitMqReceiveEndpointBuilder.ConnectConsumePipe<T>` (`.../RabbitMqReceiveEndpointBuilder.cs:25-35`):

```csharp
public override ConnectHandle ConnectConsumePipe<T>(IPipe<ConsumeContext<T>> pipe, ConnectPipeOptions options)
{
    if (_configuration.ConfigureConsumeTopology && options.HasFlag(ConnectPipeOptions.ConfigureConsumeTopology))
    {
        IRabbitMqMessageConsumeTopologyConfigurator<T> topology = _configuration.Topology.Consume.GetMessageTopology<T>();
        if (topology.ConfigureConsumeTopology)
            topology.Bind();   // <-- this is what binds the queue to the default per-type exchange
    }
    return base.ConnectConsumePipe(pipe, options);
}
```

`ConnectConsumePipe<T>` runs once per consumed message type when `x.AddConsumer<NotificationRequestedConsumer>()` / `e.ConfigureConsumer<...>()` wires up the endpoint. With `ConfigureConsumeTopology` left at its default (`true` — set in `EndpointSettings` per `src/MassTransit.Abstractions/Configuration/DependencyInjection/Configuration/EndpointSettings.cs:15`), `topology.Bind()` runs and creates exactly the broadcast-to-everyone binding ADR-0003 rejects. Setting `e.ConfigureConsumeTopology = false` on the receive endpoint short-circuits the `if` and that call never happens — the endpoint's queue then has **only** the bindings you declared explicitly via `Bind(...)`.

This must be set **per receive endpoint** (each Channel Service's own `ReceiveEndpoint(...)` block), not once globally, since each endpoint independently decides what it's bound to.

Sources: `masstransit.massient.com/documentation/transports/rabbitmq` (documents `Bind()` overloads and `ConfigureConsumeTopology = false` for "consume from a pre-existing/externally-managed queue" scenarios, matching the source behavior above); GitHub source as cited inline (v8.5.10 tag, commit `62ab339afa3bac2e9b3fe1769d0d35d7e44778e9`, 2026-06-04).

---

## 4. Does the EF Core outbox preserve the custom routing key? — **Yes, confirmed by source, with a full call-chain trace**

This is the make-or-break question for ADR-0003: if `UseBusOutbox()` staged messages lose their routing key on delivery, every Channel Service would receive every notification regardless of channel targeting, silently defeating the whole design — and worse, silently, since nothing would throw.

**Short answer: it holds.** The routing key round-trips through the outbox intact. Evidence below is exclusively from the pinned `v8.5.10` GitHub source (no docs page states this explicitly — the docs are silent on outbox+custom-topology interaction, so this had to be established by reading the outbox implementation directly, as the task anticipated).

### The mechanism: routing key is a "send property," not a header, and outbox rows carry both

RabbitMQ's `SendContext` implementation stores `RoutingKey` (and `Exchange`) as **transport send properties** — a side channel distinct from message headers, defined by the `TransportSendContext` interface:

```csharp
// src/MassTransit/Contexts/Context/TransportSendContext.cs
public interface TransportSendContext : PublishContext
{
    void WritePropertiesTo(IDictionary<string, object> properties);
    void ReadPropertiesFrom(IReadOnlyDictionary<string, object> properties);
}
```

`RabbitMqMessageSendContext<T>` (`src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/RabbitMqMessageSendContext.cs:31-64`) implements both halves:

```csharp
public override void ReadPropertiesFrom(IReadOnlyDictionary<string, object> properties)
{
    base.ReadPropertiesFrom(properties);
    Exchange = ReadString(properties, RabbitMqTransportPropertyNames.Exchange, Exchange);
    RoutingKey = ReadString(properties, RabbitMqTransportPropertyNames.RoutingKey, "");
    ...
}

public override void WritePropertiesTo(IDictionary<string, object> properties)
{
    base.WritePropertiesTo(properties);
    properties[RabbitMqTransportPropertyNames.Exchange] = Exchange;
    if (!string.IsNullOrWhiteSpace(RoutingKey))
        properties[RabbitMqTransportPropertyNames.RoutingKey] = RoutingKey;
    ...
}
```

### Step 1 — capture, at the moment the outbox stages the message

When `UseBusOutbox()` intercepts a `Publish()`/`Send()` call inside a DB transaction, it writes an `OutboxMessage` EF row via `EntityFrameworkOutboxExtensions.AddSend<T>`:

```csharp
// src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/EntityFrameworkOutboxExtensions.cs:52-58
if (context is TransportSendContext<T> transportSendContext)
{
    var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    transportSendContext.WritePropertiesTo(properties);
    outboxMessage.Properties = deserializer.SerializeDictionary(properties);
}
```

At this point `context` is the live `RabbitMqMessageSendContext<NotificationRequested>` that `SetRoutingKeyFilter` (§2) already ran against — so `RoutingKey` is already `"notification.email"` (or `.sms`/`.push`) by the time `WritePropertiesTo` serializes it into `OutboxMessage.Properties`, a plain string column on the EF outbox table. The `OutboxMessage.Properties` column is documented in the model itself: `"Transport-specific message properties (routing key, partition key, sessionId, etc.)"` (`.../OutboxMessage.cs:26-28`). `OutboxMessage.DestinationAddress` (line 59) also gets `context.DestinationAddress` — the exchange URI, e.g. `exchange:notification?type=topic` (`RabbitMqEndpointAddress` embeds exchange type in the query string, `RabbitMqEndpointAddress.cs:261-262`, `GetQueryStringOptions`) — so the *exchange kind* is preserved too, not just the routing key.

### Step 2 — replay, when the background delivery service ships the row

`BusOutboxDeliveryService<TDbContext>` (the hosted service backing `UseBusOutbox()`, EF Core provider) polls pending rows and, for each one:

```csharp
// src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/BusOutboxDeliveryService.cs:258-267
var pipe = new OutboxMessageSendPipe(message, message.DestinationAddress);
var endpoint = await _busControl.GetSendEndpoint(message.DestinationAddress).ConfigureAwait(false);
...
await endpoint.Send(new SerializedMessageBody(), pipe, token.Token).ConfigureAwait(false);
```

`GetSendEndpoint(message.DestinationAddress)` resolves a send endpoint pointed at the **same exchange URI** captured in step 1 (topic type included), and `OutboxMessageSendPipe` is the piece that rehydrates the brand-new `SendContext` it's handed:

```csharp
// src/MassTransit/Middleware/OutboxMessageSendPipe.cs:63-64
if (_message.Properties.Count > 0 && context is TransportSendContext transportSendContext)
    transportSendContext.ReadPropertiesFrom(_message.Properties);
```

For a RabbitMQ destination, `context` here is a fresh `RabbitMqMessageSendContext<T>`, so this call lands directly on the `ReadPropertiesFrom` override quoted above — `RoutingKey` is set back to `"notification.email"` straight from the stored properties, **not recomputed**. The formatter from §2 (`UseRoutingKeyFormatter`) never runs a second time and doesn't need to; the already-formatted value is what got persisted and what comes back out. (The identical pattern — `DeliverOutboxMessages` → `OutboxMessageSendPipe` → `ReadPropertiesFrom` — also exists in the **consumer-side** outbox path, `src/MassTransit/Middleware/OutboxMessagePipe.cs:106-118`, used when a consumer publishes via the outbox while handling another message, so this holds for both the "publish from a web request" pattern this project uses and the in-consumer outbox pattern.)

### Conclusion

The EF Core transactional outbox in 8.5.10 is transport-property-aware by design — it doesn't serialize "just the message body," it round-trips the full `SendContext` surface (headers *and* transport send properties, of which RabbitMQ's routing key is one) through the `OutboxMessage.Headers`/`OutboxMessage.Properties` columns, and restores all of it before the deferred send. **ADR-0003's routing survives the outbox unchanged.** This was not stated anywhere in the docs pages fetched (`masstransit.massient.com/configuration/middleware/outbox` describes the outbox's *transactional* guarantees but says nothing about topology/routing-key fidelity) — it required reading `EntityFrameworkOutboxExtensions.AddSend`, `OutboxMessageSendPipe`, and `RabbitMqMessageSendContext` directly, which is exactly the situation the task called out as the one to resolve from source rather than docs.

---

## 5. Do `_error`/`_skipped` queues still get created automatically under custom topology?

**Yes — unconditionally, and by construction they cannot be affected by the topology changes in §1-§3.**

`RabbitMqReceiveEndpointBuilder.CreateReceiveEndpointContext()` (`.../RabbitMqReceiveEndpointBuilder.cs:37-51`) always provisions both:

```csharp
public RabbitMqReceiveEndpointContext CreateReceiveEndpointContext()
{
    var brokerTopology = BuildTopology(_configuration.Settings);
    var deadLetterTransport = CreateDeadLetterTransport();   // "_skipped" queue
    var errorTransport = CreateErrorTransport();             // "_error" queue
    ...
}

IErrorTransport CreateErrorTransport()
{
    var errorSettings = _configuration.Topology.Send.GetErrorSettings(_configuration.Settings);
    ...
    return new RabbitMqErrorTransport(errorSettings.ExchangeName, filter);
}
```

Two structural reasons this is independent of everything in §1-§3:

1. **No conditional on `ConfigureConsumeTopology` anywhere in this method** — unlike `ConnectConsumePipe<T>` (§3), which explicitly checks the flag before calling `topology.Bind()`, `CreateReceiveEndpointContext()` has no such guard. It runs the same way whether the endpoint uses default type-exchange topology or a fully custom `Bind(...)`/`ConfigureConsumeTopology = false` setup.
2. **It's driven by Send topology, not Consume topology.** `errorSettings = _configuration.Topology.Send.GetErrorSettings(...)` pulls from `RabbitMqSendTopology.GetErrorSettings` (`src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Topology/RabbitMqSendTopology.cs:36-43`), which names the error queue via `DefaultErrorQueueNameFormatter`:

   ```csharp
   // src/MassTransit.Abstractions/Topology/Topology/DefaultErrorQueueNameFormatter.cs
   const string ErrorQueueSuffix = "_error";
   public string FormatErrorQueueName(string queueName) => queueName + ErrorQueueSuffix;
   ```

   and the skipped/dead-letter queue via `DefaultDeadLetterQueueNameFormatter`:

   ```csharp
   // src/MassTransit.Abstractions/Topology/Topology/DefaultDeadLetterQueueNameFormatter.cs
   const string DeadLetterQueueSuffix = "_skipped";
   public string FormatDeadLetterQueueName(string queueName) => queueName + DeadLetterQueueSuffix;
   ```

   Both are keyed off the **receive endpoint's own queue name** (`settings.ExchangeName`, which for a receive endpoint defaults to the queue name), not off anything to do with the published message type's exchange. Overriding the publish/consume topology for `NotificationRequested` has no code path that touches `Topology.Send.GetErrorSettings`/`GetDeadLetterSettings` at all — they're entirely separate object graphs.

Practical implication for the Channel Services: `email-notification-requested_error` and `email-notification-requested_skipped` (etc., per endpoint name chosen) still appear automatically next to each Channel Service's main queue, with zero additional configuration, regardless of the `Bind()`/`ConfigureConsumeTopology` changes made to route `notification.email` correctly. This matches — and extends with source-level certainty — what the prior outbox/retry/DLQ research already established generally (`docs/research/masstransit-outbox-retry-dlq.md` §3): "This is automatic — no explicit topology configuration is required to get an error queue."

---

## Putting it together: full config for one Channel Service (Email)

```csharp
// Notification API — publisher side
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.Message<NotificationRequested>(m => m.SetEntityName(ChannelRouting.ExchangeName)); // "notification"
        cfg.Publish<NotificationRequested>(p => p.ExchangeType = ExchangeType.Topic);
        cfg.Send<NotificationRequested>(s => s.UseRoutingKeyFormatter(
            sendContext => ChannelRouting.RoutingKeyFor(sendContext.Message.Channel)));

        cfg.ConfigureEndpoints(context);
    });
});
```

```csharp
// Email Channel Service — consumer side
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<NotificationRequestedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        cfg.ReceiveEndpoint("email-notification-requested", e =>
        {
            e.ConfigureConsumeTopology = false;    // no auto-bind to the default type exchange
            e.ConfigureConsumer<NotificationRequestedConsumer>(context);

            e.Bind(ChannelRouting.ExchangeName, x =>   // "notification"
            {
                x.ExchangeType = ExchangeType.Topic;
                x.RoutingKey = ChannelRouting.RoutingKeyFor(Channel.Email); // "notification.email"
            });
        });
    });
});
```

SMS and Push Channel Services mirror this with their own endpoint name and `Channel.Sms`/`Channel.Push`. `_error`/`_skipped` queues for each appear with no further config (§5), and if `UseBusOutbox()` is layered onto the Notification API's `AddMassTransit` (per the prior outbox research), the routing key set above survives deferred delivery unchanged (§4).

---

## Sources consulted

- [MassTransit GitHub repo, tag `v8.5.10`](https://github.com/MassTransit/MassTransit/tree/v8.5.10) (commit `62ab339afa3bac2e9b3fe1769d0d35d7e44778e9`, 2026-06-04) — cloned locally and read directly; all file:line citations above are against this exact tag. Key files:
  - `src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Topology/RabbitMqMessagePublishTopology.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/Configuration/Topology/IRabbitMqMessagePublishTopologyConfigurator.cs`
  - `src/MassTransit/Configuration/RoutingKeyConventionExtensions.cs`
  - `src/MassTransit/Middleware/SetRoutingKeyFilter.cs`
  - `src/MassTransit/Topology/Configuration/SetRoutingKeyMessageSendTopology.cs`
  - `src/MassTransit.Abstractions/Contexts/RoutingKeySendContext.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/RabbitMqSendContext.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/RabbitMqMessageSendContext.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/Configuration/IRabbitMqReceiveEndpointConfigurator.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/Configuration/IRabbitMqExchangeBindingConfigurator.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Configuration/RabbitMqReceiveEndpointBuilder.cs`
  - `src/MassTransit/Contexts/Context/TransportSendContext.cs`
  - `src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/EntityFrameworkOutboxExtensions.cs`
  - `src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/OutboxMessage.cs`
  - `src/Persistence/MassTransit.EntityFrameworkCoreIntegration/EntityFrameworkCoreIntegration/BusOutboxDeliveryService.cs`
  - `src/MassTransit/Middleware/OutboxMessageSendPipe.cs`
  - `src/MassTransit/Middleware/OutboxMessagePipe.cs`
  - `src/Transports/MassTransit.RabbitMqTransport/RabbitMqTransport/Topology/RabbitMqSendTopology.cs`
  - `src/MassTransit.Abstractions/Topology/Topology/DefaultErrorQueueNameFormatter.cs`
  - `src/MassTransit.Abstractions/Topology/Topology/DefaultDeadLetterQueueNameFormatter.cs`
  - `src/MassTransit.Abstractions/Configuration/DependencyInjection/Configuration/EndpointSettings.cs`
- [MassTransit docs — Topology configuration](https://masstransit.massient.com/documentation/configuration/topology) (redirect target of `masstransit.io/documentation/configuration/topology`; see the domain-trust caveat recorded in `docs/research/masstransit-outbox-retry-dlq.md`)
- [MassTransit docs — RabbitMQ transport](https://masstransit.massient.com/documentation/transports/rabbitmq) (`Bind()`, `ConfigureConsumeTopology`, `_error` queue behavior)
- [GitHub Discussion #5329 — "Using topic exchanges and routing keys with RabbitMQ"](https://github.com/MassTransit/MassTransit/discussions/5329) (community producer/consumer config pattern; corroborates `SetEntityName`/`Publish<T>.ExchangeType`/`Send<T>.UseRoutingKeyFormatter`/`ReceiveEndpoint.Bind` split)
- [GitHub Issue #1665 — "RabbitMQ Topic Exchange Binding"](https://github.com/MassTransit/MassTransit/issues/1665) (surfaced via search; general topic-binding context, not separately quoted above)
- Prior project research: `docs/research/masstransit-outbox-retry-dlq.md` (branch `research/masstransit-outbox-retry-dlq`) — referenced for the `masstransit.io` → `masstransit.massient.com` redirect caveat and for the general `_error`/`_skipped` behavior this document extends with topology-interaction specifics.
- This repo: `docs/adr/0003-caller-selected-channels-via-topic-exchange.md`, `src/Contracts/Channel.cs`, `src/Contracts/NotificationRequested.cs`, `Directory.Packages.props` (confirms the 8.5.10 pin this research targets).
