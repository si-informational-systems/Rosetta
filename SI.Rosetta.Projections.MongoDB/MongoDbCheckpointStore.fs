namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open System.Threading
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
                            let docType = typeof<Checkpoint>
                            let idProp = docType.GetProperty("Id")
                            let id = if idProp <> null then idProp.GetValue(checkpoint) else null
                            let filter = Builders<Checkpoint>.Filter.Eq("Id", checkpoint.Id)
                            
                            let mutable updateDef = Builders<Checkpoint>.Update.SetOnInsert("Id", id)
                            for prop in checkpoint.GetType().GetProperties() do
                                if prop.Name <> "Id" then
                                    let value = prop.GetValue(checkpoint)
                                    updateDef <- updateDef.Set(prop.Name, value)
                            let options = UpdateOptions(IsUpsert = true)
                            let cancellationToken = CancellationToken.None
                            let! _ = collection.UpdateOneAsync(filter, updateDef, options, cancellationToken).ConfigureAwait(false)
                            return ()
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
                        let filter = Builders<Checkpoint>.Filter.Eq("Id", id)
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