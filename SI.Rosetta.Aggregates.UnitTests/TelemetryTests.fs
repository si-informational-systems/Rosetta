namespace SI.Rosetta.Aggregates.UnitTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Diagnostics.Metrics
open Xunit
open SI.Rosetta.Aggregates.TestKit
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

/// The listeners see the whole process, so these tests don't run alongside others
[<CollectionDefinition("Telemetry", DisableParallelization = true)>]
type TelemetryCollection() = class end

[<Collection("Telemetry")>]
type CommandTelemetryTests() =
    inherit TestKitBase<OrganizationAggregateHandler>()

    [<Fact>]
    member this.``A command is traced and counted``() =
        task {
            use listener = new RosettaListener()
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString("N"))
            this.Given()
            this.When(OrganizationCommands.Register { Id = id; Name = "Rosetta" })

            let registered = OrganizationEvents.Registered { Id = id; Name = "Rosetta" } :> IAggregateEvents
            do! this.Then(ResizeArray [ registered ], ResizeArray [ registered ])

            let command = listener.Activities |> Array.find (fun activity -> activity.GetTagItem("rosetta.aggregate.id") = box id)
            Assert.Equal("Organization RegisterOrganization", command.DisplayName)
            Assert.Equal(box "handled", command.GetTagItem "rosetta.outcome")
            Assert.Equal(box 1L, command.GetTagItem "rosetta.events")
            Assert.NotEqual(ActivityStatusCode.Error, command.Status)
            let commandTags = [ "rosetta.aggregate", box "Organization"; "rosetta.command", box "RegisterOrganization" ]
            Assert.Equal<float>([| 1.0 |], listener.Measured "rosetta.commands" (("rosetta.outcome", box "handled") :: commandTags))
            Assert.Single(listener.Measured "rosetta.command.duration" commandTags) |> ignore
            Assert.Equal<float>([| 1.0 |], listener.Measured "rosetta.events.stored" [ "rosetta.aggregate", box "Organization" ])
        }

[<Collection("Telemetry")>]
type RejectedCommandTelemetryTests() =
    inherit TestKitBase<OrganizationAggregateHandler>()

    [<Fact>]
    member this.``A rejected command is traced as an error with its domain error``() =
        task {
            use listener = new RosettaListener()
            let id = sprintf "Organizations-%s" (Guid.NewGuid().ToString("N"))
            this.Given()
            this.When(OrganizationCommands.ChangeName { Id = id; Name = "Rosetta" })

            do! this.ThenError("OrganizationDoesNotExist")

            let command = listener.Activities |> Array.find (fun activity -> activity.GetTagItem("rosetta.aggregate.id") = box id)
            Assert.Equal(ActivityStatusCode.Error, command.Status)
            Assert.Equal(box "OrganizationDoesNotExist", command.GetTagItem "rosetta.domain_error")
            Assert.Equal<float>(
                [| 1.0 |],
                listener.Measured "rosetta.commands" [ "rosetta.command", box "ChangeOrganizationName"; "rosetta.outcome", box "rejected" ])
        }
