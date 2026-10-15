namespace SI.Rosetta.Common

open System.Collections.Generic
open System.Diagnostics
open System.Diagnostics.Metrics

/// Rosetta's traces and metrics, both named "SI.Rosetta". Nothing is recorded until the application listens, e.g. with
/// OpenTelemetry: services.AddOpenTelemetry().WithTracing(fun t -> t.AddSource "SI.Rosetta").WithMetrics(fun m -> m.AddMeter "SI.Rosetta")
module RosettaTelemetry =
    [<Literal>]
    let Name = "SI.Rosetta"

    let private Version = typeof<IAggregateEvents>.Assembly.GetName().Version.ToString()

    let ActivitySource = new ActivitySource(Name, Version)
    let Meter = new Meter(Name, Version)

    /// Commands handled, by aggregate, command and outcome (handled, rejected, failed)
    let Commands = Meter.CreateCounter<int64>("rosetta.commands", "{command}", "Commands handled")
    let CommandDuration = Meter.CreateHistogram<double>("rosetta.command.duration", "s", "How long commands took")
    /// Events aggregates produced and stored, by aggregate
    let EventsStored = Meter.CreateCounter<int64>("rosetta.events.stored", "{event}", "Events aggregates produced and stored")
    /// Events projections received, by projection and outcome (handled, failed, skipped)
    let ProjectionEvents = Meter.CreateCounter<int64>("rosetta.projection.events", "{event}", "Events projections received")
    let ProjectionEventDuration = Meter.CreateHistogram<double>("rosetta.projection.event.duration", "s", "How long projections took to handle an event")
    /// Times projection subscriptions dropped and resubscribed, by projection
    let Resubscriptions = Meter.CreateCounter<int64>("rosetta.projection.resubscriptions", "{resubscription}", "Projection resubscriptions")

    /// The W3C trace context of the command that stored an event, kept in the event's metadata, so the projections
    /// handling it continue that trace
    [<Literal>]
    let TraceParentHeader = "traceparent"
    [<Literal>]
    let TraceStateHeader = "tracestate"

    let Tag (key: string) (value: obj) = KeyValuePair<string, obj>(key, value)

    /// Seconds since a Stopwatch.GetTimestamp()
    let SecondsSince (started: int64) = Stopwatch.GetElapsedTime(started).TotalSeconds
