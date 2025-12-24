namespace SI.Rosetta.Persistence.EventStore

open EventStore.Client
open Microsoft.Extensions.Configuration

module EventStoreFactory =
    let private CheckEventStoreAvailable(client: EventStoreClient) =
        (client.GetStreamMetadataAsync("$ce-Any")).Result |> ignore

    let InitializeEventStore (config: IConfiguration) =
        let settings = EventStoreClientSettings.Create(config.["EventStoreDB:ConnectionString"])
        let client = new EventStoreClient(settings)
        CheckEventStoreAvailable client
        client

    let InitializeProjectionManagementClient (config: IConfiguration) = 
        let settings = EventStoreClientSettings.Create(config.GetSection("EventStoreDB:ConnectionString").Value)
        let managementClient = new EventStoreProjectionManagementClient(settings)
        managementClient
