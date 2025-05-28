namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open MongoDB.Driver
open SI.Rosetta.Projections

type MongoDbCheckpointStore(store: IMongoClient) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? MongoException -> true
        | _ -> false

    interface ICheckpointStore with
        member this.WriteAsync(checkpoint: Checkpoint) =
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
                        use! session = store.StartSessionAsync()
                        let client = session.Client
                        let! checkpoint = client.
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