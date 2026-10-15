namespace SI.Rosetta.Persistence.EventStore

open KurrentDB.Client
open Microsoft.Extensions.Configuration

module EventStoreFactory =
    let private CheckEventStoreAvailable(client: KurrentDBClient) =
        (client.GetStreamMetadataAsync("$ce-Any")).Result |> ignore

    let InitializeEventStore (config: IConfiguration) =
        let settings = KurrentDBClientSettings.Create(config.["EventStoreDB:ConnectionString"])
        let client = new KurrentDBClient(settings)
        CheckEventStoreAvailable client
        client

    let InitializeProjectionManagementClient (config: IConfiguration) = 
        let settings = KurrentDBClientSettings.Create(config.GetSection("EventStoreDB:ConnectionString").Value)
        let managementClient = new KurrentDBProjectionManagementClient(settings)
        managementClient
