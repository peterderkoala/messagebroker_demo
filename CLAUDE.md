# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

**Charted, not yet built.** `src/` is still empty — there is no application code, and therefore no build, lint, or test tooling. What exists is planning and decision documentation:

- `CONTEXT.md` — the domain glossary. Read it before naming anything; Message, Event, and Notification are deliberately distinguished.
- `docs/adr/` — architecture decisions already made (RabbitMQ + MassTransit; database-per-service on a shared Postgres container).
- GitHub issue #1 — the `/wayfinder` map: destination, decisions so far, and the ticket sequence. It is the source of truth for scope and what to work on next.

The destination is a docker-compose stack: a Notification API plus Email/SMS/Push Channel Services over RabbitMQ, with Redis, Postgres, and the Aspire Dashboard. Work happens on `dev`; `main` stays deployable.

Re-run `/init` once real code lands, to replace this section with actual commands and architecture notes.

## Constraints worth knowing upfront

- Pin MassTransit to **8.5.10** — 9.x is commercially licensed (ADR-0001).
- Automated tests are out of scope; verification is manual.
- docker-compose is the deployment ceiling — no Kubernetes or cloud work.
- Channel delivery is simulated (logged and persisted), not wired to real providers.
- Auth is a self-issued dev JWT, deliberately minimal — no external identity provider.

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (peterderkoala/messagebroker_demo), using the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default canonical labels (needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout (root `CONTEXT.md` + `docs/adr/`). See `docs/agents/domain.md`.
