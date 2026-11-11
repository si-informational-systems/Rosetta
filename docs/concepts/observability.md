---
title: Observability
description: OpenTelemetry traces and metrics for commands and projections, and the projections health check.
---

# Observability

Rosetta traces and measures commands and projections with .NET's own `ActivitySource` and `Meter`, both named **`SI.Rosetta`**. It records nothing, and costs next to nothing, until your application listens. To export them with OpenTelemetry (packages `OpenTelemetry.Extensions.Hosting` and an exporter, e.g. `OpenTelemetry.Exporter.OpenTelemetryProtocol`):

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource("SI.Rosetta").AddOtlpExporter())
    .WithMetrics(metrics => metrics.AddMeter("SI.Rosetta").AddOtlpExporter());
```

```fsharp
builder.Services
    .AddOpenTelemetry()
    .WithTracing(fun tracing -> tracing.AddSource("SI.Rosetta").AddOtlpExporter() |> ignore)
    .WithMetrics(fun metrics -> metrics.AddMeter("SI.Rosetta").AddOtlpExporter() |> ignore) |> ignore
```

## Traces

| Span | Kind | Tags |
|---|---|---|
| `{Aggregate} {Command}`, e.g. `Person RegisterPerson` | Internal | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.command`, `rosetta.events` (stored), `rosetta.outcome` (`handled`, `rejected`, `failed`), `rosetta.domain_error` |
| `{Projection} {Event}`, e.g. `Person PersonRegistered` | Consumer | `rosetta.projection`, `rosetta.stream`, `rosetta.event`, `rosetta.checkpoint`, `rosetta.outcome` (`skipped`) |
| `{Aggregate} {EventType} publish`, e.g. `Person PersonRegistered publish` | Producer | `rosetta.aggregate`, `rosetta.aggregate.id`, `rosetta.event`, `rosetta.outbox.event.id`, `rosetta.event.case` (the union case, once decoded) |

A command's trace (its trace and span ids) is stored in the metadata of the events it produces (KurrentDB.Client writes `$traceId` and `$spanId` on every append made inside a trace), and the projections handling those events continue it, as does the [Outbox publisher](outbox.md#publishing-the-events) for its PublishedEvents. So one trace runs from the request, through the command, to every read model it updates, even across services. A rejected command (`DomainException`) or a failing projection marks its span as an error. A conflict retried at once adds the span event `rosetta.conflict.retry` and the tag `rosetta.attempts`. Spans of your own code and of instrumented clients nest inside Rosetta's.

## Metrics

| Instrument | Unit | Tags |
|---|---|---|
| `rosetta.commands` | `{command}` | `rosetta.aggregate`, `rosetta.command`, `rosetta.outcome` |
| `rosetta.command.duration` | `s` | `rosetta.aggregate`, `rosetta.command` |
| `rosetta.events.stored` | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.events` | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.published` | `{event}` | `rosetta.aggregate`, `rosetta.outcome` (`published`, `failed`, `undecodable`) |
| `rosetta.conflicts.retried` | `{retry}` | `rosetta.aggregate` (a command) or `rosetta.projection` (an event): conflicts retried at once |
| `rosetta.projection.events` | `{event}` | `rosetta.projection`, `rosetta.outcome` (`handled`, `failed`, `skipped`) |
| `rosetta.projection.event.duration` | `s` | `rosetta.projection` |
| `rosetta.projection.resubscriptions` | `{resubscription}` | `rosetta.projection` |
| `rosetta.projection.faults` | `{fault}` | `rosetta.projection`, `rosetta.state` (`faulted`, `stopped`) |
| `rosetta.projections.unhealthy` (gauge) | `{projection}` | `rosetta.projection`, `rosetta.state`: 1 per Faulted or Stopped Projection, 0 for the others |

## Logs

Logs still go through `ILogger`, configured by your application as before. With OpenTelemetry logging (`builder.Logging.AddOpenTelemetry(…)`), every entry written during a command or a projection also carries its trace and span ids.

## Health

Projections report their state through a health check and a gauge: see
[Projections at runtime](projections.md#health-and-alerting). All instruments are listed in the
[Telemetry reference](../reference/telemetry.md).
