namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open MongoDB.Driver
open SI.Rosetta.Projections
open SI.Rosetta.Common

type MongoDbCheckpointStore(db: IMongoDatabase) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? MongoException -> true
        | _ -> false

    interface ICheckpointStore with
        member this.StoreAsync(checkpoint: Checkpoint) =
                let rec TryWrite retryCount =
                    task {
                        try
                            let collectionName = PluralizeName(checkpoint.GetType().Name)
                            let collection = db.GetCollection(collectionName)
                            do! collection.InsertOneAsync(checkpoint).ConfigureAwait(false)
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
                        let collectionName = PluralizeName(typeof<Checkpoint>.Name)
                        let collection = db.GetCollection(collectionName)
                        let filter = Builders<Checkpoint>.Filter.Eq("_id", id)
                        let! checkpoint = collection.Find(filter).FirstOrDefaultAsync().ConfigureAwait(false)
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