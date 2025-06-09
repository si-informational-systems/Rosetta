namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Exceptions
open SI.Rosetta.Projections

type RavenDbProjectionsStore(store: IDocumentStore) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(100.0)

    let rec DefensivelyLoad (retryCount: int) (operation: unit -> Task<'T>) =
        task {
            try
                return! operation().ConfigureAwait(false)
            with ex ->
                if not (RavenDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay).ConfigureAwait(false)
                return! DefensivelyLoad (retryCount + 1) operation
        }

    let rec DefensivelyStore (retryCount: int) (operation: unit -> Task) =
        task {
            try
                do! operation().ConfigureAwait(false)
            with ex ->
                if not (RavenDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay).ConfigureAwait(false)
                do! DefensivelyStore (retryCount + 1) operation
        }

    static member private IsTransient (ex: Exception) =
        match ex with
        | :? AggregateException as ex when (ex.InnerException :? RavenException) -> true
        | :? RavenException -> true
        | _ -> false

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) =
            DefensivelyStore 0 (fun () -> 
                task {
                    use session = store.OpenAsyncSession()
                    do! session.StoreAsync(doc).ConfigureAwait(false)
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                })

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    for doc in docs do
                        do! session.StoreAsync(doc).ConfigureAwait(false)
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                })

        member this.LoadAsync<'T when 'T : not struct>(id: obj): Task<'T> = 
            DefensivelyLoad 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    let stringId = 
                        try
                            id.ToString()
                        with
                        | ex -> raise (InvalidOperationException("Failed to convert id to string", ex))
                    let! ravenResult = session.LoadAsync<'T>(stringId).ConfigureAwait(false)
                    return ravenResult
                })

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            DefensivelyLoad 0 (fun () ->
                task {
                    let stringIds = 
                        try
                            ids |> Array.map (fun id -> id.ToString())
                        with
                        | ex -> raise (InvalidOperationException("Failed to convert ids to string array", ex))
                    use session = store.OpenAsyncSession()
                    let! ravenResult = session.LoadAsync<'T>(stringIds).ConfigureAwait(false)
                    let result = System.Collections.Generic.Dictionary<obj, 'T>()
                    for kvp in ravenResult do
                        result.[kvp.Key :> obj] <- kvp.Value
                    return result
                })

        member this.DeleteAsync(id: obj) =
            DefensivelyStore 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    session.Delete(id)
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                })

        member this.DeleteInUnitOfWorkAsync(ids: obj[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    for id in ids do
                        session.Delete(id)
                    do! session.SaveChangesAsync().ConfigureAwait(false)
                })

    interface INoSqlStore
    interface ISqlStore 