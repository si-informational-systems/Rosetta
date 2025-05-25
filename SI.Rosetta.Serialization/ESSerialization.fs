namespace SI.Rosetta.Serialization

open SI.Rosetta.Common

module EventStoreSerialization =
    open Newtonsoft.Json
    open Newtonsoft.Json.Linq
    open Newtonsoft.Json.Converters
    open System.Collections.Generic
    open System.Text
    open Microsoft.FSharp.Reflection
    open EventStore.Client
    
    let AggregateClrTypeNameHeader = "AggregateClrTypeName"
    let EventClrTypeNameHeader = "EventClrTypeName"
    let EventsDUClrTypeNameHeader = "EventsDiscriminatedUnionClrTypeName"
    let DiscriminatedUnionCaseTypeHeader = "DiscriminatedUnionCaseType"

    let JsonSettings = 
        let settings = JsonSerializerSettings()
        settings.Converters.Add(new StringEnumConverter())
        settings

    type EventStoreSerializedEvent = {
        EventClrName: string
        Data: byte[]
        Metadata: byte[]
    }

    let Serialize(event: IEvents, headers: IDictionary<string, obj>) : EventStoreSerializedEvent =
        let serializedDU = JsonConvert.SerializeObject(event, JsonSettings)
        let jObject = JObject.Parse serializedDU
        let actualEventData = jObject.["Fields"].[0]
        let actualEventDataString = actualEventData.ToString()
        let data = Encoding.UTF8.GetBytes actualEventDataString
        
        let case, _ = FSharpValue.GetUnionFields(event, event.GetType())
        let fieldType = GetUnionCaseType case
        
        let eventHeaders = 
            let dict = Dictionary<string, obj> headers
            dict.Add(EventClrTypeNameHeader, fieldType.AssemblyQualifiedName)
            dict.Add(EventsDUClrTypeNameHeader, event.GetType().AssemblyQualifiedName)
            dict.Add(DiscriminatedUnionCaseTypeHeader, fieldType.Name)
            dict
        let metadata = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(eventHeaders, JsonSettings))
        
        let serialized: EventStoreSerializedEvent = {
            EventClrName = fieldType.Name
            Data = data
            Metadata = metadata
        }
        serialized

    let Deserialize<'TEvent when 'TEvent :> IEvents>(resolvedEvent: ResolvedEvent) : 'TEvent =
        let metadata = resolvedEvent.Event.Metadata.ToArray()
        let data = resolvedEvent.Event.Data.ToArray()

        let metadataObj = JsonConvert.DeserializeObject<Dictionary<string, obj>>(Encoding.UTF8.GetString(metadata))
        let caseTypeName = metadataObj.[DiscriminatedUnionCaseTypeHeader].ToString()
        let json = Encoding.UTF8.GetString(data)
        
        let targetType = typeof<'TEvent>
        let unionCase = FSharpType.GetUnionCases(targetType)
                       |> Array.find (fun c -> 
                           let fieldType = GetUnionCaseType c
                           fieldType.Name = caseTypeName)
        
        let eventObj = JsonConvert.DeserializeObject(json, GetUnionCaseType unionCase, JsonSettings)
        
        FSharpValue.MakeUnion(unionCase, [|eventObj|]) :?> 'TEvent
