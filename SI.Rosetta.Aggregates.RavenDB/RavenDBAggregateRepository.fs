namespace SI.Rosetta.Aggregates.RavenDB

open Raven.Client.Documents
open SI.Rosetta.Aggregates

/// With an outbox, the aggregate's PublishedEvents are saved with its state, in one transaction, or on their own when
/// the command changed nothing
type RavenDBAggregateRepository(store: IDocumentStore, outbox: RavenDBOutbox option) =
    // Version is kept in the document's @metadata so the AggregateState document itself stays clean
    let VersionMetadataKey = "Rosetta-Aggregate-Version"

    new(store: IDocumentStore) = RavenDBAggregateRepository(store, None)

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
                use session = store.OpenAsyncSession()
                let state = aggregate.GetState()

                do! session.StoreAsync(state).ConfigureAwait(false)
                let metadata = session.Advanced.GetMetadataFor(state)
                metadata.[VersionMetadataKey] <- box aggregate.Version
                let events =
                    match outbox with
                    | Some _ -> OutboxEvents.Of aggregate
                    | None -> [||]
                let! recorded =
                    if events.Length > 0 then outbox.Value.StoreAsync(session, events)
                    else System.Threading.Tasks.Task.FromResult [||]
                do! session.SaveChangesAsync().ConfigureAwait(false)
                if recorded.Length > 0 then OutboxEvents.Recorded recorded.[0].Aggregate recorded.Length
            }

    interface IRecordPublishedEvents with
        member this.RecordPublishedEventsAsync(aggregate) =
            match outbox with
            | Some outbox -> OutboxEvents.RecordAsync outbox aggregate
            | None -> System.Threading.Tasks.Task.CompletedTask
