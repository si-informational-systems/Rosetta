namespace SI.Rosetta.Persistence.KurrentDB

open System
open KurrentDB.Client
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions

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

    /// A client for KurrentDB:ConnectionString. It connects on first use
    let CreateClient (config: IConfiguration) =
        new KurrentDBClient(CreateSettings config)

    /// A client for KurrentDB:ConnectionString, after checking (and blocking until) KurrentDB answers
    let InitializeKurrentDB (config: IConfiguration) =
        let client = CreateClient config
        CheckKurrentDBAvailable client
        client

    let InitializeProjectionManagementClient (config: IConfiguration) =
        let settings = CreateSettings config
        let managementClient = new KurrentDBProjectionManagementClient(settings)
        managementClient

module KurrentDBServices =
    /// One KurrentDBClient and one KurrentDBProjectionManagementClient for the whole application, shared by Aggregates and
    /// Projections: a client multiplexes every call and subscription over its gRPC connection. They're created on first use,
    /// so registering doesn't connect, and disposed with the service provider. Clients the application registered first are kept
    let AddKurrentDBClients (services: IServiceCollection) (config: IConfiguration) =
        // A missing or invalid connection string fails now, not on first use
        KurrentDBFactory.CreateSettings config |> ignore
        services.TryAddSingleton<KurrentDBClient>(fun _ -> KurrentDBFactory.CreateClient config)
        services.TryAddSingleton<KurrentDBProjectionManagementClient>(fun _ -> KurrentDBFactory.InitializeProjectionManagementClient config)
