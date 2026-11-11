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
| `{Aggregate} {Command}` (`Person RegisterPerson`) | Internal | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.command`, `rosetta.events` (stored, when any), `rosetta.outcome` (`handled`, `rejected`, `failed`), `rosetta.domain_error`, `rosetta.attempts` (after a conflict retry) | Error with the `DomainException.Name`, or the exception |
| `{Projection} {Event}` (`Person PersonRegistered`) | Consumer | `rosetta.projection`, `rosetta.stream`, `rosetta.event`, `rosetta.checkpoint`, `rosetta.outcome` (`skipped`), `rosetta.attempts` (after a conflict retry) | Error when a Handler fails |
| `{Aggregate} {EventType} publish` (`Person PersonRegistered publish`) | Producer | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.event`, `rosetta.outbox.event.id`, `rosetta.event.case` (the union case, once decoded) | Error with the exception when `PublishAsync` throws, or when the event can't be decoded |

- `{Command}` is the command's record type name; `{Event}` and `{EventType}` are the event's stored name.
- A projection span's parent is the `traceparent` stored in the event's metadata, so a trace runs from the request,
  through the command, to every Projection, across services.
- A conflict retried at once ([commands](../concepts/aggregates.md#conflicts),
  [events](../concepts/projections.md#conflicts-are-retried-at-once)) adds the span event `rosetta.conflict.retry` and
  sets `rosetta.attempts` to the attempt that follows (2, 3, …).
- The Outbox publisher's span continues the trace of the command that recorded the event (its `TraceParent`), so
  spans of your broker client inside `PublishAsync` join it.

## Metrics

| Instrument | Type | Unit | Tags |
|---|---|---|---|
| `rosetta.commands` | Counter | `{command}` | `rosetta.aggregate`, `rosetta.command`, `rosetta.outcome` |
| `rosetta.command.duration` | Histogram | `s` | `rosetta.aggregate`, `rosetta.command` |
| `rosetta.events.stored` | Counter | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.events` | Counter | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.published` | Counter | `{event}` | `rosetta.aggregate`, `rosetta.outcome` (`published`; `failed`: `PublishAsync` threw; `undecodable`: it couldn't be read back as its events union) |
| `rosetta.conflicts.retried` | Counter | `{retry}` | `rosetta.aggregate` (a command) or `rosetta.projection` (an event) |
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
| `DROPPED` (a failure, or a conflict that outlasted its immediate retries; resubscribing) | Error |
| `FAULTED` (5 failures in a row) | Critical, once |
| `STOPPED` (an unknown event, or a permanent startup error) | Critical |
| `CANCELED` (the application stopped) | Information |
| `Custom projection {Name} CREATED` / `UPDATED: {reason}` / `RELINKED: …` / `DELETED, with {Stream}: …` / `is up to date` | Information |
| `Custom projection {Name} UPDATED without comparing, …` | Warning |
| `The custom projections this application created before couldn't be checked for deletion; …` | Warning |
| `Projection {Name} is REBUILT: {reason}. …` (its `cp-` stream was relinked or replaced) | Warning |

The Outbox logs a Warning once when RavenDB's licence refuses its Document Expiration frequency. Its publisher logs:

| Message | Level |
|---|---|
| `The outbox {Collection} is published by this instance ({Owner}).` / `… by another instance: this one ({Owner}) waits to take over.` | Information |
| `The outbox {Collection} FAILED to publish. Retrying in {Delay} …` | Error |
| `The outbox {Collection} FAILED to publish {Failures} times in a row. …` (the 5th) | Critical |
| `The outbox {Collection} cannot decode {EventType} as {EventsUnionClrType}: {Reason}. Retrying in {Delay} …` | Error |
| `The outbox {Collection} cannot decode {EventType} as {EventsUnionClrType}: {Reason} ({Failures} times in a row). …` (the 5th) | Critical |
| `The outbox {Collection} RECOVERED: it publishes again.` | Information |

The KurrentDB Aggregate repository logs a Warning when a [snapshot](../concepts/aggregates.md#snapshots) couldn't be
written.

## Health check

`rosetta-projections` (`AddRosettaProjections`): Healthy when every Projection runs, Degraded while one starts or
retries, the failure status (Unhealthy) when one is Faulted or Stopped. Its data has, per Projection: `state`,
`stream`, `checkpoint`, `consecutiveFailures`, `lastError`, `since`.
