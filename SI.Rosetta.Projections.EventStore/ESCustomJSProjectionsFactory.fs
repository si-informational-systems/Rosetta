namespace SI.Rosetta.Projections.EventStore

open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open FSharp.Control
open System.Reflection
open SI.Rosetta.Persistence.EventStore

type IESCustomJSProjectionsFactory =
    abstract member CreateCustomProjections: Assembly -> Task

type ESCustomJSProjectionsFactory(conf: IConfiguration) =
    let ProjectionManagementClient = EventStoreFactory.InitializeProjectionManagementClient conf
    
    member private this.GetNewProjectionNames (projections: Dictionary<string, string>, existing: string list) =
        projections 
        |> Seq.filter (fun kv -> not (existing |> List.contains kv.Key))
        |> Seq.map (fun kv -> kv.Key, kv.Value)
        |> dict

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

                let projectionNames = 
                    existingProjections 
                    |> Seq.map (fun p -> p.Name)
                    |> Seq.toList

                let newProjections = this.GetNewProjectionNames(projectDefinitions, projectionNames)

                for KeyValue(name, code) in newProjections do
                    do! ProjectionManagementClient.CreateContinuousAsync(name, code).ConfigureAwait(false)

                for KeyValue(name, code) in projectDefinitions do
                    do! ProjectionManagementClient.UpdateAsync(name, code, emitEnabled = true).ConfigureAwait(false)
            }