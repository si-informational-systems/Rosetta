namespace SI.Rosetta.Aggregates.PostgreSQL

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open NpgsqlTypes
open SI.Rosetta.Aggregates
open SI.Rosetta.Persistence.PostgreSQL

/// Records the events in Rosetta's own table rosetta.{CollectionName in snake_case} (rosetta.outbox unless set), which
/// it creates on first use, one row per event, skipping the ones it already holds (by id). Data is the event's record
/// as JSON, exactly as KurrentDB stores it (a json column, which keeps the text as it is). With a TTL, each row carries
/// its expires_at, and the outbox deletes the expired rows after recording events, at most every ExpirationFrequency:
/// PostgreSQL has no TTL of its own
type PostgreSqlOutbox(database: PostgreSqlDatabase, options: OutboxOptions, logger: ILogger<PostgreSqlOutbox>) =
    let TableName = PostgreSqlTables.SnakeCase options.CollectionName
    let QualifiedTable = "rosetta." + PostgreSqlTable.Quote TableName
    let EventTTL = Option.ofNullable options.EventTTL
    let ExpirationFrequency = options.ExpirationFrequency
    let Creating = new SemaphoreSlim(1, 1)
    let mutable Created = false
    // Ticks of the last deletion of the expired events; 0 before the first
    let mutable LastExpiration = 0L

    let Create =
        String.concat "\n" [
            "create schema if not exists rosetta;"
            sprintf "create table if not exists %s (" QualifiedTable
            "    id uuid primary key,"
            "    aggregate text not null,"
            "    aggregate_id text not null,"
            "    version bigint not null,"
            "    position int not null,"
            "    event_type text not null,"
            "    data json not null,"
            "    recorded_at timestamptz not null,"
            "    expires_at timestamptz,"
            "    trace_parent text,"
            "    trace_state text);"
            sprintf "create index if not exists %s on %s (expires_at) where expires_at is not null;" (PostgreSqlTable.Quote(TableName + "_expires_at")) QualifiedTable ]

    let Nullable (value: string) = if isNull value then box DBNull.Value else box value

    /// Without a logger: a failed deletion of the expired events goes unreported
    new(database: PostgreSqlDatabase, options: OutboxOptions) = PostgreSqlOutbox(database, options, NullLogger<PostgreSqlOutbox>.Instance)

    /// The outbox's table, schema-qualified and quoted
    member this.Table = QualifiedTable

    /// Creates the database when it's missing, and the outbox's table, once: before the first events are recorded, not
    /// in a transaction of the aggregate's, which a concurrent creation's error would abort
    member this.EnsureCreatedAsync() : Task =
        task {
            if not Created then
                do! Creating.WaitAsync().ConfigureAwait(false)
                try
                    if not Created then
                        do! database.EnsureCreatedAsync().ConfigureAwait(false)
                        use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                        use command = new NpgsqlCommand(Create, connection)
                        try
                            let! _ = command.ExecuteNonQueryAsync().ConfigureAwait(false)
                            ()
                        // IF NOT EXISTS still fails when another process creates them at the same moment
                        with :? PostgresException as ex when ex.SqlState = PostgresErrorCodes.UniqueViolation || ex.SqlState = PostgresErrorCodes.DuplicateObject -> ()
                        Created <- true
                finally
                    Creating.Release() |> ignore
        } :> Task

    /// Inserts the events the outbox doesn't hold yet (on conflict (id) do nothing), after EnsureCreatedAsync. The
    /// command's RecordsAffected is how many it recorded
    member this.Insert(events: OutboxEvent[]) : NpgsqlBatchCommand =
        let command = NpgsqlBatchCommand()
        let rows =
            events |> Array.mapi (fun i event ->
                let values : NpgsqlParameter[] =
                    [| NpgsqlParameter(Value = event.Id, NpgsqlDbType = NpgsqlDbType.Uuid)
                       NpgsqlParameter(Value = event.Aggregate)
                       NpgsqlParameter(Value = event.AggregateId)
                       NpgsqlParameter(Value = event.Version, NpgsqlDbType = NpgsqlDbType.Bigint)
                       NpgsqlParameter(Value = event.Position, NpgsqlDbType = NpgsqlDbType.Integer)
                       NpgsqlParameter(Value = event.EventType)
                       NpgsqlParameter(Value = event.Data, NpgsqlDbType = NpgsqlDbType.Json)
                       NpgsqlParameter(Value = DateTime.SpecifyKind(event.RecordedAt, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz)
                       NpgsqlParameter(
                           Value = (match EventTTL with
                                    | Some ttl -> box (DateTime.SpecifyKind(event.RecordedAt.Add ttl, DateTimeKind.Utc))
                                    | None -> box DBNull.Value),
                           NpgsqlDbType = NpgsqlDbType.TimestampTz)
                       NpgsqlParameter(Value = Nullable event.TraceParent, NpgsqlDbType = NpgsqlDbType.Text)
                       NpgsqlParameter(Value = Nullable event.TraceState, NpgsqlDbType = NpgsqlDbType.Text) |]
                command.Parameters.AddRange values
                let first = i * values.Length
                "(" + String.Join(", ", values |> Array.mapi (fun j _ -> sprintf "$%d" (first + j + 1))) + ")")
        command.CommandText <-
            sprintf "insert into %s (id, aggregate, aggregate_id, version, position, event_type, data, recorded_at, expires_at, trace_parent, trace_state) values %s on conflict (id) do nothing"
                QualifiedTable (String.Join(", ", rows))
        command

    /// Deletes the expired events, when there's a TTL and the last deletion was ExpirationFrequency ago or more (or
    /// never happened in this process). Called once events are recorded. A failure is logged: the events were recorded
    member this.DeleteExpiredAsync() : Task =
        task {
            match EventTTL with
            | None -> ()
            | Some _ ->
                let now = DateTime.UtcNow.Ticks
                let last = Interlocked.Read(&LastExpiration)
                // One caller deletes; the others carry on
                if now - last >= ExpirationFrequency.Ticks && Interlocked.CompareExchange(&LastExpiration, now, last) = last then
                    try
                        use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                        use delete = new NpgsqlCommand(sprintf "delete from %s where expires_at < now()" QualifiedTable, connection)
                        let! _ = delete.ExecuteNonQueryAsync().ConfigureAwait(false)
                        ()
                    with ex ->
                        logger.LogWarning(ex, "The outbox couldn't delete its expired events from {Table}; it tries again in {ExpirationFrequency}.", QualifiedTable, ExpirationFrequency)
        } :> Task

    interface IOutbox with
        member this.RecordAsync(events: OutboxEvent[]) =
            task {
                if events.Length = 0 then return 0
                else
                    do! this.EnsureCreatedAsync().ConfigureAwait(false)
                    use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                    use batch = new NpgsqlBatch(connection)
                    let insert = this.Insert events
                    batch.BatchCommands.Add insert
                    let! _ = batch.ExecuteNonQueryAsync().ConfigureAwait(false)
                    do! this.DeleteExpiredAsync().ConfigureAwait(false)
                    return insert.RecordsAffected
            }
