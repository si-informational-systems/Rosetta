namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Xunit
open FSharp.Control
open KurrentDB.Client
open SI.Rosetta.Persistence.KurrentDB
open Microsoft.Extensions.Logging.Abstractions
open SI.Rosetta.Projections
open SI.Rosetta.Projections.KurrentDB

/// Custom JS projections, as the projections host creates and updates them on startup
type KurrentDBCustomProjectionsTests(databases: Databases) =
    let Configuration = databases.Configuration("custom-projections")
    let ManagementClient = KurrentDBFactory.InitializeProjectionManagementClient Configuration
    let HttpApi = KurrentDBProjectionsHttpApi(Configuration)

    // Every test has a projection, a source stream and a destination stream of its own
    let Name = "Parcels" + Guid.NewGuid().ToString("N")
    let SourceStream = "parcel-" + Name
    // Every test is an application of its own: it deletes only the custom projections it created
    let Owner = "Tests" + Name

    /// A custom projection linking these events of the test's parcel stream into cp-{Name}
    let Parameters (events: Type list) : KurrentDBProjectionParameters =
        { ProjectionName = Name + "CustomProjection"
          DestinationStreamName = "cp-" + Name
          AggregateEventsStreamNames = [ SourceStream ]
          EventsToInclude = Array.ofList events }

    /// What the projections host does on startup
    let Sync (logger: CapturingLogger<KurrentDBCustomJSProjectionsFactory>) (parameters: KurrentDBProjectionParameters) =
        let factory = KurrentDBCustomJSProjectionsFactory(Configuration, logger, ManagementClient, databases.KurrentDBClient, Owner) :> IKurrentDBCustomJSProjectionsFactory
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

    member private this.LinkedAre (parameters: KurrentDBProjectionParameters) (expected: string list) =
        Eventually (TimeSpan.FromSeconds 30.0) (sprintf "%s links %A" parameters.DestinationStreamName expected) (fun () ->
            task {
                let! linked = Linked parameters
                return linked = expected
            })

    [<Fact>]
    member this.``Starting with an unchanged definition leaves the projection, and with a changed one links its whole history again``() =
        task {
            let registered = Parameters [ typeof<ParcelRegistered> ]
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" }; ParcelEvents.Readdressed { Id = Name; Recipient = "Grace" } ]
            do! Sync (CapturingLogger()) registered
            do! this.LinkedAre registered [ "ParcelRegistered" ]

            let unchangedLogger = CapturingLogger()
            do! Sync unchangedLogger registered

            Assert.Equal(sprintf "Custom projection %s is up to date." registered.ProjectionName, Logged unchangedLogger)

            let readdressedToo = Parameters [ typeof<ParcelRegistered>; typeof<ParcelReaddressed> ]
            let changedLogger = CapturingLogger()
            do! Sync changedLogger readdressedToo

            Assert.Equal(
                sprintf "Custom projection %s RELINKED: its definition changed, so %s is linked again from the start." readdressedToo.ProjectionName readdressedToo.DestinationStreamName,
                Logged changedLogger)
            do! AssertStored readdressedToo
            // The whole history again, the event the first query left out included, then what's appended after
            do! this.LinkedAre readdressedToo [ "ParcelRegistered"; "ParcelReaddressed" ]
            do! Append [ ParcelEvents.Readdressed { Id = Name; Recipient = "Linus" } ]
            do! this.LinkedAre readdressedToo [ "ParcelRegistered"; "ParcelReaddressed"; "ParcelReaddressed" ]
            // The first query's one link is gone: the stream is truncated before it
            let! metadata = databases.KurrentDBClient.GetStreamMetadataAsync(readdressedToo.DestinationStreamName)
            Assert.Equal(1UL, metadata.Metadata.TruncateBefore.Value.ToUInt64())
            let! details = ManagementClient.GetStatusAsync(readdressedToo.ProjectionName)
            Assert.Equal("Running", details.Status)
        }

    [<Fact>]
    member this.``A custom projection the code no longer defines is deleted with its stream, and another application's is left alone``() =
        task {
            let parameters = Parameters [ typeof<ParcelRegistered> ]
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" } ]
            do! Sync (CapturingLogger()) parameters
            do! this.LinkedAre parameters [ "ParcelRegistered" ]
            // Another application's, on the same KurrentDB
            let others = { parameters with ProjectionName = "Other" + parameters.ProjectionName; DestinationStreamName = "cp-Other" + Name }
            let otherApplication =
                KurrentDBCustomJSProjectionsFactory(Configuration, CapturingLogger(), ManagementClient, databases.KurrentDBClient, "Other" + Owner)
                :> IKurrentDBCustomJSProjectionsFactory
            do! otherApplication.CreateCustomProjections [ others ]

            // Renamed: a new projection, on a new stream
            let renamed = { parameters with ProjectionName = "Renamed" + parameters.ProjectionName; DestinationStreamName = "cp-Renamed" + Name }
            let logger = CapturingLogger()
            do! Sync logger renamed

            Assert.Contains(
                sprintf "Custom projection %s DELETED, with %s: the code no longer defines it." parameters.ProjectionName parameters.DestinationStreamName,
                logger.Entries |> Seq.map snd)
            let! projections = ManagementClient.ListAllAsync() |> TaskSeq.toListAsync
            let names = projections |> List.map (fun projection -> projection.Name)
            Assert.DoesNotContain(parameters.ProjectionName, names)
            Assert.Contains(renamed.ProjectionName, names)
            Assert.Contains(others.ProjectionName, names)
            let! deleted = Linked parameters
            Assert.Empty(deleted)
            do! this.LinkedAre renamed [ "ParcelRegistered" ]
            do! this.LinkedAre others [ "ParcelRegistered" ]
        }

    [<Fact>]
    member this.``A projection reading a relinked or replaced stream is told its read models no longer match``() =
        task {
            let parameters = Parameters [ typeof<ParcelRegistered> ]
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" } ]
            do! Sync (CapturingLogger()) parameters
            do! this.LinkedAre parameters [ "ParcelRegistered" ]
            let subscription = KurrentDBSubscription<ParcelEvents>(NullLogger<KurrentDBSubscription<ParcelEvents>>.Instance, databases.KurrentDBClient)
            (subscription :> ISubscription<ParcelEvents>).StreamName <- parameters.DestinationStreamName
            let history = subscription :> ISubscriptionHistory

            let! handledItAll = history.ReplacedSinceAsync 1UL
            let! pastTheEnd = history.ReplacedSinceAsync 5UL

            Assert.True(handledItAll.IsNone)
            Assert.Contains("shorter than checkpoint 5", pastTheEnd.Value)

            do! Sync (CapturingLogger()) (Parameters [ typeof<ParcelRegistered>; typeof<ParcelReaddressed> ])
            let! builtFromDeletedLinks = history.ReplacedSinceAsync 1UL
            Assert.Contains("linked again from the start", builtFromDeletedLinks.Value)
            do! this.LinkedAre parameters [ "ParcelRegistered" ]
            // Rebuilt, it handled the new link (number 1): it matches
            let! rebuilt = history.ReplacedSinceAsync 2UL
            Assert.True(rebuilt.IsNone)
        }

    [<Fact>]
    member this.``When the code defines no custom projection anymore, the ones it created are deleted``() =
        task {
            let parameters = Parameters [ typeof<ParcelRegistered> ]
            do! Append [ ParcelEvents.Registered { Id = Name; Recipient = "Ada" } ]
            do! Sync (CapturingLogger()) parameters
            do! this.LinkedAre parameters [ "ParcelRegistered" ]
            let factory = KurrentDBCustomJSProjectionsFactory(Configuration, CapturingLogger(), ManagementClient, databases.KurrentDBClient, Owner) :> IKurrentDBCustomJSProjectionsFactory

            do! factory.CreateCustomProjections(Array.empty<KurrentDBProjectionParameters>)

            let! projections = ManagementClient.ListAllAsync() |> TaskSeq.toListAsync
            Assert.DoesNotContain(parameters.ProjectionName, projections |> List.map (fun projection -> projection.Name))
            let! linked = Linked parameters
            Assert.Empty(linked)
        }
