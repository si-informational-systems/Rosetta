namespace SI.Rosetta.Projections.PostgreSQL

open System.Collections.Generic
open System.Threading.Tasks
open Npgsql
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections

/// ISqlStore on PostgreSQL: read models as rows of the app's tables (see PostgreSqlTables for the mapping), stored,
/// loaded and deleted by key. While a Projection handles an event, every call runs in the event's transaction
/// (PostgreSqlProjectionTransaction), its writes batched; outside one, each call runs on its own
type PostgreSqlStore(database: PostgreSqlDatabase) =

    member private this.TableOf<'T>() = PostgreSqlTables.Of database.Schema typeof<'T>

    /// The event's transaction, or None outside a Projection's handlers
    static member private Transaction =
        match ProjectionTransaction.Current with
        | :? PostgreSqlProjectionTransaction as transaction -> Some transaction
        | _ -> None

    /// Runs the writes: in the event's transaction (batched), or now, in a transaction of their own. Each attempt
    /// makes its commands, as a command belongs to one batch
    member private this.Write(commands: unit -> NpgsqlBatchCommand list) : Task =
        match PostgreSqlStore.Transaction with
        | None ->
            RosettaTables.Retry (fun () ->
                task {
                    use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                    use! transaction = connection.BeginTransactionAsync().ConfigureAwait(false)
                    use batch = new NpgsqlBatch(connection, transaction)
                    for command in commands () do
                        batch.BatchCommands.Add command
                    let! _ = batch.ExecuteNonQueryAsync().ConfigureAwait(false)
                    do! transaction.CommitAsync().ConfigureAwait(false)
                }) :> Task
        | Some transaction ->
            for command in commands () do
                transaction.Enqueue command
            Task.CompletedTask

    /// The rows with these keys, by key text
    member private this.Read<'T>(ids: obj[]) : Task<Dictionary<string, 'T>> =
        let table = this.TableOf<'T>()
        let read (connection: NpgsqlConnection) (transaction: NpgsqlTransaction) =
            task {
                let found = Dictionary<string, 'T>()
                if ids.Length > 0 then
                    use select = PostgreSqlTables.Select table connection transaction ids
                    use! reader = select.ExecuteReaderAsync().ConfigureAwait(false)
                    let mutable reading = true
                    while reading do
                        let! row = reader.ReadAsync().ConfigureAwait(false)
                        if row then
                            let readModel = PostgreSqlTables.ReadRow table reader
                            found.[PostgreSqlTables.KeyText table (ProjectionKeys.KeyOf readModel)] <- readModel :?> 'T
                        else reading <- false
                return found
            }
        match PostgreSqlStore.Transaction with
        | None ->
            RosettaTables.Retry (fun () ->
                task {
                    use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                    return! read connection null
                })
        | Some transaction ->
            task {
                // What the event wrote so far is read back
                do! transaction.FlushAsync().ConfigureAwait(false)
                return! read transaction.Connection transaction.Transaction
            }

    interface IProjectionsStore with
        member this.StoreAsync<'T when 'T : not struct>(row: 'T) =
            this.Write(fun () -> [ PostgreSqlTables.Upsert (this.TableOf<'T>()) (box row) ])

        member this.StoreInUnitOfWorkAsync<'T when 'T : not struct>(rows: 'T[]) =
            let table = this.TableOf<'T>()
            this.Write(fun () -> [ for row in rows -> PostgreSqlTables.Upsert table (box row) ])

        member this.LoadAsync<'T when 'T : not struct>(id: obj) =
            task {
                let! found = this.Read<'T> [| id |]
                return
                    match found.TryGetValue(PostgreSqlTables.KeyText (this.TableOf<'T>()) (ProjectionKeys.ValuesOf id)) with
                    | true, row -> row
                    | false, _ -> Unchecked.defaultof<'T>
            }

        member this.LoadManyAsync<'T when 'T : not struct>(ids: obj[]) =
            task {
                let table = this.TableOf<'T>()
                let! found = this.Read<'T> ids
                let result = Dictionary<obj, 'T>()
                for id in ids do
                    result.[id] <-
                        match found.TryGetValue(PostgreSqlTables.KeyText table (ProjectionKeys.ValuesOf id)) with
                        | true, row -> row
                        | false, _ -> Unchecked.defaultof<'T>
                return result
            }

        member this.DeleteAsync<'T when 'T : not struct>(id: obj) =
            this.Write(fun () -> [ PostgreSqlTables.Delete (this.TableOf<'T>()) [| id |] ])

        member this.DeleteInUnitOfWorkAsync<'T when 'T : not struct>(ids: obj[]) =
            if ids.Length = 0 then Task.CompletedTask
            else this.Write(fun () -> [ PostgreSqlTables.Delete (this.TableOf<'T>()) ids ])

    interface ISqlStore
