namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Microsoft.Extensions.Configuration
open MongoDB.Bson
open MongoDB.Driver
open Testcontainers.KurrentDb
open Testcontainers.MongoDb
open Testcontainers.RavenDb
open Xunit
open KurrentDB.Client

/// KurrentDB, RavenDB and MongoDB in Docker, started once for every test (the images the compose stack uses)
type Databases() =
    let KurrentDBContainer =
        KurrentDbBuilder("kurrentplatform/kurrentdb:26.2")
            .WithEnvironment("KURRENTDB_INSECURE", "true")
            .WithEnvironment("KURRENTDB_RUN_PROJECTIONS", "All")
            .WithEnvironment("KURRENTDB_START_STANDARD_PROJECTIONS", "true")
            .WithEnvironment("KURRENTDB_TELEMETRY_OPTOUT", "true")
            .Build()

    let RavenDBContainer =
        RavenDbBuilder("ravendb/ravendb:7.2-latest")
            .WithEnvironment("RAVEN_Setup_Mode", "None")
            .WithEnvironment("RAVEN_License_Eula_Accepted", "true")
            .WithEnvironment("RAVEN_Security_UnsecuredAccessAllowed", "PublicNetwork")
            .Build()

    // A replica set, which MongoDB transactions need (with the module's default user)
    let MongoReplicaSetContainer = MongoDbBuilder("mongo:8").WithReplicaSet("rs0").Build()

    // A standalone server for the credentials tests: Host and Port reach it without the replica set's internal addresses
    let MongoWithAuthContainer = MongoDbBuilder("mongo:8").Build()

    let Containers: IContainer list = [ KurrentDBContainer; RavenDBContainer; MongoReplicaSetContainer; MongoWithAuthContainer ]

    let mutable kurrentDBClient = Unchecked.defaultof<KurrentDBClient>

    /// A password that's broken by a connection string that doesn't escape it
    static member val MongoUsername = "rosetta"
    static member val MongoPassword = "p@ss:w/rd?#%"

    member _.KurrentDBConnectionString =
        sprintf "kurrentdb://admin:changeit@%s:%d?tls=false" KurrentDBContainer.Hostname (KurrentDBContainer.GetMappedPublicPort(2113))

    member _.KurrentDBClient = kurrentDBClient

    member _.RavenDBUrl = RavenDBContainer.GetConnectionString()

    member _.MongoReplicaSetUrl =
        // Straight to the one member: the replica set names it by an address that's only valid inside Docker
        let builder = MongoUrlBuilder(MongoReplicaSetContainer.GetConnectionString())
        builder.DirectConnection <- true
        builder.ToMongoUrl()

    member _.MongoWithAuthHost = MongoWithAuthContainer.Hostname
    member _.MongoWithAuthPort = int (MongoWithAuthContainer.GetMappedPublicPort(27017))

    /// The configuration the Rosetta factories read, pointing at the containers
    member this.Configuration(ravenDBDatabase: string, ?extra: (string * string) list) : IConfiguration =
        let settings = [
            "KurrentDB:ConnectionString", this.KurrentDBConnectionString
            "RavenDB:Urls", this.RavenDBUrl
            "RavenDB:DatabaseName", ravenDBDatabase ]
        ConfigurationBuilder()
            .AddInMemoryCollection(settings @ defaultArg extra [] |> Seq.map KeyValuePair.Create)
            .Build()

    member private this.CreateMongoUser() =
        task {
            // As root, the module's default user: a user whose password must be escaped in a connection string
            use root = new MongoClient(MongoWithAuthContainer.GetConnectionString())
            let createUser =
                BsonDocument([
                    BsonElement("createUser", BsonString Databases.MongoUsername)
                    BsonElement("pwd", BsonString Databases.MongoPassword)
                    BsonElement("roles", BsonArray([ BsonDocument("role", BsonString "readWriteAnyDatabase").Add("db", BsonString "admin") ])) ])
            let! _ = root.GetDatabase("admin").RunCommandAsync<BsonDocument>(BsonDocumentCommand<BsonDocument>(createUser))
            ()
        }

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(task {
                do! Containers |> List.map (fun container -> container.StartAsync()) |> Task.WhenAll
                kurrentDBClient <- new KurrentDBClient(KurrentDBClientSettings.Create this.KurrentDBConnectionString)
                do! this.CreateMongoUser()
            })

    interface IAsyncDisposable with
        member this.DisposeAsync() =
            ValueTask(task {
                if not (isNull (box kurrentDBClient)) then
                    do! kurrentDBClient.DisposeAsync()
                for container in Containers do
                    do! container.DisposeAsync()
            })

module AssemblyFixtures =
    [<assembly: AssemblyFixture(typeof<Databases>)>]
    do ()
