namespace SI.Rosetta.IntegrationTests

open System
open System.ComponentModel.DataAnnotations
open System.ComponentModel.DataAnnotations.Schema
open System.IO
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Npgsql
open Xunit
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Persistence.PostgreSQL
open SI.Rosetta.Projections
open SI.Rosetta.Projections.HostBuilder
open SI.Rosetta.Projections.PostgreSQL

/// A parcel's row in parcels.parcels ([<Table>] names it; the schema is the configured one)
[<CLIMutable; Table("parcels")>]
type ParcelRow = { Id: string; Recipient: string }

/// How many events a parcel had: parcels.parcel_event_counts, by convention
[<CLIMutable>]
type ParcelEventCount = { Id: string; Events: int }

type StopKind =
    | Delivery
    | Pickup

type ParcelSize =
    | Small = 0
    | Large = 1

type Handling = { Fragile: bool; Instructions: string option }

type Signature =
    | Signed of Name: string
    | Refused

/// Every column type the mapping covers, with a composite key: parcels.parcel_stops
[<CLIMutable>]
type ParcelStop =
    { [<Key>] ParcelId: string
      [<Key>] Stop: int
      ArrivedAt: DateTime
      Day: DateOnly
      Fee: decimal
      Note: string option
      Floor: int option
      Tags: string list
      Attempts: int[]
      Kind: StopKind
      Size: ParcelSize
      Handling: Handling
      SignedBy: Signature option }

/// No table: parcels.lost_parcels doesn't exist
[<CLIMutable>]
type LostParcel = { Id: string; Recipient: string }

/// A column the table lacks: parcels.parcels has no weight
[<CLIMutable; Table("parcels")>]
type WeighedParcel = { Id: string; Recipient: string; Weight: int }

/// A table without a key to upsert on: parcels.unkeyed_parcels
[<CLIMutable>]
type UnkeyedParcel = { Id: string; Recipient: string }

/// The parcel's row, and one more event in its tally
type ParcelSqlProjectionHandler(store: ISqlStore) =
    member private this.Project(id: string, recipient: string) =
        task {
            do! store.StoreAsync({ ParcelRow.Id = id; Recipient = recipient })
            let! tally = store.LoadAsync<ParcelEventCount>(id)
            let events = if isNull (box tally) then 0 else tally.Events
            do! store.StoreAsync({ ParcelEventCount.Id = id; Events = events + 1 })
        }

    interface IProjectionHandler<ParcelEvents> with
        member this.Handle(event, _checkpoint) =
            match event with
            | ParcelEvents.Registered e -> this.Project(e.Id, e.Recipient)
            | ParcelEvents.Readdressed e -> this.Project(e.Id, e.Recipient)

/// The Parcels in PostgreSQL. Run by name: its handler needs ISqlStore
[<HandlesStream(ParcelStreams.Parcels); ProjectionVersion(1)>]
type ParcelSqlProjection() =
    inherit Projection<ParcelEvents>()
    interface IAmHandledBy<ParcelSqlProjectionHandler, ParcelEvents>
    interface IProjectInto<ParcelRow>
    interface IProjectInto<ParcelEventCount>

/// PostgreSQL as the typed SQL store: the mapping, the event's transaction, the startup checks, two stores in one app
type PostgreSqlProjectionsTests(databases: Databases) =
    let DatabaseName = sprintf "rosetta_sql_%s" (Guid.NewGuid().ToString("N"))
    let ConnectionString = databases.PostgreSqlConnectionString DatabaseName
    let Database = new PostgreSqlDatabase({ ConnectionString = ConnectionString; Schema = "parcels" })
    let UnitOfWork = PostgreSqlProjectionsUnitOfWork(Database) :> IProjectionsUnitOfWork
    let Checkpoints = PostgreSqlCheckpointStore(Database) :> ICheckpointStore
    let Store = PostgreSqlStore(Database) :> ISqlStore

    let Registered id = ParcelEvents.Registered { Id = id; Recipient = "Ada" }

    let NewId (prefix: string) = sprintf "%s-%s" prefix (Guid.NewGuid().ToString("N"))

    let Handler (handle: unit -> Task) =
        { new IProjectionHandler<ParcelEvents> with
            member _.Handle(_, _) = handle () }

    let Stop parcelId =
        { ParcelId = parcelId
          Stop = 1
          ArrivedAt = DateTime(2026, 11, 11, 17, 30, 0, DateTimeKind.Utc)
          Day = DateOnly(2026, 11, 11)
          Fee = 12.50m
          Note = Some "ring twice"
          Floor = None
          Tags = [ "express"; "fragile" ]
          Attempts = [| 1; 2 |]
          Kind = Pickup
          Size = ParcelSize.Large
          Handling = { Fragile = true; Instructions = Some "this side up" }
          SignedBy = Some (Signed "Grace") }

    member private this.Projection(handlers: IProjectionHandler<ParcelEvents> list) =
        task {
            do! UnitOfWork.PrepareAsync([| typeof<ParcelRow>; typeof<ParcelEventCount> |])
            let name = NewId "Sql"
            let! checkpoint = Checkpoints.ReadAsync(sprintf "Checkpoints-%s" name)
            return
                Projection<ParcelEvents>(
                    Name = name,
                    SubscriptionStreamName = ParcelStreams.Parcels,
                    Checkpoint = checkpoint,
                    CheckpointStore = Checkpoints,
                    UnitOfWork = UnitOfWork,
                    Handlers = (handlers :> seq<_>))
        }

    interface IAsyncLifetime with
        // The app's schema, as its migrations would apply it
        member this.InitializeAsync() =
            ValueTask(task {
                do! Database.EnsureCreatedAsync()
                use connection = new NpgsqlConnection(ConnectionString)
                do! connection.OpenAsync()
                use schema = new NpgsqlCommand(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Schema", "parcels.sql")), connection)
                let! _ = schema.ExecuteNonQueryAsync()
                ()
            })

    interface IAsyncDisposable with
        member _.DisposeAsync() = (Database :> IAsyncDisposable).DisposeAsync()

    [<Fact>]
    member this.``Every column type round-trips``() =
        task {
            let stop = Stop (NewId "Parcels")

            do! Store.StoreAsync(stop)
            let! loaded = Store.LoadAsync<ParcelStop>((stop.ParcelId, stop.Stop))

            Assert.Equal(stop, loaded)
        }

    [<Fact>]
    member this.``Rows are loaded, replaced and deleted by their composite key``() =
        task {
            let first = Stop (NewId "Parcels")
            let second = { first with Stop = 2; Note = None; SignedBy = None; Tags = [] }
            do! Store.StoreManyAsync([| first; second |])

            do! Store.StoreAsync({ second with Fee = 3m })
            do! Store.DeleteAsync<ParcelStop>((first.ParcelId, 1))
            let! loaded = Store.LoadManyAsync<ParcelStop>([| box (first.ParcelId, 1); box (first.ParcelId, 2) |])

            Assert.True(isNull (box loaded.[box (first.ParcelId, 1)]))
            Assert.Equal({ second with Fee = 3m }, loaded.[box (first.ParcelId, 2)])
        }

    [<Fact>]
    member this.``A missing row loads as null``() =
        task {
            let! missing = Store.LoadAsync<ParcelRow>("Parcels-missing")
            Assert.True(isNull (box missing))
        }

    [<Fact>]
    member this.``An event's rows and its checkpoint are committed together``() =
        task {
            let id = NewId "Parcels"
            let! projection = this.Projection [ ParcelSqlProjectionHandler(Store) ]

            do! projection.ProjectAsync(Registered id, 5UL)

            let! row = Store.LoadAsync<ParcelRow>(id)
            let! tally = Store.LoadAsync<ParcelEventCount>(id)
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.Equal({ ParcelRow.Id = id; Recipient = "Ada" }, row)
            Assert.Equal(1, tally.Events)
            Assert.Equal(5UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``When a handler fails, nothing is written and the checkpoint stays``() =
        task {
            let id = NewId "Parcels"
            let! projection =
                this.Projection [
                    ParcelSqlProjectionHandler(Store)
                    Handler (fun () -> task { failwith "handler failure" }) ]

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered id, 5UL) :> Task)

            let! row = Store.LoadAsync<ParcelRow>(id)
            let! checkpoint = Checkpoints.ReadAsync(projection.Checkpoint.Id)
            Assert.True(isNull (box row))
            Assert.Equal(0UL, checkpoint.Value)
        }

    [<Fact>]
    member this.``An event retried after a handler failed adds one only once``() =
        task {
            let id = NewId "Parcels"
            let failed = ref false
            let! projection =
                this.Projection [
                    ParcelSqlProjectionHandler(Store)
                    Handler (fun () ->
                        if failed.Value then Task.CompletedTask
                        else
                            failed.Value <- true
                            Task.FromException(TimeoutException "a service the handler calls is down")) ]

            let! _ = Assert.ThrowsAsync<ProjectionException>(fun () -> projection.ProjectAsync(Registered id, 5UL) :> Task)
            do! projection.ProjectAsync(Registered id, 5UL)

            let! tally = Store.LoadAsync<ParcelEventCount>(id)
            Assert.Equal(1, tally.Events)
        }

    [<Fact>]
    member this.``A handler reads back the rows it wrote, before the commit``() =
        task {
            let id = NewId "Parcels"
            let seen = ref 0
            let! projection =
                this.Projection [
                    ParcelSqlProjectionHandler(Store)
                    Handler (fun () ->
                        task {
                            // Batched by the handler before: sent before this read
                            let! tally = Store.LoadAsync<ParcelEventCount>(id)
                            seen.Value <- tally.Events
                        }) ]

            do! projection.ProjectAsync(Registered id, 5UL)

            Assert.Equal(1, seen.Value)
        }

    [<Fact>]
    member this.``A projection whose table is missing, lacks a column or has no key doesn't start``() =
        task {
            let! missing = Assert.ThrowsAsync<ReadModelTableException>(fun () -> UnitOfWork.PrepareAsync([| typeof<LostParcel> |]))
            let! column = Assert.ThrowsAsync<ReadModelTableException>(fun () -> UnitOfWork.PrepareAsync([| typeof<WeighedParcel> |]))
            let! key = Assert.ThrowsAsync<ReadModelTableException>(fun () -> UnitOfWork.PrepareAsync([| typeof<UnkeyedParcel> |]))
            do! UnitOfWork.PrepareAsync([| typeof<ParcelRow>; typeof<ParcelEventCount>; typeof<ParcelStop> |])

            Assert.Contains("\"parcels\".\"lost_parcels\" (for LostParcel) doesn't exist", missing.Message)
            Assert.Contains("has no column weight (Weight)", column.Message)
            Assert.Contains("no primary key or unique index on (id)", key.Message)
        }

    [<Fact>]
    member this.``Clearing a projection's read models empties their tables``() =
        task {
            let stop = Stop (NewId "Parcels")
            do! Store.StoreAsync(stop)

            do! UnitOfWork.ClearAsync([| typeof<ParcelStop> |])

            let! cleared = Store.LoadAsync<ParcelStop>((stop.ParcelId, stop.Stop))
            Assert.True(isNull (box cleared))
        }

    [<Fact>]
    member this.``An app projects into RavenDB and PostgreSQL at once, and rebuilds a projection whose version went up``() =
        task {
            // A row the version bump (0 → 1) empties before the SQL projection runs
            do! Store.StoreAsync({ ParcelRow.Id = "Parcels-stale"; Recipient = "Nobody" })
            let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
            builder.Configuration.AddConfiguration(
                databases.Configuration("Rosetta-PostgreSqlProjectionsTests", [
                    "PostgreSQL:ConnectionString", ConnectionString
                    "PostgreSQL:Schema", "parcels" ])) |> ignore
            builder.UseAggregatesWithEventSourcing<SI.Rosetta.Aggregates.HostBuilder.KurrentDB>(typeof<ParcelSqlProjection>.Assembly) |> ignore
            builder.UseProjectionsWith<SI.Rosetta.Projections.HostBuilder.KurrentDB, RavenDB, PostgreSQL>(fun projections ->
                projections.Projection<ParcelProjection, ParcelEvents>()          // documents: its handler takes INoSqlStore
                projections.Projection<ParcelSqlProjection, ParcelEvents>()) |> ignore   // rows: its handler takes ISqlStore
            use host = builder.Build()
            do! host.StartAsync()

            let id = sprintf "Parcels-%s" (Guid.NewGuid().ToString("N"))
            let handler = host.Services.GetRequiredService<IParcelAggregateHandler>()
            do! handler.ExecuteAsync(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            do! handler.ExecuteAsync(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })

            let documents = host.Services.GetRequiredService<INoSqlStore>()
            let rows = host.Services.GetRequiredService<ISqlStore>()
            do! Eventually (TimeSpan.FromSeconds 60.0) "both projections catch up" (fun () ->
                    task {
                        let! parcel = documents.LoadAsync<Parcel>(id)
                        let! row = rows.LoadAsync<ParcelRow>(id)
                        let! tally = rows.LoadAsync<ParcelEventCount>(id)
                        return not (isNull (box parcel)) && parcel.Recipient = "Grace"
                            && not (isNull (box row)) && row.Recipient = "Grace"
                            && not (isNull (box tally)) && tally.Events = 2
                    })
            let! stale = rows.LoadAsync<ParcelRow>("Parcels-stale")
            Assert.True(isNull (box stale))
            // Each projection's checkpoint is in its own store
            let! sqlCheckpoint = Checkpoints.ReadAsync("Checkpoints-ParcelSql")
            Assert.Equal(1, sqlCheckpoint.Version)
            Assert.True(sqlCheckpoint.Value > 0UL)
            do! host.StopAsync()
        }
