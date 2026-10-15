namespace SI.Rosetta.IntegrationTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open MongoDB.Driver
open Xunit
open SI.Rosetta.Aggregates
open SI.Rosetta.Aggregates.HostBuilder

/// The state-based repositories, as UseAggregatesWithoutEventSourcing registers them
type StateBasedAggregateRepositoryTests(databases: Databases) =
    let DatabaseName = "Rosetta-StateBasedAggregateRepositoryTests"

    // Not the Parcels category, which HostingTests projects
    let NewId () = sprintf "StoredParcels-%s" (Guid.NewGuid().ToString("N"))

    let CreateBuilder (configuration: IConfiguration) =
        let builder = Host.CreateApplicationBuilder(HostApplicationBuilderSettings(DisableDefaults = true))
        builder.Configuration.AddConfiguration(configuration) |> ignore
        builder

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

    [<Fact>]
    member this.``RavenDB reads the state back as it was stored, with its Version``() =
        task {
            let builder = CreateBuilder(databases.Configuration DatabaseName)
            builder.UseAggregatesWithoutEventSourcing<RavenDB>(typeof<ParcelAggregate>.Assembly) |> ignore
            use host = builder.Build()

            do! ReadsBackTheStoredState host
        }

    [<Fact>]
    member this.``MongoDB reads the state back as it was stored, with its Version``() =
        task {
            let url = MongoUrlBuilder(databases.MongoReplicaSetUrl.ToString(), DatabaseName = DatabaseName, AuthenticationSource = "admin")
            let builder = CreateBuilder(databases.Configuration("unused", [ "MongoDB:ConnectionString", url.ToString() ]))
            builder.UseAggregatesWithoutEventSourcing<MongoDB>(typeof<ParcelAggregate>.Assembly) |> ignore
            use host = builder.Build()

            do! ReadsBackTheStoredState host
        }
