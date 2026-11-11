namespace SI.Rosetta.IntegrationTests

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open FSharp.Control
open KurrentDB.Client
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.KurrentDB
open SI.Rosetta.Common

/// A parcel's state, snapshotted every 2 events
[<Snapshot(2)>]
type SnapshottedParcelState() =
    inherit AggregateState<ParcelEvents>()
    member val Recipient = "" with get, set
    member val Recipients : string list = [] with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | ParcelEvents.Registered e ->
            this.Id <- e.Id
            this.Recipient <- e.Recipient
            this.Recipients <- [ e.Recipient ]
        | ParcelEvents.Readdressed e ->
            this.Recipient <- e.Recipient
            this.Recipients <- this.Recipients @ [ e.Recipient ]

type SnapshottedParcelAggregate() =
    inherit Aggregate<SnapshottedParcelState, ParcelCommands>()

    override this.Execute(command) =
        match command with
        | ParcelCommands.Register cmd -> this.Apply(ParcelEvents.Registered { Id = cmd.Id; Recipient = cmd.Recipient })
        | ParcelCommands.Readdress cmd -> this.Apply(ParcelEvents.Readdressed { Id = cmd.Id; Recipient = cmd.Recipient })

/// Keeps part of what it builds in a field: a snapshot would lose it
[<Snapshot(1)>]
type FieldParcelState() =
    inherit AggregateState<ParcelEvents>()
    let mutable readdressed = 0
    member this.Readdressed = readdressed
    override this.ApplyEvent(ev) =
        match ev with
        | ParcelEvents.Registered e -> this.Id <- e.Id
        | ParcelEvents.Readdressed _ -> readdressed <- readdressed + 1

/// Snapshots in KurrentDB: taken every n events, the latest kept, loaded from, and ignored when they don't fit
type KurrentDBSnapshotsTests(databases: Databases) =
    let Client = databases.KurrentDBClient
    let Repository = KurrentDBAggregateRepository(Client) :> IAggregateRepository

    // Not the Parcels category, which HostingTests projects
    let Id = sprintf "SnapshottedParcels-%s" (Guid.NewGuid().ToString("N"))
    let SnapshotStream = KurrentDBAggregateRepository.SnapshotStreamOf Id

    let Load () =
        task {
            let! state = Repository.GetAsync<SnapshottedParcelState>(Id)
            return SnapshottedParcelAggregate(State = state.Value)
        }

    /// Registered to Ada, then readdressed to each recipient, one command each
    let Store (recipients: string list) =
        task {
            let registered = SnapshottedParcelAggregate()
            registered.Execute(ParcelCommands.Register { Id = Id; Recipient = "Ada" })
            do! Repository.StoreAsync(registered)
            for recipient in recipients do
                let! parcel = Load ()
                parcel.Execute(ParcelCommands.Readdress { Id = Id; Recipient = recipient })
                do! Repository.StoreAsync(parcel)
        }

    let StoredSnapshots () =
        task {
            let read = Client.ReadStreamAsync(Direction.Forwards, SnapshotStream, StreamPosition.Start)
            match! read.ReadState with
            | ReadState.StreamNotFound -> return []
            | _ -> return! read |> TaskSeq.toListAsync
        }

    /// The id of the stream's event at revision
    let EventIdAt (revision: int64) =
        task {
            let! events = Client.ReadStreamAsync(Direction.Forwards, Id, StreamPosition.FromInt64 revision, 1L) |> TaskSeq.toListAsync
            return events.Head.Event.EventId.ToGuid()
        }

    /// Writes a snapshot of its own: what a doctored state, metadata or event id does to loading
    let WriteSnapshot (recipient: string) (version: int64) (fingerprint: string) (lastEventId: Guid) =
        task {
            let state = SnapshottedParcelState(Id = Id, Version = version, Recipient = recipient, Recipients = [ recipient ])
            let data = JsonSerializer.Serialize(state, EventEncoding.JsonOptions)
            let metadata = sprintf """{"Version":%d,"Fingerprint":"%s","LastEventId":"%O"}""" version fingerprint lastEventId
            let! _ = Client.AppendToStreamAsync(SnapshotStream, StreamState.Any, [ EventData(Uuid.NewUuid(), "RosettaSnapshot", ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes data), ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes metadata)) ])
            ()
        }

    let Fingerprint () = Snapshots.FingerprintOf typeof<SnapshottedParcelState> (SnapshotAttribute(2))

    [<Fact>]
    member this.``A snapshot is taken every 2 events, only the latest is kept, and loading gives the whole state``() =
        task {
            do! Store [ "Grace"; "Linus"; "Ada"; "Joan" ]

            let! snapshots = StoredSnapshots ()
            let snapshot = Assert.Single(snapshots)
            Assert.Contains("\"Version\":4", Encoding.UTF8.GetString(snapshot.Event.Metadata.Span))
            Assert.DoesNotContain("SnapshottedParcels", snapshot.Event.EventStreamId.Split('-').[0])
            let! parcel = Load ()
            Assert.Equal(5L, parcel.Version)
            Assert.Equal<string list>([ "Ada"; "Grace"; "Linus"; "Ada"; "Joan" ], parcel.State.Recipients)
        }

    [<Fact>]
    member this.``Loading starts from the snapshot, and applies the events after it``() =
        task {
            do! Store [ "Grace"; "Linus" ]
            let! lastCovered = EventIdAt 1L
            // A snapshot no event built: only loading from it gives Snapshotted
            do! WriteSnapshot "Snapshotted" 2L (Fingerprint ()) lastCovered

            let! parcel = Load ()

            Assert.Equal(3L, parcel.Version)
            Assert.Equal("Linus", parcel.State.Recipient)
            Assert.Equal<string list>([ "Snapshotted"; "Linus" ], parcel.State.Recipients)
        }

    [<Fact>]
    member this.``A snapshot of another shape or version is ignored``() =
        task {
            do! Store [ "Grace"; "Linus" ]
            let! lastCovered = EventIdAt 1L
            do! WriteSnapshot "Snapshotted" 2L "0000000000000000" lastCovered

            let! parcel = Load ()

            Assert.Equal<string list>([ "Ada"; "Grace"; "Linus" ], parcel.State.Recipients)
        }

    [<Fact>]
    member this.``A snapshot whose last event isn't in the stream anymore is ignored``() =
        task {
            do! Store [ "Grace"; "Linus" ]
            do! WriteSnapshot "Snapshotted" 2L (Fingerprint ()) (Guid.NewGuid())

            let! parcel = Load ()

            Assert.Equal<string list>([ "Ada"; "Grace"; "Linus" ], parcel.State.Recipients)
        }

    [<Fact>]
    member this.``A state a snapshot would lose part of can't be snapshotted``() =
        task {
            let! refused = Assert.ThrowsAsync<InvalidOperationException>(fun () -> Repository.GetAsync<FieldParcelState>(Id) :> Task)

            Assert.Contains("FieldParcelState can't be snapshotted", refused.Message)
            Assert.Contains("readdressed", refused.Message)
        }
