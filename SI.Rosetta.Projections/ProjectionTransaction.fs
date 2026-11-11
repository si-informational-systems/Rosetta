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

/// The read model store's transactions, one per event
type IProjectionsUnitOfWork =
    /// Run for each projection when it starts, before its checkpoint is read, with the read model types it declares
    /// (IProjectInto): it fails when the store can't commit an event atomically (MongoDB without a replica set), or
    /// when a read model's table doesn't match it (PostgreSQL)
    abstract member PrepareAsync: readModels: Type[] -> Task
    abstract member BeginAsync: unit -> Task<IProjectionTransaction>
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
