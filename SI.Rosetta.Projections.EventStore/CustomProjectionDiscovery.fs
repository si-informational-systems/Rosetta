namespace SI.Rosetta.Projections.EventStore

open System
open System.Reflection
open Microsoft.FSharp.Reflection
open SI.Rosetta.Common
open SI.Rosetta.Projections

module CustomProjectionDiscovery =
    let private GetProjectionCaseTypes (projectionType: Type) =
        FSharpType.GetUnionCases projectionType
        |> Array.map (fun case -> GetUnionCaseType case)
            
    let private GetStreamName (projectionType: Type) =
        let attr = projectionType.GetCustomAttributes(typeof<CustomProjectionStream>, false)
        if attr.Length = 0 then
            raise (InvalidOperationException(sprintf "Type %s must have CustomProjectionStream attribute" projectionType.Name))
        let streamName = (attr.[0] :?> CustomProjectionStream).Name
        if not (streamName.StartsWith("cp-")) then
            raise (InvalidOperationException(sprintf "EventStore Custom Projection Stream name must start with 'cp-'. Invalid Stream name: %s" streamName))
        streamName

    let private GetProjectionName (projectionType: Type) =
        let streamName = GetStreamName projectionType
        streamName.Substring("cp-".Length) + "CustomProjection"

    let private FindAggregateEventDUs (assemblies: Assembly array) =
        assemblies
        |> Array.collect (fun assembly -> 
            assembly.GetTypes()
            |> Array.filter (fun t -> 
                t.GetInterfaces() 
                |> Array.exists (fun i -> i = typeof<IAggregateEvents>)
                && FSharpType.IsUnion t))

    let private FindAggregateEventDUForType (eventType: Type) (aggregateEventDUs: Type array) =
        aggregateEventDUs
        |> Array.tryFind (fun du ->
            FSharpType.GetUnionCases du
            |> Array.exists (fun case ->
                GetUnionCaseType case = eventType))
        |> Option.map (fun du -> 
            let name = du.Name
            if name.EndsWith("Events") then
                name.Substring(0, name.Length - "Events".Length)
            else name)

    let private FindStreamNameConstant (assembly: Assembly) (aggregateName: string) =
        let expectedStreamNameConstant = aggregateName + "Stream"
        let field = 
            assembly.GetModules()
            |> Array.collect (fun m -> m.GetTypes())
            |> Array.tryPick (fun t -> 
                let f = t.GetField(expectedStreamNameConstant, 
                    BindingFlags.Public ||| 
                    BindingFlags.Static ||| 
                    BindingFlags.FlattenHierarchy)
                if f <> null && f.FieldType = typeof<string> then Some f else None)
            
        match field with
        | Some f -> f.GetValue(null) :?> string
        | None -> raise (InvalidOperationException(sprintf "Stream constant '%s' not found in assembly %s" expectedStreamNameConstant assembly.FullName))
    
    let private GetCustomProjectionEventsDUTypes (assembly: Assembly) =
        assembly.GetTypes()
        |> Array.filter (fun t -> 
            t.GetInterfaces() 
            |> Array.exists (fun i -> i = typeof<ICustomProjectionEvents>)
            && IsUnion t)

    let private DiscoverSourceStreams (callingAssembly: Assembly, eventTypes: Type array) =
        let aggregateEventDUsAssemblies = eventTypes |> Array.map (fun t -> t.Assembly) |> Array.distinct
        let aggregateEventDUs = FindAggregateEventDUs aggregateEventDUsAssemblies
        
        let sourceAggregateStreams =
            eventTypes
            |> Array.choose (fun eventType -> 
                FindAggregateEventDUForType eventType aggregateEventDUs)
            |> Array.distinct
            |> Array.map (fun aggregateName -> FindStreamNameConstant callingAssembly aggregateName)
            |> Array.toList
        
        sourceAggregateStreams
            
    let DiscoverCustomProjections (assembly: Assembly) : EventStoreProjectionParameters array =
        let customProjectionDUTypes = GetCustomProjectionEventsDUTypes assembly
        let result = 
            customProjectionDUTypes
            |> Array.map (fun projType ->
                let eventTypes = GetProjectionCaseTypes projType
        
                let param : EventStoreProjectionParameters = { 
                    ProjectionName = GetProjectionName projType
                    DestinationStreamName = GetStreamName projType
                    AggregateEventsStreamNames = DiscoverSourceStreams(assembly, eventTypes)
                    EventsToInclude = eventTypes
                }
                param)
        result