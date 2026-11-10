namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open FSharp.Control
open KurrentDB.Client
open SI.Rosetta.Persistence.KurrentDB
open SI.Rosetta.Projections.KurrentDB

/// Custom JS projections, as the projections host creates and updates them on startup
type KurrentDBCustomProjectionsTests(databases: Databases) =
    let Configuration = databases.Configuration("custom-projections")
    let ManagementClient = KurrentDBFactory.InitializeProjectionManagementClient Configuration
    let HttpApi = KurrentDBProjectionsHttpApi(Configuration)

    // Every test has a projection, a source stream and a destination stream of its own
    let Name = "Parcels" + Guid.NewGuid().ToString("N")
    let SourceStream = "parcel-" + Name

    /// A custom projection linking these events of the test's parcel stream into cp-{Name}
    let Parameters (events: Type list) : KurrentDBProjectionParameters =
        { ProjectionName = Name + "CustomProjection"
          DestinationStreamName = "cp-" + Name
          AggregateEventsStreamNames = [ SourceStream ]
          EventsToInclude = Array.ofList events }

    /// What the projections host does on startup
    let Sync (logger: CapturingLogger<KurrentDBCustomJSProjectionsFactory>) (parameters: KurrentDBProjectionParameters) =
        let factory = KurrentDBCustomJSProjectionsFactory(Configuration, logger, ManagementClient) :> IKurrentDBCustomJSProjectionsFactory
        factory.CreateCustomProjections [ parameters ]

    /// The one message logged
    let Logged (logger: CapturingLogger<KurrentDBCustomJSProjectionsFactory>) =
        Assert.Single(logger.Entries |> Seq.map snd)

    /// The projection's stored query and emit setting are the ones these parameters build
    let AssertStored (parameters: KurrentDBProjectionParameters) =
        task {
            match! HttpApi.TryGetDefinitionAsync(parameters.ProjectionName) with
            | Ok stored ->
                Assert.Equal({ Query = (KurrentDBProjectionBuilder.BuildProjectionDefinition parameters).Source; EmitEnabled = true }, stored)
            | Error error -> Assert.Fail(sprintf "The stored definition couldn't be read: %s" error)
        }

    /// The event types linked into the destination stream so far, from the links themselves
    let Linked (parameters: KurrentDBProjectionParameters) =
        task {
            let result = databases.KurrentDBClient.ReadStreamAsync(Direction.Forwards, parameters.DestinationStreamName, StreamPosition.Start, resolveLinkTos = true)
            match! result.ReadState with
            | ReadState.StreamNotFound -> return []
            | _ ->
                let! events = result |> TaskSeq.toListAsync
                return events |> List.map (fun e -> (if isNull (box e.Link) then "copy of " else "") + e.Event.EventType)
        }

    let Append (events: ParcelEvents list) =
        task {
            let! _ = databases.KurrentDBClient.AppendToStreamAsync(SourceStream, StreamState.Any, events |> List.map Events.Known)
            ()
        }

    interface IDisposable with
        member _.Dispose() = ManagementClient.Dispose()

    [<Fact>]
    member this.``A new custom projection is created running, with emit enabled, and links the events it includes``() =
        task {
            let logger = CapturingLogger()
            let parameters = Parameters [ typeof<ParcelRegistered> ]
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" } ]

            do! Sync logger parameters

            Assert.Equal(sprintf "Custom projection %s CREATED." parameters.ProjectionName, Logged logger)
            do! AssertStored parameters
            do! Eventually (TimeSpan.FromSeconds 30.0) "the event appended before the projection existed is linked" (fun () ->
                    task {
                        let! linked = Linked parameters
                        return linked = [ "ParcelRegistered" ]
                    })
            let! details = ManagementClient.GetStatusAsync(parameters.ProjectionName)
            Assert.Equal("Running", details.Status)
        }

    [<Fact>]
    member this.``Starting with an unchanged definition leaves the projection, and with a changed one updates it``() =
        task {
            let registered = Parameters [ typeof<ParcelRegistered> ]
            do! Sync (CapturingLogger()) registered

            let unchangedLogger = CapturingLogger()
            do! Sync unchangedLogger registered

            Assert.Equal(sprintf "Custom projection %s is up to date." registered.ProjectionName, Logged unchangedLogger)

            let readdressedToo = Parameters [ typeof<ParcelRegistered>; typeof<ParcelReaddressed> ]
            let changedLogger = CapturingLogger()
            do! Sync changedLogger readdressedToo

            Assert.Equal(sprintf "Custom projection %s UPDATED: its definition changed." readdressedToo.ProjectionName, Logged changedLogger)
            do! AssertStored readdressedToo
            // The updated query runs, still emitting: it links the event the first one left out
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" }; ParcelEvents.Readdressed { Id = Name; Recipient = "Grace" } ]
            do! Eventually (TimeSpan.FromSeconds 30.0) "both events are linked" (fun () ->
                    task {
                        let! linked = Linked readdressedToo
                        return linked = [ "ParcelRegistered"; "ParcelReaddressed" ]
                    })
        }
