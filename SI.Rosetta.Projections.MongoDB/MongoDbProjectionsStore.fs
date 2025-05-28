namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open System.Threading
open SI.Rosetta.Projections
open MongoDB.Driver
open SI.Rosetta.Common

type MongoDbProjectionsStore(client: IMongoClient, db: IMongoDatabase) =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(50.0)

    let rec DefensivelyLoad (retryCount: int) (operation: unit -> Task<'T>) =
        task {
            try
                return! operation().ConfigureAwait(false)
            with ex ->
                if not (MongoDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay).ConfigureAwait(false)
                return! DefensivelyLoad (retryCount + 1) operation
        }

    let rec DefensivelyStore (retryCount: int) (operation: unit -> Task) =
        task {
            try
                do! operation().ConfigureAwait(false)
            with ex ->
                if not (MongoDbProjectionsStore.IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay).ConfigureAwait(false)
                do! DefensivelyStore (retryCount + 1) operation
        }

    static member private IsTransient (ex: Exception) =
        match ex with
        | :? AggregateException as ex when (ex.InnerException :? MongoException) -> true
        | :? MongoException -> true
        | _ -> false

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) =
            DefensivelyStore 0 (fun () -> 
                task {
                    let collectionName = PluralizeName(typeof<'T>.Name)
                    let collection = db.GetCollection(collectionName)
                    do! collection.InsertOneAsync(doc).ConfigureAwait(false)
                })

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use! session = client.StartSessionAsync()
                    let transactionOptions = TransactionOptions(writeConcern = WriteConcern.WMajority)
                    let cancellationToken = CancellationToken.None
                    do! session.WithTransactionAsync(
                        (fun s ct ->
                            task {
                                let collectionName = PluralizeName(typeof<'T>.Name)
                                let collection = db.GetCollection(collectionName)
                                for doc in docs do
                                    do! collection.InsertOneAsync(s, doc, cancellationToken = ct).ConfigureAwait(false)
                            } :> Task),
                        transactionOptions,
                        cancellationToken)
                })

        member this.LoadAsync<'T when 'T : not struct>(ids: obj[]) =
            DefensivelyLoad 0 (fun () ->
                task {
                    let stringIds = 
                            try
                                ids |> Array.map (fun id -> id.ToString())
                            with
                            | ex -> raise (InvalidOperationException("Failed to convert ids to string array", ex))
                    let collectionName = PluralizeName(typeof<'T>.Name)
                    let collection = db.GetCollection<'T>(collectionName)
                    let filter = Builders<'T>.Filter.In("_id", stringIds)
                    let! mongoResult = collection.Find(filter).ToListAsync().ConfigureAwait(false)
                    let result = System.Collections.Generic.Dictionary<obj, 'T>()
                    for doc in mongoResult do
                        let docType = doc.GetType()
                        let idProperty = docType.GetProperty("Id")
                        let idField = docType.GetField("Id")
                        let id = 
                            if idProperty <> null then idProperty.GetValue(doc)
                            elif idField <> null then idField.GetValue(doc)
                            else null
                        if id <> null then
                            result.[id] <- doc
                    return result
                })

        member this.DeleteAsync(id: obj) =
            DefensivelyStore 0 (fun () ->
                task {
                    let collectionName = PluralizeName(typeof<'T>.Name)
                    let collection = db.GetCollection<'T>(collectionName)
                    let filter = Builders<'T>.Filter.Eq("_id", id.ToString())
                    let! _ = collection.DeleteOneAsync(filter).ConfigureAwait(false)
                    return ()
                })

        member this.DeleteInUnitOfWorkAsync(ids: obj[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use! session = client.StartSessionAsync()
                    let transactionOptions = TransactionOptions(writeConcern = WriteConcern.WMajority)
                    let cancellationToken = CancellationToken.None
                    do! session.WithTransactionAsync(
                        (fun s ct ->
                            task {
                                let stringIds = ids |> Array.map (fun id -> id.ToString())
                                let collectionName = PluralizeName(typeof<'T>.Name)
                                let collection = db.GetCollection<'T>(collectionName)
                                let filter = Builders<'T>.Filter.In("_id", stringIds)
                                let! _ = collection.DeleteManyAsync(s, filter, cancellationToken = ct).ConfigureAwait(false)
                            } :> Task),
                        transactionOptions,
                        cancellationToken)
                })

    interface INoSqlStore
    interface ISqlStore 