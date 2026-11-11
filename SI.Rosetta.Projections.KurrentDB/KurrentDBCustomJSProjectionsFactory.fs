namespace SI.Rosetta.Projections.KurrentDB

open System
open System.Collections.Generic
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open FSharp.Control
open KurrentDB.Client

type CustomProjectionAction =
    | Create
    | Update of reason: string
    /// Its definition changed: its stream is linked again from the start of its source streams
    | Relink
    /// The stored definition couldn't be read, so it's re-submitted to be safe
    | UpdateUnverified of error: string
    | Unchanged

module CustomProjectionSync =
    /// What a Custom Projection needs on startup, from its KurrentDB status (None when it doesn't exist) and stored definition
    let Decide (query: string) (status: string option) (stored: Result<StoredProjectionDefinition, string>) =
        match status, stored with
        | None, _ -> Create
        // Before Faulted: relinking restarts it too, and its stream must be linked again
        | Some _, Ok definition when definition.Query <> query -> Relink
        // Re-submitting restarts it, as every startup used to
        | Some status, _ when status.StartsWith("Faulted", StringComparison.OrdinalIgnoreCase) ->
            Update (sprintf "it was %s" status)
        | Some _, Error error -> UpdateUnverified error
        | Some _, Ok definition when not definition.EmitEnabled -> Update "emit was disabled"
        | Some _, Ok _ -> Unchanged

type IKurrentDBCustomJSProjectionsFactory =
    /// Creates or updates the custom projections of every ICustomProjectionEvents DU in the assembly
    abstract member CreateCustomProjections: Assembly -> Task
    /// Creates or updates these custom projections
    abstract member CreateCustomProjections: projections: KurrentDBProjectionParameters seq -> Task

/// Keeps KurrentDB's custom JS projections as the code defines them, on startup:
/// - a new one is created (continuous, engine V1, emit enabled);
/// - one whose definition changed is relinked: stopped, its cp- stream soft-deleted, its query updated, reset and
///   started again, so it links its source streams' whole history again. The Rosetta projections reading that stream
///   rebuild their read models when they start (KurrentDBSubscription sees the stream's truncation);
/// - one the code no longer defines (removed, or renamed: a new name is a new projection) is stopped and deleted, and
///   its cp- stream soft-deleted.
/// What it defined is recorded in the stream RosettaCustomProjections-{owner}, so it only ever deletes the projections it
/// created itself: not another service's on the same KurrentDB, nor any made by hand. The owner is the application's
/// name unless KurrentDB:CustomProjectionsOwner sets it
type KurrentDBCustomJSProjectionsFactory(
    conf: IConfiguration,
    logger: ILogger<KurrentDBCustomJSProjectionsFactory>,
    projectionManagementClient: KurrentDBProjectionManagementClient,
    client: KurrentDBClient,
    owner: string) =
    let ProjectionManagementClient = projectionManagementClient
    let HttpApi = KurrentDBProjectionsHttpApi(conf)
    let Registry = "RosettaCustomProjections-" + owner

    [<Literal>]
    let RegistryEventType = "CustomProjectionsDefined"

    /// Owned by the entry assembly's name, unless KurrentDB:CustomProjectionsOwner says otherwise
    new(conf: IConfiguration, logger: ILogger<KurrentDBCustomJSProjectionsFactory>, projectionManagementClient: KurrentDBProjectionManagementClient, client: KurrentDBClient) =
        KurrentDBCustomJSProjectionsFactory(conf, logger, projectionManagementClient, client, KurrentDBCustomJSProjectionsFactory.DefaultOwner(conf, null))

    /// KurrentDB:CustomProjectionsOwner, else the application's name, else the entry assembly's
    static member DefaultOwner(conf: IConfiguration, applicationName: string) =
        match conf.["KurrentDB:CustomProjectionsOwner"] with
        | configured when not (String.IsNullOrWhiteSpace configured) -> configured.Trim()
        | _ when not (String.IsNullOrWhiteSpace applicationName) -> applicationName
        | _ ->
            match Assembly.GetEntryAssembly() with
            | null -> "Rosetta"
            | entry -> entry.GetName().Name

    /// The stream that records what this owner defined
    member this.RegistryStream = Registry

    /// The custom projections the owner defined last time: name → cp- stream
    member private this.ReadRegistryAsync() : Task<Map<string, string>> =
        task {
            let read = client.ReadStreamAsync(Direction.Backwards, Registry, StreamPosition.End, 1L)
            match! read.ReadState with
            | ReadState.StreamNotFound -> return Map.empty
            | _ ->
                let! events = read |> TaskSeq.toListAsync
                match events with
                | [] -> return Map.empty
                | latest :: _ ->
                    use document = JsonDocument.Parse(latest.Event.Data.ToArray())
                    return
                        document.RootElement.EnumerateArray()
                        |> Seq.map (fun projection -> projection.GetProperty("Name").GetString(), projection.GetProperty("Stream").GetString())
                        |> Map.ofSeq
        }

    member private this.WriteRegistryAsync(defined: Map<string, string>) : Task =
        task {
            let projections =
                defined
                |> Seq.map (fun (KeyValue(name, stream)) -> dict [ "Name", name; "Stream", stream ])
                |> Seq.toArray
            let data = EventData(Uuid.NewUuid(), RegistryEventType, ReadOnlyMemory<byte>(JsonSerializer.SerializeToUtf8Bytes projections))
            let! written = client.AppendToStreamAsync(Registry, StreamState.Any, [ data ]).ConfigureAwait(false)
            // Only the latest is read
            if written.NextExpectedStreamState = StreamState.StreamRevision 0UL then
                let! _ = client.SetStreamMetadataAsync(Registry, StreamState.Any, StreamMetadata(maxCount = Nullable 10)).ConfigureAwait(false)
                ()
        } :> Task

    /// Soft-deletes the stream: its links go, the events they point to stay. Whatever reads it from before is rebuilt
    member private this.DeleteStreamAsync(stream: string) : Task =
        task {
            try
                let! _ = client.DeleteAsync(stream, StreamState.Any).ConfigureAwait(false)
                ()
            with :? StreamNotFoundException -> ()
        } :> Task

    /// Links the stream again from the start: stopped, its stream deleted, the query updated, reset, started
    member private this.RelinkAsync(name: string, query: string, stream: string) : Task =
        task {
            do! ProjectionManagementClient.DisableAsync(name).ConfigureAwait(false)
            do! this.DeleteStreamAsync(stream)
            do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
            do! ProjectionManagementClient.ResetAsync(name).ConfigureAwait(false)
            do! ProjectionManagementClient.EnableAsync(name).ConfigureAwait(false)
        } :> Task

    /// Stops and deletes a projection the code no longer defines, and its stream
    member private this.RemoveAsync(name: string, stream: string, exists: bool) : Task =
        task {
            if exists then
                do! ProjectionManagementClient.DisableAsync(name).ConfigureAwait(false)
                do! HttpApi.DeleteProjectionAsync(name)
            do! this.DeleteStreamAsync(stream)
        } :> Task

    /// Makes KurrentDB's custom projections the ones defined (name → query, cp- stream), and deletes the ones previous
    /// (what the registry says this owner defined before) no longer has
    member private this.SyncAsync(defined: Map<string, string * string>, previous: Map<string, string>) : Task =
        task {
            let! existingProjections =
                ProjectionManagementClient.ListAllAsync()
                |> TaskSeq.toListAsync

            let statuses =
                existingProjections
                |> Seq.map (fun p -> p.Name, p.Status)
                |> Map.ofSeq

            for KeyValue(name, (query, stream)) in defined do
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
                | Relink ->
                    do! this.RelinkAsync(name, query, stream)
                    logger.LogInformation("Custom projection {Name} RELINKED: its definition changed, so {Stream} is linked again from the start.", name, stream)
                | Update reason ->
                    do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
                    logger.LogInformation("Custom projection {Name} UPDATED: {Reason}.", name, reason)
                | UpdateUnverified error ->
                    do! ProjectionManagementClient.UpdateAsync(name, query, emitEnabled = true).ConfigureAwait(false)
                    logger.LogWarning("Custom projection {Name} UPDATED without comparing, its definition couldn't be read: {Error}", name, error)
                | Unchanged ->
                    logger.LogInformation("Custom projection {Name} is up to date.", name)

            for KeyValue(name, stream) in previous do
                if not (defined.ContainsKey name) then
                    do! this.RemoveAsync(name, stream, statuses.ContainsKey name)
                    logger.LogInformation("Custom projection {Name} DELETED, with {Stream}: the code no longer defines it.", name, stream)

            let current = defined |> Map.map (fun _ (_, stream) -> stream)
            if current <> previous then
                do! this.WriteRegistryAsync(current)
        } :> Task

    interface IKurrentDBCustomJSProjectionsFactory with
        member this.CreateCustomProjections(assembly: Assembly) =
            (this :> IKurrentDBCustomJSProjectionsFactory).CreateCustomProjections(CustomProjectionDiscovery.DiscoverCustomProjections assembly)

        member this.CreateCustomProjections(projections: KurrentDBProjectionParameters seq) : Task =
            task {
                let defined =
                    projections
                    |> Seq.map (fun parameters ->
                        let definition = KurrentDBProjectionBuilder.BuildProjectionDefinition parameters
                        definition.Name, (definition.Source, parameters.DestinationStreamName))
                    |> Map.ofSeq
                let! previous = this.ReadRegistryAsync()
                // Nothing defined, nothing created before: KurrentDB's projections, which may be off, aren't asked
                if not (defined.IsEmpty && previous.IsEmpty) then
                    do! this.SyncAsync(defined, previous)
            }
