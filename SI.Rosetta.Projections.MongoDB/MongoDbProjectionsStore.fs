namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open System.Threading
open SI.Rosetta.Projections
open MongoDB.Driver
open SI.Rosetta.Common
open System.Runtime.ExceptionServices
open System.Collections.Generic

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
                    let docType = typeof<'T>
                    let idProp = docType.GetProperty("Id")
                    let id = if idProp <> null then idProp.GetValue(doc) else null
                    let filter = Builders<'T>.Filter.Eq("Id", id.ToString())
                    
                    // Build update definition: SetOnInsert for Id, Set for other properties
                    let mutable updateDef = Builders<'T>.Update.SetOnInsert("Id", id)
                    for prop in docType.GetProperties() do
                        if prop.Name <> "Id" then
                            let value = prop.GetValue(doc)
                            updateDef <- updateDef.Set(prop.Name, value)
                    
                    let options = UpdateOptions(IsUpsert = true)
                    let cancellationToken = CancellationToken.None
                    let! _ = collection.UpdateOneAsync(filter, updateDef, options, cancellationToken).ConfigureAwait(false)
                    ()
                })

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use! session = client.StartSessionAsync()
                    let transactionOptions = TransactionOptions(writeConcern = WriteConcern.WMajority)
                    let cancellationToken = CancellationToken.None
                    let transactionFunc = Func<IClientSessionHandle, CancellationToken, Task<unit>>(fun s ct ->
                        task {
                            try
                                let collectionName = PluralizeName(typeof<'T>.Name)
                                let collection = db.GetCollection(collectionName)
                                let docType = typeof<'T>
                                for doc in docs do
                                    let idProp = docType.GetProperty("Id")
                                    let id = if idProp <> null then idProp.GetValue(doc) else null
                                    let filter = Builders<'T>.Filter.Eq("Id", id.ToString())
                    
                                    // Build update definition: SetOnInsert for Id, Set for other properties
                                    let mutable updateDef = Builders<'T>.Update.SetOnInsert("Id", id)
                                    for prop in docType.GetProperties() do
                                        if prop.Name <> "Id" then
                                            let value = prop.GetValue(doc)
                                            updateDef <- updateDef.Set(prop.Name, value)
                    
                                    let options = UpdateOptions(IsUpsert = true)
                                    let cancellationToken = CancellationToken.None
                                    let! _ = collection.UpdateOneAsync(filter, updateDef, options, cancellationToken).ConfigureAwait(false)
                                    ()
                                ()
                            with ex -> ExceptionDispatchInfo.Capture(ex).Throw()
                        })
                    do! session.WithTransactionAsync<unit>(transactionFunc, transactionOptions, cancellationToken)
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
                    let filter = Builders<'T>.Filter.In("Id", stringIds)
                    let! mongoResult =
                        task {
                            try
                                let! docs = collection.Find(filter).ToListAsync().ConfigureAwait(false)
                                return docs |> Seq.cast<'T> |> Seq.toArray
                            with
                            | :? MongoException -> return Array.empty<'T>
                        }

                    let foundDocs = 
                        mongoResult
                        |> Seq.choose (fun doc ->
                            let docType = doc.GetType()
                            let id =
                                match docType.GetProperty("Id"), docType.GetField("Id") with
                                | (null, null) -> null
                                | (prop, _) when prop <> null -> prop.GetValue(doc)
                                | (_, field) -> field.GetValue(doc)
                            if isNull id then None else Some (id, doc))
                        |> dict

                    // Build result: Every input ID maps to either the doc or null
                    let result = Dictionary<obj, 'T>()
                    for id in ids do
                        match foundDocs.TryGetValue(id) with
                        | true, doc -> result[id] <- doc
                        | false, _ -> result[id] <- Unchecked.defaultof<'T> // null for reference types
            
                    return result
                })

        member this.DeleteAsync(id: obj) =
            DefensivelyStore 0 (fun () ->
                task {
                    let collectionName = PluralizeName(typeof<'T>.Name)
                    let collection = db.GetCollection<'T>(collectionName)
                    let filter = Builders<'T>.Filter.Eq("Id", id.ToString())
                    let! _ = collection.DeleteOneAsync(filter).ConfigureAwait(false)
                    return ()
                })

        member this.DeleteInUnitOfWorkAsync(ids: obj[]) =
            DefensivelyStore 0 (fun () ->
                task {
                    use! session = client.StartSessionAsync()
                    let transactionOptions = TransactionOptions(writeConcern = WriteConcern.WMajority)
                    let cancellationToken = CancellationToken.None
                    let transactionFunc = Func<IClientSessionHandle, CancellationToken, Task<unit>>(fun s ct ->
                        task {
                            try 
                                let stringIds = ids |> Array.map (fun id -> id.ToString())
                                let collectionName = PluralizeName(typeof<'T>.Name)
                                let collection = db.GetCollection<'T>(collectionName)
                                let filter = Builders<'T>.Filter.In("Id", stringIds)
                                let! _ = collection.DeleteManyAsync(s, filter, cancellationToken = ct).ConfigureAwait(false)
                                return ()
                            with ex -> ExceptionDispatchInfo.Capture(ex).Throw()
                        })
                    do! session.WithTransactionAsync<unit>(transactionFunc, transactionOptions, cancellationToken)
                })

    interface INoSqlStore
    interface ISqlStore 