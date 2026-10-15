namespace SI.Rosetta.Aggregates.RavenDB

open Raven.Client.Documents
open SI.Rosetta.Aggregates

type RavenDBAggregateRepository(store: IDocumentStore) =
    // Version is kept in the document's @metadata so the AggregateState document itself stays clean
    let VersionMetadataKey = "Rosetta-Aggregate-Version"

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
                do! session.SaveChangesAsync().ConfigureAwait(false)
            }
