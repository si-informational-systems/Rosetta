namespace SI.Rosetta.Projections.MongoDB

open System
open System.Threading
open System.Threading.Tasks
open MongoDB.Bson
open MongoDB.Driver
open SI.Rosetta.Common
open SI.Rosetta.Projections

/// An event's transaction in MongoDB: a session with a transaction, committed with the checkpoint. Disposed without
/// committing, the transaction is aborted
type MongoDbProjectionTransaction(session: IClientSessionHandle, db: IMongoDatabase) =
    let MaxCommitAttempts = 3

    member this.Session = session

    interface IProjectionTransaction with
        member this.CommitAsync(checkpoint: Checkpoint) =
            task {
                do! MongoDbDocuments.UpsertAsync<Checkpoint>(db, session, checkpoint, CancellationToken.None).ConfigureAwait(false)
                // A commit whose outcome is unknown can be sent again: MongoDB commits a transaction once
                let mutable attempt = 1
                let mutable committed = false
                while not committed do
                    try
                        do! session.CommitTransactionAsync().ConfigureAwait(false)
                        committed <- true
                    with :? MongoException as ex when ex.HasErrorLabel "UnknownTransactionCommitResult" && attempt < MaxCommitAttempts ->
                        attempt <- attempt + 1
            } :> Task

        member this.DisposeAsync() =
            ValueTask(task {
                try
                    if session.IsInTransaction then
                        do! session.AbortTransactionAsync().ConfigureAwait(false)
                finally
                    session.Dispose()
            })

/// A MongoDB transaction per event, which needs a replica set (a single-node one is enough) or a sharded cluster
type MongoDbProjectionsUnitOfWork(client: IMongoClient, db: IMongoDatabase) =
    let mutable Prepared = false

    static member val internal StandaloneMessage =
        "MongoDB projections need a replica set: each event's read models and its checkpoint are written in one transaction, " +
        "and a standalone MongoDB server has no transactions. A single-node replica set is enough: start mongod with " +
        "--replSet rs0, run rs.initiate() once, and connect with replicaSet=rs0 (or directConnection=true)."

    /// A write conflict with another transaction: MongoDB labels it TransientTransactionError, to retry the transaction
    interface IRecognizeConflicts with
        member this.IsConflict(ex) =
            match ex with
            | :? MongoException as mongo -> mongo.HasErrorLabel "TransientTransactionError"
            | _ -> false

    interface IProjectionsUnitOfWork with
        member this.PrepareAsync(projectionType, _readModels) =
            task {
                ClusterWideTransaction.Refuse("MongoDB", projectionType)
                if not Prepared then
                    let! hello = db.RunCommandAsync<BsonDocument>(BsonDocumentCommand<BsonDocument>(BsonDocument("hello", BsonInt32 1))).ConfigureAwait(false)
                    // A replica set member names its set; mongos (a sharded cluster) says isdbgrid
                    let isReplicaSet = hello.Contains "setName"
                    let isMongos = hello.Contains "msg" && hello.["msg"] = BsonString "isdbgrid"
                    if not (isReplicaSet || isMongos) then
                        raise (InvalidOperationException(MongoDbProjectionsUnitOfWork.StandaloneMessage))
                    Prepared <- true
            } :> Task

        member this.BeginAsync(_projectionType) =
            task {
                let! session = client.StartSessionAsync().ConfigureAwait(false)
                session.StartTransaction(TransactionOptions(writeConcern = WriteConcern.WMajority))
                return new MongoDbProjectionTransaction(session, db) :> IProjectionTransaction
            }

        /// Each read model's collection: the pluralized type name, as the store names it
        member this.ClearAsync(readModels: Type[]) =
            task {
                for readModel in readModels do
                    let collection = db.GetCollection<BsonDocument>(PluralizeName readModel.Name)
                    let! _ = collection.DeleteManyAsync(FilterDefinition<BsonDocument>.Empty).ConfigureAwait(false)
                    ()
            } :> Task
