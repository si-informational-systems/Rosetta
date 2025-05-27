namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Exceptions
open SI.Rosetta.Projections

type RavenDbCheckpointWriter(store: IDocumentStore) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? AggregateException as ex when (ex.InnerException :? RavenException) -> true
        | :? RavenException -> true
        | _ -> false

    interface ICheckpointWriter with
        member this.Write(checkpoint: Checkpoint) =
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