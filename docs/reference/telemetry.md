---
title: Telemetry
description: Every span, metric and log Rosetta emits.
---

# Telemetry

Rosetta uses .NET's `ActivitySource` and `Meter`, both named **`SI.Rosetta`** (`RosettaTelemetry.Name`). Nothing is
recorded until something listens. See [Observability](../concepts/observability.md) to export them.

## Spans

| Name | Kind | Tags | Status |
|---|---|---|---|
| `{Aggregate} {Command}` (`Person RegisterPerson`) | Internal | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.command`, `rosetta.events` (stored, when any), `rosetta.outcome` (`handled`, `rejected`, `failed`), `rosetta.domain_error` | Error with the `DomainException.Name`, or the exception |
| `{Projection} {Event}` (`Person PersonRegistered`) | Consumer | `rosetta.projection`, `rosetta.stream`, `rosetta.event`, `rosetta.checkpoint`, `rosetta.outcome` (`skipped`) | Error when a Handler fails |

- `{Command}` is the command's record type name; `{Event}` is the event's stored name.
- A projection span's parent is the `traceparent` stored in the event's metadata, so a trace runs from the request,
  through the command, to every Projection, across services.

## Metrics

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `rosetta.commands` | Counter | `{command}` | `rosetta.aggregate`, `rosetta.command`, `rosetta.outcome` |
| `rosetta.command.duration` | Histogram | `s` | `rosetta.aggregate`, `rosetta.command` |
| `rosetta.events.stored` | Counter | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.events` | Counter | `{event}` | `rosetta.aggregate` |
| `rosetta.projection.events` | Counter | `{event}` | `rosetta.projection`, `rosetta.outcome` (`handled`, `failed`, `skipped`) |
| `rosetta.projection.event.duration` | Histogram | `s` | `rosetta.projection` |
| `rosetta.projection.resubscriptions` | Counter | `{resubscription}` | `rosetta.projection` |
| `rosetta.projection.faults` | Counter | `{fault}` | `rosetta.projection`, `rosetta.state` (`faulted`, `stopped`) |
| `rosetta.projections.unhealthy` | Observable gauge | `{projection}` | `rosetta.projection`, `rosetta.state`; 1 when Faulted or Stopped, else 0 |

No tag holds an id, so cardinality stays bounded by the number of Aggregates, commands and Projections.

## Logs

Through `ILogger`. The Projections log each state change, with the Projection's name and stream:

| Message | Level |
|---|---|
| `STARTED` (from checkpoint n) | Information |
| `RECOVERED` | Information |
| `SKIPPED` an unknown event (with `[<SkipUnknownEvents>]`) | Warning the first time per event type, then Debug |
| `DROPPED` (a failure; resubscribing) | Error |
| `FAULTED` (5 failures in a row) | Critical, once |
| `STOPPED` (an unknown event, or a permanent startup error) | Critical |
| `CANCELED` (the application stopped) | Information |
| `Custom projection {Name} CREATED` / `UPDATED: {reason}` / `is up to date` | Information |
| `Custom projection {Name} UPDATED without comparing, …` | Warning |

The Outbox logs a Warning once when RavenDB's licence refuses its Document Expiration frequency.

## Health check

`rosetta-projections` (`AddRosettaProjections`): Healthy when every Projection runs, Degraded while one starts or
retries, the failure status (Unhealthy) when one is Faulted or Stopped. Its data has, per Projection: `state`,
`stream`, `checkpoint`, `consecutiveFailures`, `lastError`, `since`.
