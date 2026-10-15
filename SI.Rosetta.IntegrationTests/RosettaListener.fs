namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Diagnostics.Metrics
open SI.Rosetta.Common

/// Records what Rosetta traces and measures while it's alive, as an OpenTelemetry exporter would receive it
type RosettaListener() =
    let StoppedActivities = ConcurrentQueue<Activity>()
    let Measurements = ConcurrentQueue<string * float * Map<string, obj>>()

    let ActivityListener =
        new ActivityListener(
            ShouldListenTo = (fun source -> source.Name = RosettaTelemetry.Name),
            Sample = SampleActivity<ActivityContext>(fun _ -> ActivitySamplingResult.AllDataAndRecorded),
            ActivityStopped = (fun activity -> StoppedActivities.Enqueue activity))

    let MeterListener = new MeterListener()

    let Record (instrument: Instrument) (value: float) (tags: ReadOnlySpan<Collections.Generic.KeyValuePair<string, obj>>) =
        Measurements.Enqueue(instrument.Name, value, tags.ToArray() |> Array.map (fun tag -> tag.Key, tag.Value) |> Map.ofArray)

    do
        ActivitySource.AddActivityListener ActivityListener
        MeterListener.InstrumentPublished <- fun instrument listener ->
            if instrument.Meter.Name = RosettaTelemetry.Name then listener.EnableMeasurementEvents instrument
        MeterListener.SetMeasurementEventCallback<int64>(fun instrument value tags _ -> Record instrument (float value) tags)
        MeterListener.SetMeasurementEventCallback<double>(fun instrument value tags _ -> Record instrument value tags)
        MeterListener.Start()

    member _.Activities = StoppedActivities.ToArray()

    /// The measurements of the instrument whose tags include these
    member _.Measured (instrument: string) (tags: (string * obj) list) =
        Measurements.ToArray()
        |> Array.filter (fun (name, _, measuredTags) ->
            name = instrument && tags |> List.forall (fun (key, value) -> measuredTags.TryFind key = Some value))
        |> Array.map (fun (_, value, _) -> value)

    interface IDisposable with
        member _.Dispose() =
            ActivityListener.Dispose()
            MeterListener.Dispose()
