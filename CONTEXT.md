# Notification Platform

A reference microservices system for learning production-grade async messaging and caching in .NET 10. Users request notifications through a single entry point; independent channel services deliver them asynchronously via a message broker.
_Avoid_: Alerting platform, notification system

## Language

**Notification**:
The business-level request to tell a user something (e.g. "your order shipped"), independent of how it's delivered. Recorded by the Notification API when accepted, and realized per Channel by a Delivery Attempt. It carries no delivery outcome of its own — a Notification is only ever *accepted*, never "sent" or "failed".
_Avoid_: Message, event, alert

**Notification API**:
The single entry point (.NET Minimal API) that accepts notification requests, authenticates callers via a self-issued JWT, and publishes a `NotificationRequested` event. It never delivers anything itself.
_Avoid_: Web app, Gateway, Web API (generic)

**Channel**:
One delivery medium for a notification — Email, SMS, or Push. Each Channel is implemented by exactly one Channel Service.
_Avoid_: Service, provider

**Targeted Channel**:
A Channel the caller named on a Notification. A Notification targets at least one and at most every Channel; only its Targeted Channels ever produce a Delivery Attempt for it. Naming no Channel, or one that doesn't exist, is a rejected request rather than a Notification.
_Avoid_: Subscription, preference, recipient list (nothing in this system stores a standing choice — targeting is per-Notification)

**Channel Service**:
An independently deployed consumer (Email Service, SMS Service, Push Service) that consumes `NotificationRequested`, simulates delivery over its Channel, and records the result as a Delivery Attempt in its own database.
_Avoid_: Worker, handler, consumer (when a specific service is meant, name it)

**Event**:
A fact published to the broker that something happened in the domain, e.g. `NotificationRequested`. Named in the past/requested tense and carries only the data a Channel Service needs to act. Scoped to one Targeted Channel, not to a Notification: a Notification targeting two Channels produces two Events.
_Avoid_: Message (see Message), notification

**Message**:
The transport-level envelope MassTransit moves over RabbitMQ — headers, serialization, routing. Used only when talking about broker mechanics (retries, DLQ, poison messages), never as a synonym for Event or Notification.
_Avoid_: Event, notification

**Delivery Attempt**:
One Channel Service's persisted record of trying to deliver a Notification: its outcome (Delivered, Failed), timestamp, the Idempotency Key used to detect duplicates, and the identity of the Notification it realizes. This is what "notification status" means in this system — there is no separate status concept.
_Avoid_: Notification status, delivery log

**Idempotency Key**:
The deterministic identifier (derived from the event's message ID) a Channel Service uses to recognize it has already processed a given `NotificationRequested` event, so a redelivered Message produces no duplicate Delivery Attempt.

**Outbox**:
The Notification API's local table that stages `NotificationRequested` events in the same database transaction as the request that caused them, so publishing to RabbitMQ is never lost even if the broker is briefly unavailable. Specifically the transactional outbox pattern, applied only at the point where a Notification is first accepted.
_Avoid_: Queue, staging table

**Dead Letter**:
A Message a Channel Service failed to process after its retry policy is exhausted, moved to that Channel's dead-letter queue for later inspection rather than retried forever.
_Avoid_: Poison message (use only when describing the retry-then-park mechanism itself, not the parked message)

**Cache Entry**:
Data a service reads from Redis instead of Postgres to avoid repeat lookups (cache-aside), scoped and owned by the service that writes it. Distinct from the Redis Streams demo below — Cache Entries never cross service boundaries.
_Avoid_: Session, state (this system has neither)

**Comparison Stream**:
The standalone Redis Streams pipeline built solely to contrast a lightweight at-most/at-least-once log with RabbitMQ's broker semantics. Not part of the Notification flow — no Channel Service depends on it.
_Avoid_: Redis queue, secondary broker
