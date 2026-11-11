namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading.Tasks
open System.Threading
open SI.Rosetta.Projections
open MongoDB.Driver
open System.Runtime.ExceptionServices
open System.Collections.Generic

/// While a Projection handles an event, every call runs in the event's transaction (MongoDbProjectionTransaction),
/// committed with the checkpoint. Outside one, every call writes straight away
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

    /// The event's session, or null outside a Projection's handlers
    static member private Session =
        match ProjectionTransaction.Current with
        | :? MongoDbProjectionTransaction as transaction -> transaction.Session
        | _ -> null

    static member private Load<'T>(db: IMongoDatabase, session: IClientSessionHandle, id: obj) : Task<'T> =
        task {
            let stringId =
                try
                    id.ToString()
                with
                | ex -> raise (InvalidOperationException("Failed to convert id to string", ex))
            let collection = MongoDbDocuments.Collection<'T> db
            let filter = Builders<'T>.Filter.Eq("Id", stringId)
            // A missing document is null. Errors propagate (and transient ones are retried), so they're never taken for "not found"
            let found = if isNull session then collection.Find(filter) else collection.Find(session, filter)
            return! found.FirstOrDefaultAsync().ConfigureAwait(false)
        }

    static member private LoadMany<'T>(db: IMongoDatabase, session: IClientSessionHandle, ids: obj[]) : Task<Dictionary<obj, 'T>> =
        task {
            let stringIds =
                    try
                        ids |> Array.map (fun id -> id.ToString())
                    with
                    | ex -> raise (InvalidOperationException("Failed to convert ids to string array", ex))
            let collection = MongoDbDocuments.Collection<'T> db
            let filter = Builders<'T>.Filter.In("Id", stringIds)
            let found = if isNull session then collection.Find(filter) else collection.Find(session, filter)
            let! mongoResult = found.ToListAsync().ConfigureAwait(false)

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
        }

    /// Runs the writes in the event's transaction, or in one of their own outside a Projection's handlers
    member private this.InTransaction(writes: IClientSessionHandle -> CancellationToken -> Task) : Task =
        match MongoDbProjectionsStore.Session with
        | null ->
            DefensivelyStore 0 (fun () ->
                task {
                    use! session = client.StartSessionAsync()
                    let transactionOptions = TransactionOptions(writeConcern = WriteConcern.WMajority)
                    let transactionFunc = Func<IClientSessionHandle, CancellationToken, Task<unit>>(fun s ct ->
                        task {
                            try
                                do! writes s ct
                            with ex -> ExceptionDispatchInfo.Capture(ex).Throw()
                        })
                    do! session.WithTransactionAsync<unit>(transactionFunc, transactionOptions, CancellationToken.None)
                }) :> Task
        | session -> writes session CancellationToken.None

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(doc: 'T) =
            match MongoDbProjectionsStore.Session with
            | null -> DefensivelyStore 0 (fun () -> MongoDbDocuments.UpsertAsync<'T>(db, null, doc, CancellationToken.None)) :> Task
            | session -> MongoDbDocuments.UpsertAsync<'T>(db, session, doc, CancellationToken.None)

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(docs: 'T[]) =
            this.InTransaction(fun s ct ->
                task {
                    for doc in docs do
                        do! MongoDbDocuments.UpsertAsync<'T>(db, s, doc, ct).ConfigureAwait(false)
                })

        member this.LoadAsync<'T when 'T : not struct>(id: obj) =
            match MongoDbProjectionsStore.Session with
            | null -> DefensivelyLoad 0 (fun () -> MongoDbProjectionsStore.Load<'T>(db, null, id))
            | session -> MongoDbProjectionsStore.Load<'T>(db, session, id)

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            match MongoDbProjectionsStore.Session with
            | null -> DefensivelyLoad 0 (fun () -> MongoDbProjectionsStore.LoadMany<'T>(db, null, ids))
            | session -> MongoDbProjectionsStore.LoadMany<'T>(db, session, ids)

        member this.DeleteAsync<'T when 'T : not struct>(id: obj) =
            match MongoDbProjectionsStore.Session with
            | null -> DefensivelyStore 0 (fun () -> MongoDbDocuments.DeleteAsync<'T>(db, null, id.ToString(), CancellationToken.None)) :> Task
            | session -> MongoDbDocuments.DeleteAsync<'T>(db, session, id.ToString(), CancellationToken.None)

        member this.DeleteInUnitOfWorkAsync<'T when 'T : not struct>(ids: obj[]) =
            this.InTransaction(fun s ct ->
                task {
                    let stringIds = ids |> Array.map (fun id -> id.ToString())
                    let filter = Builders<'T>.Filter.In("Id", stringIds)
                    let! _ = (MongoDbDocuments.Collection<'T> db).DeleteManyAsync(s, filter, cancellationToken = ct).ConfigureAwait(false)
                    ()
                })

    interface INoSqlStore
