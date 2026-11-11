namespace SI.Rosetta.Projections.PostgreSQL

open System
open System.Net.Sockets
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections

/// Rosetta's own tables, in the rosetta schema: the projections' checkpoints
module internal RosettaTables =
    let private Create = """
create schema if not exists rosetta;
create table if not exists rosetta.checkpoints (
    id text primary key,
    value bigint not null,
    version int not null default 0,
    updated_at timestamptz not null default now());
"""

    let EnsureAsync (connection: NpgsqlConnection) : Task =
        task {
            use command = new NpgsqlCommand(Create, connection)
            try
                let! _ = command.ExecuteNonQueryAsync().ConfigureAwait(false)
                ()
            // IF NOT EXISTS still fails when another connection creates them at the same moment
            with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation || ex.SqlState = PostgresErrorCodes.DuplicateObject -> ()
        } :> Task

    let private StoreCheckpointSql =
        """insert into rosetta.checkpoints (id, value, version, updated_at) values ($1, $2, $3, now())
           on conflict (id) do update set value = excluded.value, version = excluded.version, updated_at = now()"""

    let private CheckpointParameters (checkpoint: Checkpoint) : NpgsqlParameter[] =
        [| NpgsqlParameter(Value = checkpoint.Id)
           NpgsqlParameter(Value = int64 checkpoint.Value, NpgsqlDbType = NpgsqlDbType.Bigint)
           NpgsqlParameter(Value = checkpoint.Version, NpgsqlDbType = NpgsqlDbType.Integer) |]

    /// The checkpoint's upsert, for an event's batch
    let CheckpointCommand (checkpoint: Checkpoint) =
        let command = NpgsqlBatchCommand(StoreCheckpointSql)
        command.Parameters.AddRange(CheckpointParameters checkpoint)
        command

    /// Writes the checkpoint on its own
    let StoreCheckpointAsync (connection: NpgsqlConnection) (checkpoint: Checkpoint) : Task =
        task {
            use command = new NpgsqlCommand(StoreCheckpointSql, connection)
            command.Parameters.AddRange(CheckpointParameters checkpoint)
            let! _ = command.ExecuteNonQueryAsync().ConfigureAwait(false)
            ()
        } :> Task

    /// While PostgreSQL boots, isn't reachable yet, or the connection drops
    let IsTransient (ex: exn) =
        match ex with
        | :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.CannotConnectNow -> true
        | :? NpgsqlException as ex -> ex.IsTransient || (ex.InnerException :? SocketException)
        | :? SocketException -> true
        | _ -> false

    let private MaxRetries = 3
    let private RetryDelay = TimeSpan.FromMilliseconds(100.0)

    /// A small retry policy for transient errors, outside a projection's transaction
    let rec Retry (operation: unit -> Task<'T>) : Task<'T> =
        let rec attempt retries =
            task {
                try
                    return! operation().ConfigureAwait(false)
                with ex when IsTransient ex && retries < MaxRetries ->
                    do! Task.Delay(RetryDelay).ConfigureAwait(false)
                    return! attempt (retries + 1)
            }
        attempt 0

/// The checkpoints, in rosetta.checkpoints
type PostgreSqlCheckpointStore(database: PostgreSqlDatabase) =
    interface ICheckpointStore with
        member this.StoreAsync(checkpoint: Checkpoint) =
            task {
                use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                do! RosettaTables.EnsureAsync connection
                do! RosettaTables.StoreCheckpointAsync connection checkpoint
            } :> Task

        member this.ReadAsync(id: string) =
            task {
                use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                use command = new NpgsqlCommand("select value, version from rosetta.checkpoints where id = $1", connection)
                command.Parameters.Add(NpgsqlParameter(Value = id)) |> ignore
                try
                    use! reader = command.ExecuteReaderAsync().ConfigureAwait(false)
                    let! found = reader.ReadAsync().ConfigureAwait(false)
                    return
                        if found then Checkpoint(Id = id, Value = uint64 (reader.GetInt64 0), Version = reader.GetInt32 1)
                        else Checkpoint(Id = id, Value = 0UL)
                // Before the projections first started: nothing is checkpointed yet
                with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UndefinedTable ->
                    return Checkpoint(Id = id, Value = 0UL)
            }
