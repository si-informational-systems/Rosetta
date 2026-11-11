namespace SI.Rosetta.Aggregates.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

/// Every write is checked against the version the aggregate was loaded at (ConcurrencyException when another command got
/// there first), so a command never overwrites another's.
/// With an outbox, the aggregate's PublishedEvents are saved with its state, in one transaction, or on their own when
/// the command changed nothing
type RavenDBAggregateRepository(store: IDocumentStore, outbox: RavenDBOutbox option) =
    // Version is kept in the document's @metadata so the AggregateState document itself stays clean
    let VersionMetadataKey = "Rosetta-Aggregate-Version"

    new(store: IDocumentStore) = RavenDBAggregateRepository(store, None)

    /// The change vector the state may be saved over: none ("", saved only while no document has the Id) for a new
    /// aggregate; for a loaded one, the stored document's, once its version is checked to be the one the aggregate was
    /// loaded at. Read in a session of its own: the session the state is stored in can't track a second object for the Id
    member private this.ExpectedChangeVectorAsync<'TAggregateState>(id: string, loaded: int64) : Task<string> =
        task {
            if loaded = 0L then return String.Empty
            else
                use lookup = store.OpenAsyncSession()
                let! current = lookup.LoadAsync<'TAggregateState>(id).ConfigureAwait(false)
                if isNull (box current) then
                    return raise (ConcurrencyException.Changed(typeof<'TAggregateState>, id, loaded))
                else
                    let metadata = lookup.Advanced.GetMetadataFor(current)
                    let stored = if metadata.ContainsKey(VersionMetadataKey) then metadata.GetLong(VersionMetadataKey) else 0L
                    if stored <> loaded then
                        return raise (ConcurrencyException.Changed(typeof<'TAggregateState>, id, loaded))
                    else
                        return lookup.Advanced.GetChangeVectorFor(current)
        }

    /// RavenDB's ConcurrencyException as Rosetta's: the state's document changed (or was created) since the change
    /// vector was read, or an outbox event was recorded meanwhile by a concurrent repeat of the command
    static member private Refused(stateType: Type, id: string, loaded: int64, conflict: Raven.Client.Exceptions.ConcurrencyException) =
        if not (isNull conflict.Id) && not (String.Equals(conflict.Id, id, StringComparison.OrdinalIgnoreCase)) then
            ConcurrencyException(
                sprintf "%s %s: its event %s was recorded in the outbox by another command meanwhile, so nothing was stored." stateType.Name id conflict.Id,
                conflict)
        elif loaded = 0L then ConcurrencyException.Created(stateType, id, conflict)
        else ConcurrencyException.Changed(stateType, id, loaded, conflict)

    interface IStateBasedAggregateRepository with
        member this.GetAsync<'TAggregateState
                                    when 'TAggregateState : (new : unit -> 'TAggregateState)
                                    and 'TAggregateState :> IAggregateState>
            (id: string, version: int64): System.Threading.Tasks.Task<Option<'TAggregateState>> =
            task {
                use session = store.OpenAsyncSession()
                let! aggregateState = session.LoadAsync<'TAggregateState>(id).ConfigureAwait(false)
                if isNull (box aggregateState) then return None
                else
                    let metadata = session.Advanced.GetMetadataFor(aggregateState)
                    if metadata.ContainsKey(VersionMetadataKey) then
                        aggregateState.Version <- metadata.GetLong(VersionMetadataKey)
                    return Some aggregateState
            }
        member this.StoreAsync<'TAggregateState
                    when 'TAggregateState :> IAggregateState>
                    (aggregate: IAggregate<'TAggregateState>): System.Threading.Tasks.Task =
            task {
                let stateType = typeof<'TAggregateState>
                let loaded = LoadedVersionOf aggregate
                let! changeVector = this.ExpectedChangeVectorAsync<'TAggregateState>(aggregate.Id, loaded)
                use session = store.OpenAsyncSession()
                let state = aggregate.GetState()

                // Saved only while the document is still the one the change vector names (none: while there's none)
                do! session.StoreAsync(state, changeVector, aggregate.Id).ConfigureAwait(false)
                let metadata = session.Advanced.GetMetadataFor(state)
                metadata.[VersionMetadataKey] <- box aggregate.Version
                let events =
                    match outbox with
                    | Some _ -> OutboxEvents.Of aggregate
                    | None -> [||]
                let! recorded =
                    if events.Length > 0 then outbox.Value.StoreAsync(session, events)
                    else System.Threading.Tasks.Task.FromResult [||]
                try
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                with :? Raven.Client.Exceptions.ConcurrencyException as conflict ->
                    raise (RavenDBAggregateRepository.Refused(stateType, aggregate.Id, loaded, conflict))
                if recorded.Length > 0 then OutboxEvents.Recorded recorded.[0].Aggregate recorded.Length
            }

    interface IRecordPublishedEvents with
        member this.RecordPublishedEventsAsync(aggregate) =
            match outbox with
            | Some outbox -> OutboxEvents.RecordAsync outbox aggregate
            | None -> System.Threading.Tasks.Task.CompletedTask
