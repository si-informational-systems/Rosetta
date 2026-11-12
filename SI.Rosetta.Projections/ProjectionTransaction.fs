namespace SI.Rosetta.Projections

open System
open System.Threading
open System.Threading.Tasks

/// One event's transaction in the read model store, begun before the event's handlers run. Committing writes the
/// event's checkpoint in it too, so an event's read models and its checkpoint are written together or not at all.
/// Disposing it without committing rolls it back
type IProjectionTransaction =
    inherit IAsyncDisposable
    abstract member CommitAsync: checkpoint: Checkpoint -> Task

/// A unit of work whose store has conflicts of its own (a MongoDB write conflict, a PostgreSQL deadlock, a RavenDB
/// ConcurrencyException): the event's transaction is retried at once on them (ConflictRetries), as on Rosetta's
/// ConcurrencyException
type IRecognizeConflicts =
    /// Whether the exception (one of a failure's causes) is a conflict that trying again can resolve
    abstract member IsConflict: ex: exn -> bool

/// The read model store's transactions, one per event
type IProjectionsUnitOfWork =
    /// Run for each projection when it starts, before its checkpoint is read, with its type and the read model types it
    /// declares (IProjectInto): it fails when the store can't commit an event atomically (MongoDB without a replica set),
    /// when a read model's table doesn't match it (PostgreSQL), or when the projection asks for a transaction the store
    /// doesn't have ([<ClusterWideTransaction>] outside RavenDB)
    abstract member PrepareAsync: projectionType: Type * readModels: Type[] -> Task
    /// Begins an event's transaction for the projection type, whose attributes may choose its kind
    /// ([<ClusterWideTransaction>], for every event or the ones it names), null for a projection created without one,
    /// and the event's record type (the type its DU case wraps)
    abstract member BeginAsync: projectionType: Type * eventType: Type -> Task<IProjectionTransaction>
    /// Empties the read models' tables (PostgreSQL) or collections (RavenDB, MongoDB), for a projection that's rebuilt
    abstract member ClearAsync: readModels: Type[] -> Task

/// The transaction of the event being handled. A store writes and reads through it while there is one,
/// and on its own outside a Projection's handlers
[<AbstractClass; Sealed>]
type ProjectionTransaction =
    static let Ambient = AsyncLocal<IProjectionTransaction>()

    /// The event's transaction, or null outside a Projection's handlers
    static member Current = Ambient.Value

    /// Runs the handlers with the transaction as Current
    static member internal Within(transaction: IProjectionTransaction, run: unit -> Task) : Task =
        task {
            Ambient.Value <- transaction
            try
                do! run ()
            finally
                Ambient.Value <- null
        }
