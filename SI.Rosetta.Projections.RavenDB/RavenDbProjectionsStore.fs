namespace SI.Rosetta.Projections.RavenDB

open System
open System.Collections.Generic
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Documents.Session
open SI.Rosetta.Projections

/// While a Projection handles an event, every call goes through the event's session (RavenDbProjectionTransaction),
/// which is saved with the checkpoint. Outside one, every call has its own session and writes straight away
type RavenDbProjectionsStore(store: IDocumentStore) =
    let DefensivelyLoad (operation: unit -> Task<'T>) = RavenDbRetries.Retry 0 operation

    let DefensivelyStore (operation: unit -> Task) =
        RavenDbRetries.Retry 0 (fun () -> task { do! operation().ConfigureAwait(false) }) :> Task

    let ToStringId (id: obj) =
        try
            id.ToString()
        with
        | ex -> raise (InvalidOperationException("Failed to convert id to string", ex))

    static member private LoadMany<'T>(session: IAsyncDocumentSession, ids: obj[]) : Task<Dictionary<obj, 'T>> =
        task {
            let stringIds =
                try
                    ids |> Array.map (fun id -> id.ToString())
                with
                | ex -> raise (InvalidOperationException("Failed to convert ids to string array", ex))
            let! ravenResult = session.LoadAsync<'T>(stringIds).ConfigureAwait(false)
            let result = Dictionary<obj, 'T>()
            for kvp in ravenResult do
                result.[kvp.Key :> obj] <- kvp.Value
            return result
        }

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction -> transaction.StoreAsync(doc)
            | _ ->
                DefensivelyStore (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        do! session.StoreAsync(doc).ConfigureAwait(false)
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    })

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction ->
                task {
                    for doc in docs do
                        do! transaction.StoreAsync(doc).ConfigureAwait(false)
                } :> Task
            | _ ->
                DefensivelyStore (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        for doc in docs do
                            do! session.StoreAsync(doc).ConfigureAwait(false)
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    })

        member this.LoadAsync<'T when 'T : not struct>(id: obj): Task<'T> =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction -> transaction.Session.LoadAsync<'T>(ToStringId id)
            | _ ->
                DefensivelyLoad (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        return! session.LoadAsync<'T>(ToStringId id).ConfigureAwait(false)
                    })

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction -> RavenDbProjectionsStore.LoadMany<'T>(transaction.Session, ids)
            | _ ->
                DefensivelyLoad (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        return! RavenDbProjectionsStore.LoadMany<'T>(session, ids)
                    })

        member this.DeleteAsync<'T when 'T : not struct>(id: obj) =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction -> transaction.DeleteAsync<'T>(ToStringId id)
            | _ ->
                // By the Id's string: given an obj, session.Delete binds to Delete<T>(entity), which only takes loaded entities
                DefensivelyStore (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        session.Delete(id.ToString())
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    })

        member this.DeleteInUnitOfWorkAsync<'T when 'T : not struct>(ids: obj[]) =
            match ProjectionTransaction.Current with
            | :? RavenDbProjectionTransaction as transaction ->
                task {
                    for id in ids do
                        do! transaction.DeleteAsync<'T>(ToStringId id).ConfigureAwait(false)
                } :> Task
            | _ ->
                DefensivelyStore (fun () ->
                    task {
                        use session = store.OpenAsyncSession()
                        for id in ids do
                            session.Delete(id.ToString())
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    })

    interface INoSqlStore
