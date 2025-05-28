namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Exceptions
open SI.Rosetta.Projections

type RavenDbCheckpointStore(store: IDocumentStore) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(100.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? RavenException -> true
        | _ -> false

    interface ICheckpointStore with
        member this.StoreAsync(checkpoint: Checkpoint) =
            let rec TryWrite retryCount =
                task {
                    try
                        use session = store.OpenAsyncSession()
                        do! session.StoreAsync(checkpoint).ConfigureAwait(false)
                        do! session.SaveChangesAsync().ConfigureAwait(false)
                    with ex ->
                        if not (IsTransient ex) || retryCount >= MaxRetries then
                            raise ex
                        do! Task.Delay(Delay).ConfigureAwait(false)
                        do! TryWrite (retryCount + 1)
                }
            TryWrite 0 

        member this.ReadAsync(id: string) =
            let rec TryRead retryCount =
                task {
                    try
                        use session = store.OpenAsyncSession()
                        let! checkpoint = session.LoadAsync<Checkpoint>(id).ConfigureAwait(false)
                        return 
                            match Option.ofObj checkpoint with
                            | None -> Checkpoint(Id = id, Value = 0UL)
                            | Some cp -> cp
                    with ex ->
                        if not (IsTransient ex) || retryCount >= MaxRetries then
                            raise ex
                        do! Task.Delay(Delay).ConfigureAwait(false)
                        return! TryRead (retryCount + 1)
                }
            TryRead 0 