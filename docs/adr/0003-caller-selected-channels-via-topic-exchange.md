# Caller-selected Channels, routed by a topic exchange

A caller names the Channels a Notification should reach, and the Notification API publishes one `NotificationRequested` Event per Targeted Channel with routing key `notification.<channel>`. Each Channel Service binds its queue to the matching key, so the broker decides who receives what and a service is never woken for a Notification it wasn't targeted with.

## Considered options

- **Broadcast, filtered by consumers** — publish one Event carrying the Channel list, let all three Channel Services receive it and drop what isn't theirs. Rejected: this is MassTransit's free default, but the broker discriminates nothing, two-thirds of the wake-ups are wasted, and caller-selected targeting becomes cosmetic. The point of this project is to learn broker routing, and this option contains none.
- **A message type per Channel** (`EmailNotificationRequested`, …) — MassTransit's type-based exchanges would route these with no custom topology at all, and polymorphic publish would keep the contracts sharing an interface. Genuinely the idiomatic option, and rejected only because type-based routing is MassTransit's abstraction rather than RabbitMQ's: it would leave routing keys and bindings unlearned.

## Consequences

- MassTransit's type-based publish topology has to be overridden (entity name, exchange type, routing-key formatter) and each receive endpoint binds explicitly. This is steering around the framework's default, so expect the configuration to look unusual next to typical MassTransit samples — that is deliberate, not a mistake to tidy up.
- The topic exchange is doing direct-style routing until something binds a wildcard (`notification.#`). Nothing does today; an audit or archival consumer is where the topic shape would start paying for itself.
- A Notification becomes several Events, each with its own message ID. Idempotency Keys stay per-Event, so each Channel Service dedupes its own redeliveries independently, and the Notification's identity travels on every Event and onto every Delivery Attempt as the correlation key tying the fan-out back together.
- A Notification targeting no Channel, or an unknown one, is rejected by the Notification API with a 400 rather than accepted into the outbox — a request that can produce no Delivery Attempt is a caller error.
