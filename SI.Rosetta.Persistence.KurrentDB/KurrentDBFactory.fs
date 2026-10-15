namespace SI.Rosetta.Persistence.KurrentDB

open System
open KurrentDB.Client
open Microsoft.Extensions.Configuration

module KurrentDBFactory =
    let ConnectionStringKey = "KurrentDB:ConnectionString"

    let private CheckKurrentDBAvailable(client: KurrentDBClient) =
        (client.GetStreamMetadataAsync("$ce-Any")).Result |> ignore

    /// The client settings from KurrentDB:ConnectionString, which must be set
    let CreateSettings (config: IConfiguration) =
        match config.[ConnectionStringKey] with
        | connectionString when String.IsNullOrWhiteSpace connectionString ->
            raise (InvalidOperationException(sprintf "%s is missing from the configuration." ConnectionStringKey))
        | connectionString -> KurrentDBClientSettings.Create connectionString

    let InitializeKurrentDB (config: IConfiguration) =
        let settings = CreateSettings config
        let client = new KurrentDBClient(settings)
        CheckKurrentDBAvailable client
        client

    let InitializeProjectionManagementClient (config: IConfiguration) =
        let settings = CreateSettings config
        let managementClient = new KurrentDBProjectionManagementClient(settings)
        managementClient
