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

A command's trace context (W3C `traceparent`/`tracestate`) is stored in the metadata of the events it produces, and the projections handling those events continue it. So one trace runs from the request, through the command, to every read model it updates, even across services. A rejected command (`DomainException`) or a failing projection marks its span as an error. Spans of your own code and of instrumented clients nest inside Rosetta's.

## Metrics

| Instrument | Unit | Tags |
|---|---|---|
| `rosetta.commands` | `{command}` | `rosetta.aggregate`, `rosetta.command`, `rosetta.outcome` |
| `rosetta.command.duration` | `s` | `rosetta.aggregate`, `rosetta.command` |
| `rosetta.events.stored` | `{event}` | `rosetta.aggregate` |
| `rosetta.outbox.events` | `{event}` | `rosetta.aggregate` |
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
