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
/// its expires_at (from its recording, or from its publication when a publisher is registered), and the outbox deletes
/// the expired rows itself, at most every ExpirationFrequency: PostgreSQL has no TTL of its own. Its publisher's lease
/// is a row of rosetta.outbox_leases
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
            "    events_union_clr_type text,"
            "    event_clr_type text,"
            "    event_type text not null,"
            "    data json not null,"
            "    recorded_at timestamptz not null,"
            "    expires_at timestamptz,"
            "    trace_parent text,"
            "    trace_state text,"
            "    published_at timestamptz);"
            // A table created before the outbox had a publisher
            sprintf "alter table %s add column if not exists published_at timestamptz;" QualifiedTable
            // A table created before the outbox recorded the events' types (Rosetta 1.38)
            sprintf "alter table %s add column if not exists events_union_clr_type text, add column if not exists event_clr_type text;" QualifiedTable
            sprintf "create index if not exists %s on %s (expires_at) where expires_at is not null;" (PostgreSqlTable.Quote(TableName + "_expires_at")) QualifiedTable
            sprintf "create index if not exists %s on %s (recorded_at, position) where published_at is null;" (PostgreSqlTable.Quote(TableName + "_unpublished")) QualifiedTable
            "create table if not exists rosetta.outbox_leases (name text primary key, owner text not null, expires_at timestamptz not null);" ]

    let Nullable (value: string) = if isNull value then box DBNull.Value else box value

    let At (at: DateTime) = DateTime.SpecifyKind(at, DateTimeKind.Utc)

    /// Without a logger: a failed deletion of the expired events goes unreported
    new(database: PostgreSqlDatabase, options: OutboxOptions) = PostgreSqlOutbox(database, options, NullLogger<PostgreSqlOutbox>.Instance)

    /// The outbox's table, schema-qualified and quoted
    member this.Table = QualifiedTable

    /// Creates the database when it's missing, and the outbox's tables, once: before the first events are recorded, not
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
                       NpgsqlParameter(Value = Nullable event.EventsUnionClrType, NpgsqlDbType = NpgsqlDbType.Text)
                       NpgsqlParameter(Value = Nullable event.EventClrType, NpgsqlDbType = NpgsqlDbType.Text)
                       NpgsqlParameter(Value = event.EventType)
                       NpgsqlParameter(Value = event.Data, NpgsqlDbType = NpgsqlDbType.Json)
                       NpgsqlParameter(Value = At event.RecordedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz)
                       NpgsqlParameter(
                           Value = (match EventTTL with
                                    // Waiting to be published, it doesn't expire
                                    | Some ttl when not options.ExpiresFromPublication -> box (At (event.RecordedAt.Add ttl))
                                    | _ -> box DBNull.Value),
                           NpgsqlDbType = NpgsqlDbType.TimestampTz)
                       NpgsqlParameter(Value = Nullable event.TraceParent, NpgsqlDbType = NpgsqlDbType.Text)
                       NpgsqlParameter(Value = Nullable event.TraceState, NpgsqlDbType = NpgsqlDbType.Text) |]
                command.Parameters.AddRange values
                let first = i * values.Length
                "(" + String.Join(", ", values |> Array.mapi (fun j _ -> sprintf "$%d" (first + j + 1))) + ")")
        command.CommandText <-
            sprintf "insert into %s (id, aggregate, aggregate_id, version, position, events_union_clr_type, event_clr_type, event_type, data, recorded_at, expires_at, trace_parent, trace_state) values %s on conflict (id) do nothing"
                QualifiedTable (String.Join(", ", rows))
        command

    /// Deletes the expired events, when there's a TTL and the last deletion was ExpirationFrequency ago or more (or
    /// never happened in this process). Called once events are recorded or published. A failure is logged: the events
    /// were recorded or published
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

    interface IOutboxRelayStore with
        member this.TryLeaseAsync(owner, duration, cancellationToken) =
            task {
                do! this.EnsureCreatedAsync().ConfigureAwait(false)
                use! connection = database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false)
                // Taken when free, expired or already the owner's; by the database's clock
                use lease =
                    new NpgsqlCommand(
                        """insert into rosetta.outbox_leases as lease (name, owner, expires_at) values ($1, $2, now() + $3)
                           on conflict (name) do update set owner = excluded.owner, expires_at = excluded.expires_at
                           where lease.owner = excluded.owner or lease.expires_at < now()""",
                        connection)
                lease.Parameters.Add(NpgsqlParameter(Value = TableName)) |> ignore
                lease.Parameters.Add(NpgsqlParameter(Value = owner)) |> ignore
                lease.Parameters.Add(NpgsqlParameter(Value = duration, NpgsqlDbType = NpgsqlDbType.Interval)) |> ignore
                let! taken = lease.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)
                return taken = 1
            }

        member this.UnpublishedAsync(max, cancellationToken) =
            task {
                do! this.EnsureCreatedAsync().ConfigureAwait(false)
                use! connection = database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false)
                use select =
                    new NpgsqlCommand(
                        sprintf "select id, aggregate, aggregate_id, version, position, event_type, data::text, recorded_at, trace_parent, trace_state, events_union_clr_type, event_clr_type from %s where published_at is null order by recorded_at, position limit $1" QualifiedTable,
                        connection)
                select.Parameters.Add(NpgsqlParameter(Value = max, NpgsqlDbType = NpgsqlDbType.Integer)) |> ignore
                use! reader = select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false)
                let events = Collections.Generic.List<OutboxEvent>()
                let mutable reading = true
                while reading do
                    let! read = reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                    if read then
                        events.Add(
                            { Id = reader.GetGuid 0
                              Aggregate = reader.GetString 1
                              AggregateId = reader.GetString 2
                              Version = reader.GetInt64 3
                              Position = reader.GetInt32 4
                              EventsUnionClrType = if reader.IsDBNull 10 then null else reader.GetString 10
                              EventClrType = if reader.IsDBNull 11 then null else reader.GetString 11
                              EventType = reader.GetString 5
                              Data = reader.GetString 6
                              RecordedAt = reader.GetDateTime 7
                              TraceParent = if reader.IsDBNull 8 then null else reader.GetString 8
                              TraceState = if reader.IsDBNull 9 then null else reader.GetString 9 })
                    else reading <- false
                return events.ToArray()
            }

        member this.MarkPublishedAsync(events, publishedAt, cancellationToken) =
            task {
                if events.Length > 0 then
                    use! connection = database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false)
                    use mark = new NpgsqlCommand(sprintf "update %s set published_at = $1, expires_at = $2 where id = any($3)" QualifiedTable, connection)
                    mark.Parameters.Add(NpgsqlParameter(Value = At publishedAt, NpgsqlDbType = NpgsqlDbType.TimestampTz)) |> ignore
                    mark.Parameters.Add(
                        NpgsqlParameter(
                            Value = (match EventTTL with
                                     | Some ttl -> box (At (publishedAt.Add ttl))
                                     | None -> box DBNull.Value),
                            NpgsqlDbType = NpgsqlDbType.TimestampTz)) |> ignore
                    mark.Parameters.Add(NpgsqlParameter(Value = (events |> Array.map (fun event -> event.Id)), NpgsqlDbType = (NpgsqlDbType.Array ||| NpgsqlDbType.Uuid))) |> ignore
                    let! _ = mark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false)
                    do! this.DeleteExpiredAsync().ConfigureAwait(false)
            } :> Task
