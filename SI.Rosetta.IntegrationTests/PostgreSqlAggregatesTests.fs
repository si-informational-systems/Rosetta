namespace SI.Rosetta.IntegrationTests

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Aggregates.PostgreSQL
open SI.Rosetta.Common
open SI.Rosetta.Persistence.PostgreSQL

/// A state with a list, an option and a map: routed_parcel_states
type RoutedParcelState() =
    inherit AggregateState<ParcelEvents>()
    member val Recipients : string list = [] with get, set
    member val ReaddressedAt : DateTime option = None with get, set
    member val Notes : Map<string, string> = Map.empty with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | ParcelEvents.Registered e ->
            this.Id <- e.Id
            this.Recipients <- [ e.Recipient ]
            this.Notes <- Map [ "desk", "front" ]
        | ParcelEvents.Readdressed e ->
            this.Recipients <- this.Recipients @ [ e.Recipient ]
            this.ReaddressedAt <- Some (DateTime(2026, 11, 11, 17, 30, 0, DateTimeKind.Utc))

type RoutedParcelAggregate() =
    inherit Aggregate<RoutedParcelState, ParcelCommands>()

    override this.Execute(command) =
        match command with
        | ParcelCommands.Register cmd -> this.Apply(ParcelEvents.Registered { Id = cmd.Id; Recipient = cmd.Recipient })
        | ParcelCommands.Readdress cmd -> this.Apply(ParcelEvents.Readdressed { Id = cmd.Id; Recipient = cmd.Recipient })

/// No table: lost_parcel_states doesn't exist until a test creates it
type LostParcelState() =
    inherit AggregateState<ParcelEvents>()
    member val Recipient = "" with get, set
    override this.ApplyEvent(_) = ()

/// A column the table lacks: unweighed_parcel_states has no weight
type UnweighedParcelState() =
    inherit AggregateState<ParcelEvents>()
    member val Weight = 0 with get, set
    override this.ApplyEvent(_) = ()

/// An outbox row, as whatever publishes the events would read it
type OutboxRow =
    { Id: Guid
      Aggregate: string
      Version: int64
      Position: int
      EventType: string
      Data: string
      RecordedAt: DateTime
      ExpiresAt: DateTime option }

/// The state-based aggregates and the outbox in PostgreSQL: the mapping, the version checks, the table check, the
/// outbox in the state's transaction and its TTL
type PostgreSqlAggregatesTests(databases: Databases) =
    let DatabaseName = sprintf "rosetta_aggregates_%s" (Guid.NewGuid().ToString("N"))
    let ConnectionString = databases.PostgreSqlConnectionString DatabaseName
    let Database = new PostgreSqlDatabase({ ConnectionString = ConnectionString; Schema = "public" })
    let Repository = PostgreSqlAggregateRepository(Database) :> IAggregateRepository

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "StoredParcels-%s" (Guid.NewGuid().ToString("N"))

    let RegisteredTo (recipient: string) id =
        let parcel = ParcelAggregate()
        parcel.Execute(ParcelCommands.Register { Id = id; Recipient = recipient })
        parcel

    let Registered id = RegisteredTo "Ada" id

    let Readdressed id =
        let parcel = Registered id
        parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
        parcel

    let Load (repository: IAggregateRepository) id =
        task {
            let! state = repository.GetAsync<ParcelAggregateState>(id)
            return ParcelAggregate(State = state.Value)
        }

    let WithOutbox (options: OutboxOptions) =
        PostgreSqlAggregateRepository(Database, Some (PostgreSqlOutbox(Database, options))) :> IAggregateRepository

    let Execute (sql: string) =
        task {
            use! connection = Database.DataSource.OpenConnectionAsync()
            use command = new NpgsqlCommand(sql, connection)
            let! _ = command.ExecuteNonQueryAsync()
            ()
        }

    /// The aggregate's rows in the outbox table, in the order they were recorded
    let OutboxOf (table: string) (aggregateId: string) =
        task {
            use! connection = Database.DataSource.OpenConnectionAsync()
            use command =
                new NpgsqlCommand(
                    sprintf "select id, aggregate, version, position, event_type, data::text, recorded_at, expires_at from %s where aggregate_id = $1 order by recorded_at, position" table,
                    connection)
            command.Parameters.Add(NpgsqlParameter(Value = aggregateId)) |> ignore
            use! reader = command.ExecuteReaderAsync()
            let rows = ResizeArray<OutboxRow>()
            while reader.Read() do
                rows.Add(
                    { Id = reader.GetGuid 0
                      Aggregate = reader.GetString 1
                      Version = reader.GetInt64 2
                      Position = reader.GetInt32 3
                      EventType = reader.GetString 4
                      Data = reader.GetString 5
                      RecordedAt = reader.GetDateTime 6
                      ExpiresAt = if reader.IsDBNull 7 then None else Some (reader.GetDateTime 7) })
            return List.ofSeq rows
        }

    let Decode (row: OutboxRow) = EventEncoding.Decode<ParcelEvents> Seq.empty row.EventType row.Data

    interface IAsyncLifetime with
        // The app's schema, as its migrations would apply it
        member this.InitializeAsync() =
            ValueTask(task {
                do! Database.EnsureCreatedAsync()
                do! Execute(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schema", "aggregates.sql")))
            })

    interface IAsyncDisposable with
        member _.DisposeAsync() = (Database :> IAsyncDisposable).DisposeAsync()

    [<Fact>]
    member this.``UseAggregatesWithoutEventSourcing<PostgreSQL> reads the state back as it was stored, with its Version``() =
        task {
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(databases.Configuration("unused", [ "PostgreSQL:ConnectionString", ConnectionString ])) |> ignore
            builder.UseAggregatesWithoutEventSourcing<PostgreSQL>(typeof<ParcelAggregate>.Assembly) |> ignore
            use host = builder.Build()
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()

            do! repository.StoreAsync(Readdressed id)

            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            let! missing = repository.GetAsync<ParcelAggregateState>(NewId())
            Assert.Equal("Grace", stored.Value.Recipient)
            Assert.Equal(id, stored.Value.Id)
            Assert.Equal(2L, stored.Value.Version)
            Assert.True(missing.IsNone)
        }

    [<Fact>]
    member this.``A loaded aggregate's changes update its row``() =
        task {
            let id = NewId()
            do! Repository.StoreAsync(Registered id)
            let! parcel = Load Repository id

            parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            do! Repository.StoreAsync(parcel)

            let! stored = Repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Grace", stored.Value.Recipient)
            Assert.Equal(2L, stored.Value.Version)
        }

    [<Fact>]
    member this.``Lists, options and maps round-trip``() =
        task {
            let id = NewId()
            let parcel = RoutedParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })

            do! Repository.StoreAsync(parcel)

            let! stored = Repository.GetAsync<RoutedParcelState>(id)
            Assert.Equal<string list>([ "Ada"; "Grace" ], stored.Value.Recipients)
            Assert.Equal(Some (DateTime(2026, 11, 11, 17, 30, 0, DateTimeKind.Utc)), stored.Value.ReaddressedAt)
            Assert.Equal<Map<string, string>>(Map [ "desk", "front" ], stored.Value.Notes)
            Assert.Equal(2L, stored.Value.Version)
        }

    [<Fact>]
    member this.``A command that loaded an older version is refused, and the newer state is kept``() =
        task {
            let id = NewId()
            do! Repository.StoreAsync(Registered id)
            let! first = Load Repository id
            let! second = Load Repository id
            first.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            second.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Linus" })
            do! Repository.StoreAsync(first)

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> Repository.StoreAsync(second))

            Assert.Contains("is no longer at version 1", refused.Message)
            let! stored = Repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Grace", stored.Value.Recipient)
            Assert.Equal(2L, stored.Value.Version)
        }

    [<Fact>]
    member this.``Creating an aggregate that exists meanwhile is refused``() =
        task {
            let id = NewId()
            do! Repository.StoreAsync(Registered id)

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> Repository.StoreAsync(RegisteredTo "Linus" id))

            Assert.Contains("already exists", refused.Message)
            let! stored = Repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Ada", stored.Value.Recipient)
        }

    [<Fact>]
    member this.``A state's table is checked, until it matches``() =
        task {
            let! missing = Assert.ThrowsAsync<InvalidOperationException>(fun () -> Repository.GetAsync<LostParcelState>(NewId()) :> Task)
            let! column = Assert.ThrowsAsync<InvalidOperationException>(fun () -> Repository.GetAsync<UnweighedParcelState>(NewId()) :> Task)

            Assert.Equal("""The table "public"."lost_parcel_states" (for LostParcelState) doesn't exist: create it before the aggregates are stored.""", missing.Message)
            Assert.Equal("""The table "public"."unweighed_parcel_states" (for UnweighedParcelState) has no column weight (Weight).""", column.Message)
            // The app's migrations create it: the next command finds it
            do! Execute "create table lost_parcel_states (id text primary key, version bigint not null, recipient text not null)"
            let! found = Repository.GetAsync<LostParcelState>(NewId())
            Assert.True(found.IsNone)
        }

    [<Fact>]
    member this.``The outbox records the published events with the state, in its transaction``() =
        task {
            let repository = WithOutbox(OutboxOptions(EventTTL = Nullable(TimeSpan.FromHours 1.0)))
            let id = NewId()

            do! repository.StoreAsync(Readdressed id)

            let! recorded = OutboxOf "rosetta.outbox" id
            Assert.Equal<string list>([ "ParcelRegistered"; "ParcelReaddressed" ], recorded |> List.map (fun row -> row.EventType))
            Assert.Equal<int list>([ 0; 1 ], recorded |> List.map (fun row -> row.Position))
            for row in recorded do
                Assert.Equal("Parcel", row.Aggregate)
                Assert.Equal(2L, row.Version)
                Assert.Equal(row.RecordedAt.AddHours 1.0, row.ExpiresAt.Value, TimeSpan.FromSeconds 1.0)
            Assert.Equal(Ok (ParcelEvents.Readdressed { Id = id; Recipient = "Grace" }), Decode recorded.[1])
        }

    [<Fact>]
    member this.``A refused command records neither its state nor its events``() =
        task {
            let repository = WithOutbox(OutboxOptions(CollectionName = "ParcelEvents"))
            let id = NewId()
            do! repository.StoreAsync(Registered id)

            let! _ = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync(RegisteredTo "Linus" id))

            let! recorded = OutboxOf "rosetta.parcel_events" id
            let row = Assert.Single(recorded)
            Assert.Equal(Ok (ParcelEvents.Registered { Id = id; Recipient = "Ada" }), Decode row)
            Assert.True(row.ExpiresAt.IsNone)
        }

    [<Fact>]
    member this.``The outbox leaves an event it holds as it is when an unchanged aggregate publishes it again``() =
        task {
            let repository = WithOutbox(OutboxOptions())
            let id = NewId()
            do! repository.StoreAsync(Registered id)
            let! first = OutboxOf "rosetta.outbox" id
            let! repeated = Load repository id
            repeated.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })

            do! (repository :?> IRecordPublishedEvents).RecordPublishedEventsAsync(repeated)

            let! recorded = OutboxOf "rosetta.outbox" id
            Assert.Equal(Assert.Single(first), Assert.Single(recorded))
        }

    [<Fact>]
    member this.``The outbox deletes the expired events once its expiration frequency has passed``() =
        task {
            let repository = WithOutbox(OutboxOptions(EventTTL = Nullable(TimeSpan.FromSeconds 1.0), ExpirationFrequency = TimeSpan.FromSeconds 1.0))
            let expiring, later = NewId(), NewId()
            do! repository.StoreAsync(Registered expiring)
            do! Task.Delay(TimeSpan.FromSeconds 2.5)

            do! repository.StoreAsync(Registered later)

            let! expired = OutboxOf "rosetta.outbox" expiring
            let! recorded = OutboxOf "rosetta.outbox" later
            Assert.Empty(expired)
            Assert.Single(recorded) |> ignore
        }

    [<Fact>]
    member this.``UseOutboxWith<PostgreSQL> shares the state-based repository's transaction``() =
        task {
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(databases.Configuration("unused", [ "PostgreSQL:ConnectionString", ConnectionString ])) |> ignore
            builder.UseAggregatesWithoutEventSourcing<PostgreSQL>(typeof<ParcelAggregate>.Assembly).UseOutboxWith<PostgreSQL>() |> ignore
            use host = builder.Build()
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()

            do! repository.StoreAsync(Registered id)

            Assert.IsType<PostgreSqlAggregateRepository>(repository) |> ignore
            let! recorded = OutboxOf "rosetta.outbox" id
            Assert.Single(recorded) |> ignore
        }
