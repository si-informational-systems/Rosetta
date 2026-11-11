namespace SI.Rosetta.Aggregates.KurrentDB

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading.Tasks
open KurrentDB.Client
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open SI.Rosetta.Aggregates
open SI.Rosetta.Common
open SI.Rosetta.Serialization

/// What a snapshot's metadata says: the version it's the state at, the fingerprint of the state's shape, and the id of
/// the last event it covers, which must still be in the stream at version - 1
type internal SnapshotMetadata =
    { Version: int64; Fingerprint: string; LastEventId: Guid }

    member this.ToJson() =
        let fields = Dictionary<string, obj>()
        fields.["Version"] <- box this.Version
        fields.["Fingerprint"] <- box this.Fingerprint
        fields.["LastEventId"] <- box this.LastEventId
        JsonSerializer.SerializeToUtf8Bytes fields

    static member Parse(json: ReadOnlySpan<byte>) =
        use document = JsonDocument.Parse(json.ToArray())
        let root = document.RootElement
        { Version = root.GetProperty("Version").GetInt64()
          Fingerprint = root.GetProperty("Fingerprint").GetString()
          LastEventId = root.GetProperty("LastEventId").GetGuid() }

/// Event-sourced aggregates in KurrentDB. Events are upcast while the aggregate is read; one its events DU has no case for
/// fails the read with UnknownEventException, as skipping it would rebuild the wrong state.
/// A state with [<Snapshot(n)>] is snapshotted every n events into the stream RosettaSnapshots-{id} (its own category,
/// so projections of the aggregate's never see it; $maxCount 1, so only the latest is kept), once its events are
/// appended: a failed snapshot is logged, and the command stands. It's loaded from its latest snapshot and the events
/// after it, unless the snapshot no longer fits (another shape or [<Snapshot>] Version, or its last event isn't where
/// it was): then from its whole stream
type KurrentDBAggregateRepository(client: KurrentDBClient, upcasters: IEventUpcaster seq, logger: ILogger<KurrentDBAggregateRepository>) =
    let Upcasters = Seq.toArray upcasters

    [<Literal>]
    let SnapshotEventType = "RosettaSnapshot"

    new(client: KurrentDBClient, upcasters: IEventUpcaster seq) =
        KurrentDBAggregateRepository(client, upcasters, NullLogger<KurrentDBAggregateRepository>.Instance)

    new(client: KurrentDBClient) = KurrentDBAggregateRepository(client, Seq.empty)

    /// The stream an aggregate's snapshots are in
    static member SnapshotStreamOf(id: string) = "RosettaSnapshots-" + id

    member private this.ToEventData (event: IAggregateEvents) (headers: IDictionary<string, obj>) =
        let serializedEvent = KurrentDBSerialization.Serialize(event, headers)
        EventData(Uuid.NewUuid(), serializedEvent.EventClrName, serializedEvent.Data, ReadOnlyMemory<byte> serializedEvent.Metadata)

    /// Appends the aggregate's Changes, expecting the version it was loaded at. Answers the last event's id
    member private this.SaveAggregate (aggregate: IAggregate) =
        task {
            let commitHeaders =
                let dict = Dictionary<string, obj>()
                dict.Add(KurrentDBSerialization.AggregateClrTypeNameHeader, aggregate.GetType().AssemblyQualifiedName :> obj)
                dict

            let newEvents = aggregate.Changes |> Seq.toList
            let originalVersion = aggregate.Version - int64 newEvents.Length
            let expectedState =
                if originalVersion = 0L then StreamState.NoStream
                else StreamState.StreamRevision(uint64 (originalVersion - 1L))

            let eventData = newEvents |> List.map (fun e -> this.ToEventData e commitHeaders)

            let! _ = client.AppendToStreamAsync(aggregate.Id, expectedState, eventData).ConfigureAwait(false)
            aggregate.Changes.Clear()
            return (List.last eventData).EventId
        }

    member private this.TrySaveAggregate (aggregate: IAggregate) =
        task {
            try
                return! this.SaveAggregate(aggregate).ConfigureAwait(false)
            with
            | :? AggregateException as ex when (ex.InnerException :? WrongExpectedVersionException) ->
                return raise (ConcurrencyException(ex.Message, ex.InnerException))
            | :? WrongExpectedVersionException as ex ->
                return raise (ConcurrencyException(ex.Message, ex))
        }

    member private this.WriteSnapshotAsync(stateType: Type, snapshot: SnapshotAttribute, id: string, version: int64, lastEventId: Uuid, state: obj) =
        task {
            let stream = KurrentDBAggregateRepository.SnapshotStreamOf id
            let metadata = { Version = version; Fingerprint = Snapshots.FingerprintOf stateType snapshot; LastEventId = lastEventId.ToGuid() }
            let data = EventData(
                        Uuid.NewUuid(), SnapshotEventType,
                        ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(Snapshots.Serialize stateType state)),
                        ReadOnlyMemory<byte>(metadata.ToJson()))
            let! written = client.AppendToStreamAsync(stream, StreamState.Any, [ data ]).ConfigureAwait(false)
            // The stream's first snapshot: only the latest is kept
            if written.NextExpectedStreamState = StreamState.StreamRevision 0UL then
                let! _ = client.SetStreamMetadataAsync(stream, StreamState.Any, StreamMetadata(maxCount = Nullable 1)).ConfigureAwait(false)
                ()
        }

    /// StoreAsync, apart from the generic interface method (where the F# compiler emits an invalid program for such code)
    member private this.StoreStateAsync(stateType: Type, aggregate: IAggregate, state: obj) : Task =
        task {
            let loaded = LoadedVersionOf aggregate
            let stored = aggregate.Version
            let! lastEventId = this.TrySaveAggregate(aggregate).ConfigureAwait(false)
            match Snapshots.Of stateType with
            | Some snapshot when Snapshots.IsDue snapshot loaded stored ->
                try
                    do! this.WriteSnapshotAsync(stateType, snapshot, aggregate.Id, stored, lastEventId, state).ConfigureAwait(false)
                with ex ->
                    logger.LogWarning(ex, "The snapshot of {State} {Id} at version {Version} couldn't be written: it's loaded from an older one, or its whole stream, until the next.",
                        stateType.Name, aggregate.Id, stored)
            | _ -> ()
        } :> Task

    /// The latest snapshot, when it fits: the state's shape and [<Snapshot>] Version, and at version or before
    member private this.SnapshotAsync(stateType: Type, snapshot: SnapshotAttribute, id: string, version: int64) : Task<(IAggregateState * SnapshotMetadata) option> =
        task {
            let fingerprint = Snapshots.FingerprintOf stateType snapshot
            let read = client.ReadStreamAsync(Direction.Backwards, KurrentDBAggregateRepository.SnapshotStreamOf id, StreamPosition.End, 1L)
            let! readState = read.ReadState
            if readState = ReadState.StreamNotFound then return None
            else
                let events = read.GetAsyncEnumerator()
                let! found = events.MoveNextAsync().AsTask()
                if not found then return None
                else
                    let event = events.Current.Event
                    let metadata = SnapshotMetadata.Parse event.Metadata.Span
                    if metadata.Fingerprint <> fingerprint || metadata.Version > version then return None
                    else
                        let state = Snapshots.Deserialize stateType (Encoding.UTF8.GetString(event.Data.Span)) :?> IAggregateState
                        state.Version <- metadata.Version
                        return Some (state, metadata)
        }

    /// Applies the stream's events from revision from on, until the state reaches version. Answers whether the stream
    /// exists, and, when expected is given, whether its first event is that one (a snapshot's last event)
    member private this.ApplyAsync(state: IAggregateState, id: string, from: int64, version: int64, expected: Guid option) : Task<bool> =
        task {
            let maxCount = if version = Int64.MaxValue then Int64.MaxValue else version - from
            let read = client.ReadStreamAsync(Direction.Forwards, id, StreamPosition.FromInt64 from, maxCount)
            let! readState = read.ReadState
            if readState = ReadState.StreamNotFound then return false
            else
                let events = read.GetAsyncEnumerator()
                let mutable fits = true
                let mutable first = expected.IsSome
                let mutable reading = true
                while reading do
                    let! next = events.MoveNextAsync().AsTask()
                    if not next then reading <- false
                    elif first then
                        first <- false
                        fits <- events.Current.Event.EventId.ToGuid() = expected.Value
                        reading <- fits
                    elif state.Version = version then reading <- false
                    else
                        state.Mutate(KurrentDBSerialization.DeserializeAs state.EventsType Upcasters events.Current)
                // The snapshot's last event was the stream's last, or it isn't there anymore
                return fits && not first
        }

    /// GetAsync, apart from the generic interface method
    member private this.ReadStateAsync(stateType: Type, id: string, version: int64) : Task<IAggregateState option> =
        task {
            let snapshot = Snapshots.Of stateType
            let! fromSnapshot =
                match snapshot with
                | Some snapshot when version > 0L -> this.SnapshotAsync(stateType, snapshot, id, version)
                | _ -> Task.FromResult None
            let! resumed =
                match fromSnapshot with
                | Some (state, metadata) ->
                    task {
                        // From the snapshot's last event, which must still be the one it covered
                        let! fits = this.ApplyAsync(state, id, metadata.Version - 1L, version, Some metadata.LastEventId)
                        return if fits then Some state else None
                    }
                | None -> Task.FromResult None
            match resumed with
            | Some state -> return Some state
            | None ->
                let state = Activator.CreateInstance stateType :?> IAggregateState
                let! exists = this.ApplyAsync(state, id, 0L, version, None)
                return if exists then Some state else None
        }

    interface IEventSourcedAggregateRepository with
        member this.StoreAsync<'TAggregateState
                                when 'TAggregateState :> IAggregateState>
                                (aggregate: IAggregate<'TAggregateState>) =
            this.StoreStateAsync(typeof<'TAggregateState>, aggregate, box (aggregate.GetState()))

        member this.GetAsync<'TAggregateState
                                when 'TAggregateState : (new : unit -> 'TAggregateState)
                                and 'TAggregateState :> IAggregateState>
            (id: string, version: int64) =
            task {
                match! this.ReadStateAsync(typeof<'TAggregateState>, id, version).ConfigureAwait(false) with
                | Some state -> return Some (state :?> 'TAggregateState)
                | None -> return None
            }
