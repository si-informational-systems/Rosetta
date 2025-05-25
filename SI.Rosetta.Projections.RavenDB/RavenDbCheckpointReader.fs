namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Documents
open Raven.Client.Exceptions
open SI.Rosetta.Projections

type RavenDbCheckpointReader(store: IDocumentStore) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? RavenException -> true
        | _ -> false

    interface ICheckpointReader with
        member this.Read(id: string) =
            let rec TryRead retryCount =
                task {
                    try
                        use session = store.OpenAsyncSession()
                        let! checkpoint = session.LoadAsync<Checkpoint>(id)
                        return 
                            match Option.ofObj checkpoint with
                            | None -> Checkpoint(Id = id, Value = 0UL)
                            | Some cp -> cp
                    with ex ->
                        if not (IsTransient ex) || retryCount >= MaxRetries then
                            raise ex
                        do! Task.Delay(Delay)
                        return! TryRead (retryCount + 1)
                }
            TryRead 0 