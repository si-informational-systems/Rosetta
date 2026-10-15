namespace SI.Rosetta.IntegrationTests

open MongoDB.Bson
open MongoDB.Driver
open Xunit
open SI.Rosetta.Persistence.MongoDB

type MongoConfigTests(databases: Databases) =
    let Ping (config: MongoConfig) =
        task {
            use client = MongoClientFactory.CreateClientWithoutDatabase config
            let! reply = client.GetDatabase(config.DatabaseName).RunCommandAsync<BsonDocument>(BsonDocumentCommand<BsonDocument>(BsonDocument("ping", BsonInt32 1)))
            Assert.Equal(1.0, reply.["ok"].ToDouble())
        }

    [<Fact>]
    member this.``Connects with a password the connection string must escape``() =
        let config =
            databases.Configuration("unused", [
                "MongoDB:Host", databases.MongoWithAuthHost
                "MongoDB:Port", string databases.MongoWithAuthPort
                "MongoDB:DatabaseName", "Rosetta-MongoConfigTests"
                "MongoDB:Username", Databases.MongoUsername
                "MongoDB:Password", Databases.MongoPassword
                "MongoDB:AuthSource", "admin" ])
        Ping (MongoConfig.FromConfiguration config)

    [<Fact>]
    member this.``A ConnectionString is used as it is, and names the database when DatabaseName isn't set``() =
        // The default user is in admin, while a database in the URL would otherwise be the one authenticated against
        let url = MongoUrlBuilder(databases.MongoReplicaSetUrl.ToString(), DatabaseName = "Rosetta-FromConnectionString", AuthenticationSource = "admin")
        let config = MongoConfig.FromConfiguration(databases.Configuration("unused", [ "MongoDB:ConnectionString", url.ToString() ]))

        Assert.Equal("Rosetta-FromConnectionString", config.DatabaseName)
        Ping config
