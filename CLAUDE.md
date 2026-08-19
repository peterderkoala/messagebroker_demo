# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

This repository is currently empty (`src/` and `docs/` exist but contain no files). There is no build, lint, or test tooling set up yet, and no architecture to document.

Once the project has real content, re-run `/init` (or ask Claude Code directly) to regenerate this file with actual commands and architecture notes.

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (peterderkoala/messagebroker_demo), using the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default canonical labels (needs-triage, needs-info, ready-for-agent, ready-for-human, wontfix). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout (root `CONTEXT.md` + `docs/adr/`). See `docs/agents/domain.md`.
