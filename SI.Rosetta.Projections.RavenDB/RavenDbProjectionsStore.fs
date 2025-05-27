namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Exceptions
open SI.Rosetta.Projections

type RavenDbProjectionsStore(store: IDocumentStore) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let rec DefensivelyLoad (retryCount: int) (operation: unit -> Task<'T>) =
        task {
            try
                return! operation()
            with ex ->
                if not (RavenDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay)
                return! DefensivelyLoad (retryCount + 1) operation
        }

    let rec DefensivelyStore (retryCount: int) (operation: unit -> Task) =
        task {
            try
                do! operation()
            with ex ->
                if not (RavenDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay)
                do! DefensivelyStore (retryCount + 1) operation
        }

    static member private IsTransient (ex: Exception) =
        match ex with
        | :? AggregateException as ex when (ex.InnerException :? RavenException) -> true
        | :? RavenException -> true
        | _ -> false

    interface IProjectionsStore with
        member this.StoreAsync(doc: 'T) =
            DefensivelyStore 0 (fun () -> 
                task {
                    use session = store.OpenAsyncSession()
                    do! session.StoreAsync(doc)
                    do! session.SaveChangesAsync()
                })

        member this.StoreInUnitOfWorkAsync(docs: 'T[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    for doc in docs do
                        do! session.StoreAsync(doc)
                    do! session.SaveChangesAsync()
                })

        member this.LoadAsync(ids: obj[]) =
            DefensivelyLoad 0 (fun () ->
                task {
                    let stringIds = 
                            try
                                ids |> Array.map (fun id -> id.ToString())
                            with
                            | ex -> raise (InvalidOperationException("Failed to convert ids to string array", ex))
                    use session = store.OpenAsyncSession()
                    let! ravenResult = session.LoadAsync<'T>(stringIds)
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
                    do! session.SaveChangesAsync()
                })

        member this.DeleteInUnitOfWorkAsync(ids: obj[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use session = store.OpenAsyncSession()
                    for id in ids do
                        session.Delete(id)
                    do! session.SaveChangesAsync()
                })

    interface INoSqlStore
    interface ISqlStore 