namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open MongoDB.Bson
open MongoDB.Driver
open Raven.Client.Documents
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Common
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Aggregates.RavenDB

/// The state-based repositories, as UseAggregatesWithoutEventSourcing registers them: the state read back, and every
/// write checked against the version it was loaded at
type StateBasedAggregateRepositoryTests(databases: Databases) =
    let DatabaseName = "Rosetta-StateBasedAggregateRepositoryTests"

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "StoredParcels-%s" (Guid.NewGuid().ToString("N"))

    let CreateBuilder (configuration: IConfiguration) =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddConfiguration(configuration) |> ignore
        builder

    let RavenHost (register: HostApplicationBuilder -> unit) =
        let builder = CreateBuilder(databases.Configuration DatabaseName)
        register builder
        builder.Build()

    let MongoHost (register: HostApplicationBuilder -> unit) =
        let url = MongoUrlBuilder(databases.MongoReplicaSetUrl.ToString(), DatabaseName = DatabaseName, AuthenticationSource = "admin")
        let builder = CreateBuilder(databases.Configuration("unused", [ "MongoDB:ConnectionString", url.ToString() ]))
        register builder
        builder.Build()

    let RegisteredTo (recipient: string) id =
        let parcel = ParcelAggregate()
        parcel.Execute(ParcelCommands.Register { Id = id; Recipient = recipient })
        parcel

    let Load (repository: IStateBasedAggregateRepository) id =
        task {
            let! state = repository.GetAsync<ParcelAggregateState>(id)
            return ParcelAggregate(State = state.Value)
        }

    let ReadsBackTheStoredState (host: IHost) =
        task {
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()
            let parcel = ParcelAggregate()
            parcel.Execute(ParcelCommands.Register { Id = id; Recipient = "Ada" })
            parcel.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            do! repository.StoreAsync(parcel)

            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            let! missing = repository.GetAsync<ParcelAggregateState>(NewId())

            Assert.Equal("Grace", stored.Value.Recipient)
            Assert.Equal(2L, stored.Value.Version)
            Assert.True(missing.IsNone)
        } :> Task

    /// Two commands load version 1: the first one's change is stored, the second one's refused, and the second one,
    /// run again on the aggregate as it is now, goes through. Returns the aggregate's Id
    let RefusesAStaleUpdate (host: IHost) =
        task {
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()
            do! repository.StoreAsync(RegisteredTo "Ada" id)
            let! first = Load repository id
            let! second = Load repository id
            first.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Grace" })
            second.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Linus" })
            do! repository.StoreAsync(first)

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync(second))

            Assert.Contains("is no longer at version 1", refused.Message)
            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Grace", stored.Value.Recipient)
            Assert.Equal(2L, stored.Value.Version)
            let! retried = Load repository id
            retried.Execute(ParcelCommands.Readdress { Id = id; Recipient = "Linus" })
            do! repository.StoreAsync(retried)
            let! after = repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Linus", after.Value.Recipient)
            Assert.Equal(3L, after.Value.Version)
            return id
        }

    /// Two commands find the aggregate missing: the first one creates it, the second one is refused. Returns its Id
    let RefusesASecondCreation (host: IHost) =
        task {
            let repository = host.Services.GetRequiredService<IStateBasedAggregateRepository>()
            let id = NewId()
            do! repository.StoreAsync(RegisteredTo "Ada" id)

            let! refused = Assert.ThrowsAsync<ConcurrencyException>(fun () -> repository.StoreAsync(RegisteredTo "Linus" id))

            Assert.Contains("already exists", refused.Message)
            let! stored = repository.GetAsync<ParcelAggregateState>(id)
            Assert.Equal("Ada", stored.Value.Recipient)
            Assert.Equal(1L, stored.Value.Version)
            return id
        }

    let Raven () = RavenHost (fun builder -> builder.UseAggregatesWithoutEventSourcing<RavenDB>(typeof<ParcelAggregate>.Assembly) |> ignore)
    let Mongo () = MongoHost (fun builder -> builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<ParcelAggregate>.Assembly) |> ignore)

    [<Fact>]
    member this.``RavenDB reads the state back as it was stored, with its Version``() =
        task {
            use host = Raven ()
            do! ReadsBackTheStoredState host
        }

    [<Fact>]
    member this.``MongoDB reads the state back as it was stored, with its Version``() =
        task {
            use host = Mongo ()
            do! ReadsBackTheStoredState host
        }

    [<Fact>]
    member this.``RavenDB refuses a command that loaded an older version``() =
        task {
            use host = Raven ()
            let! _ = RefusesAStaleUpdate host
            ()
        }

    [<Fact>]
    member this.``MongoDB refuses a command that loaded an older version``() =
        task {
            use host = Mongo ()
            let! _ = RefusesAStaleUpdate host
            ()
        }

    [<Fact>]
    member this.``RavenDB refuses creating an aggregate that exists meanwhile``() =
        task {
            use host = Raven ()
            let! _ = RefusesASecondCreation host
            ()
        }

    [<Fact>]
    member this.``MongoDB refuses creating an aggregate that exists meanwhile``() =
        task {
            use host = Mongo ()
            let! _ = RefusesASecondCreation host
            ()
        }

    [<Fact>]
    member this.``RavenDB records no events for a refused command``() =
        task {
            use host = RavenHost (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<RavenDB>(typeof<ParcelAggregate>.Assembly)
                    .UseOutboxWith<RavenDB>(fun outbox -> outbox.CollectionName <- "RefusedParcelEvents") |> ignore)

            let! id = RefusesASecondCreation host

            use session = host.Services.GetRequiredService<IDocumentStore>().OpenAsyncSession()
            let! recorded = session.Advanced.LoadStartingWithAsync<RavenDBOutboxEvent>("RefusedParcelEvents/", pageSize = 1024)
            let event = Assert.Single(recorded |> Seq.filter (fun event -> event.AggregateId = id))
            Assert.Contains("Ada", event.Data)
        }

    [<Fact>]
    member this.``MongoDB records no events for a refused command``() =
        task {
            use host = MongoHost (fun builder ->
                builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<ParcelAggregate>.Assembly)
                    .UseOutboxWith<MongoDB>(fun outbox -> outbox.CollectionName <- "RefusedOutbox") |> ignore)

            let! id = RefusesAStaleUpdate host

            let documents = host.Services.GetRequiredService<IMongoDatabase>().GetCollection<BsonDocument>("RefusedOutbox")
            let! recorded = documents.Find(Builders<BsonDocument>.Filter.Eq("aggregateId", id)).ToListAsync()
            // Registered (1), readdressed to Grace (2), then to Linus once run again (3): not the refused Linus at 2
            let versions = recorded |> Seq.map (fun document -> document.["version"].ToInt64(), document.["data"].AsString) |> Seq.sortBy fst |> Seq.toList
            Assert.Equal<int64 list>([ 1L; 2L; 3L ], versions |> List.map fst)
            Assert.Contains("Grace", snd versions.[1])
        }
