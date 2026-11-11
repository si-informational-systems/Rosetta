namespace SI.Rosetta.Projections.PostgreSQL

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Npgsql
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections

/// A read model's table doesn't match it (missing, a column missing, no unique key): the app's schema hasn't been
/// applied yet, or is wrong. Not an InvalidOperationException, so a projection that starts before the app's migrations
/// have run retries until they have
type ReadModelTableException(message: string) =
    inherit Exception(message)

/// An event's transaction in PostgreSQL: a connection and a transaction. The handlers' writes wait in a batch, sent
/// before anything is read and with the checkpoint at the commit, so an event costs few round trips. Disposed without
/// committing, it's rolled back
type PostgreSqlProjectionTransaction(connection: NpgsqlConnection, transaction: NpgsqlTransaction) =
    let Pending = List<NpgsqlBatchCommand>()

    member this.Connection = connection
    member this.Transaction = transaction

    member this.Enqueue(command: NpgsqlBatchCommand) = Pending.Add command

    /// Sends the writes waiting, so what's read next sees them
    member this.FlushAsync() : Task =
        task {
            if Pending.Count > 0 then
                use batch = new NpgsqlBatch(connection, transaction)
                for command in Pending do
                    batch.BatchCommands.Add command
                Pending.Clear()
                let! _ = batch.ExecuteNonQueryAsync().ConfigureAwait(false)
                ()
        }

    interface IProjectionTransaction with
        member this.CommitAsync(checkpoint: Checkpoint) =
            task {
                Pending.Add(RosettaTables.CheckpointCommand checkpoint)
                do! this.FlushAsync()
                do! transaction.CommitAsync().ConfigureAwait(false)
            } :> Task

        member this.DisposeAsync() =
            ValueTask(task {
                try
                    do! transaction.DisposeAsync().ConfigureAwait(false)
                finally
                    connection.Dispose()
            })

/// A PostgreSQL transaction per event. When a projection starts, it creates a missing database and Rosetta's
/// checkpoints table (retrying while PostgreSQL boots), and checks the projection's read model tables. The tables are
/// the app's: Rosetta doesn't create or change them
type PostgreSqlProjectionsUnitOfWork(database: PostgreSqlDatabase) =
    let Gate = new SemaphoreSlim(1, 1)
    let mutable Prepared = false
    // 1, 2, 4, 8, 16, 30 s: about a minute for PostgreSQL to come up
    let PrepareDelays = [ 1.0; 2.0; 4.0; 8.0; 16.0; 30.0 ] |> List.map TimeSpan.FromSeconds

    let PrepareOnceAsync () =
        task {
            do! database.EnsureCreatedAsync().ConfigureAwait(false)
            use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
            do! RosettaTables.EnsureAsync connection
        }

    /// The values of the first column the query returns, for the table's schema and name ($1, $2)
    static member private ReadAllAsync<'T>(connection: NpgsqlConnection, sql: string, table: PostgreSqlTable) : Task<List<'T>> =
        task {
            use query = new NpgsqlCommand(sql, connection)
            query.Parameters.Add(NpgsqlParameter(Value = table.Schema)) |> ignore
            query.Parameters.Add(NpgsqlParameter(Value = table.Name)) |> ignore
            let values = List<'T>()
            use! reader = query.ExecuteReaderAsync().ConfigureAwait(false)
            let mutable reading = true
            while reading do
                let! read = reader.ReadAsync().ConfigureAwait(false)
                if read then values.Add(reader.GetFieldValue<'T> 0) else reading <- false
            return values
        }

    /// The problem with the table, if any: missing, a column missing, or no unique key to upsert on
    static member private Problem(table: PostgreSqlTable, columns: HashSet<string>, uniques: List<string[]>) : string option =
        let where = sprintf "%s (for %s)" table.Qualified table.Type.Name
        let missing = table.Columns |> Array.filter (fun column -> not (columns.Contains column.Name))
        let key = table.Keys |> Array.map (fun key -> key.Name) |> Array.sort
        if columns.Count = 0 then
            Some (sprintf "The table %s doesn't exist: create it before the projections start." where)
        elif missing.Length > 0 then
            Some (sprintf "The table %s has no column %s." where
                    (String.Join(", ", missing |> Array.map (fun column -> sprintf "%s (%s)" column.Name column.Property.Name))))
        elif not (uniques |> Seq.exists (fun unique -> Array.sort unique = key)) then
            Some (sprintf "The table %s has no primary key or unique index on (%s): rows are upserted on their key." where (String.Join(", ", key)))
        else None

    /// Fails with ReadModelTableException when the read model's table doesn't match it
    static member private CheckAsync(connection: NpgsqlConnection, table: PostgreSqlTable) : Task =
        task {
            let! columns =
                PostgreSqlProjectionsUnitOfWork.ReadAllAsync<string>(
                    connection, "select column_name::text from information_schema.columns where table_schema = $1 and table_name = $2", table)
            let! uniques =
                PostgreSqlProjectionsUnitOfWork.ReadAllAsync<string[]>(
                    connection,
                    """select array_agg(a.attname::text order by a.attname) from pg_index i
                       join pg_class c on c.oid = i.indrelid
                       join pg_namespace n on n.oid = c.relnamespace
                       join pg_attribute a on a.attrelid = c.oid and a.attnum = any(i.indkey)
                       where n.nspname = $1 and c.relname = $2 and i.indisunique
                       group by i.indexrelid""",
                    table)
            match PostgreSqlProjectionsUnitOfWork.Problem(table, HashSet<string>(columns), uniques) with
            | Some problem -> raise (ReadModelTableException problem)
            | None -> ()
        } :> Task

    interface IProjectionsUnitOfWork with
        member this.PrepareAsync(readModels: Type[]) =
            task {
                do! Gate.WaitAsync().ConfigureAwait(false)
                try
                    let mutable delays = PrepareDelays
                    while not Prepared do
                        try
                            do! PrepareOnceAsync ()
                            Prepared <- true
                        with ex when RosettaTables.IsTransient ex && not delays.IsEmpty ->
                            do! Task.Delay(delays.Head).ConfigureAwait(false)
                            delays <- delays.Tail
                finally
                    Gate.Release() |> ignore
                if readModels.Length > 0 then
                    use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                    for readModel in readModels do
                        do! PostgreSqlProjectionsUnitOfWork.CheckAsync(connection, PostgreSqlTables.Of database.Schema readModel)
            } :> Task

        member this.BeginAsync() =
            task {
                let! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                try
                    let! transaction = connection.BeginTransactionAsync().ConfigureAwait(false)
                    return new PostgreSqlProjectionTransaction(connection, transaction) :> IProjectionTransaction
                with ex ->
                    connection.Dispose()
                    ExceptionDispatchInfo.Throw ex
                    return Unchecked.defaultof<IProjectionTransaction>
            }

        /// Truncates the read models' tables, in one statement
        member this.ClearAsync(readModels: Type[]) =
            task {
                if readModels.Length > 0 then
                    let tables = readModels |> Array.map (fun readModel -> (PostgreSqlTables.Of database.Schema readModel).Qualified)
                    use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                    use truncate = new NpgsqlCommand(sprintf "truncate table %s" (String.Join(", ", tables)), connection)
                    let! _ = truncate.ExecuteNonQueryAsync().ConfigureAwait(false)
                    ()
            } :> Task
