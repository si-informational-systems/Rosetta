namespace SI.Rosetta.Projections.EventStore

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FSharp.Control
open System.Reflection
open KurrentDB.Client
open SI.Rosetta.Persistence.EventStore

type CustomProjectionAction =
    | Create
    | Update of reason: string
    /// The stored definition couldn't be read, so it's re-submitted to be safe
    | UpdateUnverified of error: string
    | Unchanged

module CustomProjectionSync =
    /// What a Custom Projection needs on startup, from its KurrentDB status (None when it doesn't exist) and stored definition
    let Decide (query: string) (status: string option) (stored: Result<StoredProjectionDefinition, string>) =
        match status, stored with
        | None, _ -> Create
        // Re-submitting restarts it, as every startup used to
        | Some status, _ when status.StartsWith("Faulted", StringComparison.OrdinalIgnoreCase) ->
            Update (sprintf "it was %s" status)
        | Some _, Error error -> UpdateUnverified error
        | Some _, Ok definition when definition.Query <> query -> Update "its definition changed"
        | Some _, Ok definition when not definition.EmitEnabled -> Update "emit was disabled"
        | Some _, Ok _ -> Unchanged

type IESCustomJSProjectionsFactory =
    abstract member CreateCustomProjections: Assembly -> Task

type ESCustomJSProjectionsFactory(conf: IConfiguration, logger: ILogger<ESCustomJSProjectionsFactory>) =
    let ProjectionManagementClient = EventStoreFactory.InitializeProjectionManagementClient conf
    let HttpApi = ESProjectionsHttpApi(conf)

    member private this.BuildProjectionDefinitions (assembly: Assembly) =
        let projections = CustomProjectionDiscovery.DiscoverCustomProjections assembly
        let projectDefinitions = Dictionary<string, string>()

        for parameters in projections do
            let projectionDef = EventStoreProjectionBuilder.BuildProjectionDefinition parameters
            projectDefinitions.Add(projectionDef.Name, projectionDef.Source)

        projectDefinitions

    interface IESCustomJSProjectionsFactory with
        member this.CreateCustomProjections(assembly: Assembly) =
            task {
                let projectDefinitions = this.BuildProjectionDefinitions assembly

                let! existingProjections =
                    ProjectionManagementClient.ListAllAsync()
                    |> TaskSeq.toListAsync

                let statuses =
                    existingProjections
                    |> Seq.map (fun p -> p.Name, p.Status)
                    |> Map.ofSeq

                for KeyValue(name, query) in projectDefinitions do
                    let status = statuses |> Map.tryFind name
                    let! stored =
                        match status with
                        | None -> Task.FromResult(Error "it doesn't exist yet")
                        | Some _ -> HttpApi.TryGetDefinitionAsync(name)

                    match CustomProjectionSync.Decide query status stored with
                    | Create ->
                        // V1 explicitly: the V2 engine is still experimental, and it's chosen only at creation
                        do! ProjectionManagementClient.CreateContinuousAsync(name, query, engineVersion = ProjectionEngineVersion.V1).ConfigureAwait(false)
                        // Continuous projections created over gRPC have emit disabled, which linkTo needs
                        do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
                        logger.LogInformation("Custom projection {Name} CREATED.", name)
                    | Update reason ->
                        do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
                        logger.LogInformation("Custom projection {Name} UPDATED: {Reason}.", name, reason)
                    | UpdateUnverified error ->
                        do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
                        logger.LogWarning("Custom projection {Name} UPDATED without comparing, its definition couldn't be read: {Error}", name, error)
                    | Unchanged ->
                        logger.LogInformation("Custom projection {Name} is up to date.", name)
            }
