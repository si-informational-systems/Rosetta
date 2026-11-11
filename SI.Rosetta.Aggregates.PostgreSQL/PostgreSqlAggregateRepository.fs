namespace SI.Rosetta.Aggregates.PostgreSQL

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open Npgsql
open SI.Rosetta.Aggregates
open SI.Rosetta.Persistence.PostgreSQL

/// Stores the AggregateState as a row of the app's table, mapped as read models are (see PostgreSqlTables): by default
/// the snake_case plural of the state type's name (ParcelAggregateState → parcel_aggregate_states) in PostgreSQL:Schema,
/// a column per property, Id the key and Version a regular column. The tables are the app's: Rosetta checks each one
/// the first time its state type is used, and doesn't create or change them.
/// Writes are checked against the version the aggregate was loaded at: a new aggregate is inserted unless its Id
/// exists, a loaded one updated only while its row is still at that version. Otherwise ConcurrencyException, and
/// nothing is written. With an outbox, the aggregate's PublishedEvents are recorded with its state, in one transaction,
/// or on their own when the command changed nothing
type PostgreSqlAggregateRepository(database: PostgreSqlDatabase, outbox: PostgreSqlOutbox option) =
    let Checked = ConcurrentDictionary<Type, PostgreSqlTable>()

    new(database: PostgreSqlDatabase) = PostgreSqlAggregateRepository(database, None)

    /// The state type's table, checked against it the first time. A table that doesn't match fails every command on
    /// that state type, until it does
    member private this.TableAsync(stateType: Type) : Task<PostgreSqlTable> =
        task {
            match Checked.TryGetValue stateType with
            | true, table -> return table
            | false, _ ->
                let table = PostgreSqlTables.Of database.Schema stateType
                use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
                match! PostgreSqlTableCheck.ProblemAsync(connection, table, "the aggregates are stored").ConfigureAwait(false) with
                | Some problem -> return raise (InvalidOperationException problem)
                | None -> return Checked.GetOrAdd(stateType, table)
        }

    static member private VersionColumnOf(table: PostgreSqlTable) =
        table.Columns |> Array.find (fun column -> column.Property.Name = "Version")

    /// The state's write, checked against the version it was loaded at
    static member private WriteOf(table: PostgreSqlTable, state: obj, loaded: int64) =
        if loaded = 0L then PostgreSqlTables.Insert table state
        else PostgreSqlTables.UpdateWhen table state (PostgreSqlAggregateRepository.VersionColumnOf table) (box loaded)

    static member private Refused(stateType: Type, id: string, loaded: int64) =
        if loaded = 0L then ConcurrencyException.Created(stateType, id)
        else ConcurrencyException.Changed(stateType, id, loaded)

    /// StoreAsync, apart from the generic interface method: written there, the F# compiler emitted an invalid program
    member private this.StoreStateAsync(stateType: Type, aggregate: IAggregate, state: obj) : Task =
        task {
            let! table = this.TableAsync stateType
            let loaded = LoadedVersionOf aggregate
            let write = PostgreSqlAggregateRepository.WriteOf(table, state, loaded)
            let events =
                match outbox with
                | Some _ -> OutboxEvents.Of aggregate
                | None -> [||]
            if events.Length > 0 then
                do! outbox.Value.EnsureCreatedAsync().ConfigureAwait(false)
            let record = if events.Length > 0 then Some (outbox.Value.Insert events) else None
            use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
            use! transaction = connection.BeginTransactionAsync().ConfigureAwait(false)
            use batch = new NpgsqlBatch(connection, transaction)
            batch.BatchCommands.Add write
            record |> Option.iter batch.BatchCommands.Add
            let! _ = batch.ExecuteNonQueryAsync().ConfigureAwait(false)
            // Disposed without a commit, the transaction is rolled back, the events with the state
            if write.RecordsAffected = 0 then raise (PostgreSqlAggregateRepository.Refused(stateType, aggregate.Id, loaded))
            do! transaction.CommitAsync().ConfigureAwait(false)
            if record.IsSome then
                OutboxEvents.Recorded events.[0].Aggregate record.Value.RecordsAffected
                do! outbox.Value.DeleteExpiredAsync().ConfigureAwait(false)
        } :> Task

    /// GetAsync's reading, apart from the generic interface method (as StoreStateAsync)
    member private this.ReadStateAsync(stateType: Type, id: string) : Task<obj option> =
        task {
            let! table = this.TableAsync stateType
            use! connection = database.DataSource.OpenConnectionAsync().ConfigureAwait(false)
            use select = PostgreSqlTables.Select table connection null [| box id |]
            use! reader = select.ExecuteReaderAsync().ConfigureAwait(false)
            let! found = reader.ReadAsync().ConfigureAwait(false)
            return if found then Some (PostgreSqlTables.ReadRow table reader) else None
        }

    interface IStateBasedAggregateRepository with
        member this.GetAsync<'TAggregateState
                                    when 'TAggregateState : (new : unit -> 'TAggregateState)
                                    and 'TAggregateState :> IAggregateState>
            (id: string, version: int64): Task<Option<'TAggregateState>> =
            task {
                match! this.ReadStateAsync(typeof<'TAggregateState>, id).ConfigureAwait(false) with
                | Some state -> return Some (state :?> 'TAggregateState)
                | None -> return None
            }

        member this.StoreAsync<'TAggregateState
                    when 'TAggregateState :> IAggregateState>
                    (aggregate: IAggregate<'TAggregateState>): Task =
            this.StoreStateAsync(typeof<'TAggregateState>, aggregate, box (aggregate.GetState()))

    interface IRecordPublishedEvents with
        member this.RecordPublishedEventsAsync(aggregate) =
            match outbox with
            | Some outbox -> OutboxEvents.RecordAsync outbox aggregate
            | None -> Task.CompletedTask
