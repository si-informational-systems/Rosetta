namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Generic
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.FSharp.Reflection
open KurrentDB.Client
open SI.Rosetta.Common
open SI.Rosetta.Serialization

[<AutoOpen>]
module Eventually =
    /// Polls check until it's true, failing after timeout: projections catch up asynchronously
    let Eventually (timeout: TimeSpan) (description: string) (check: unit -> Task<bool>) =
        task {
            let deadline = DateTime.UtcNow + timeout
            let mutable passed = false
            while not passed do
                let! ok = check ()
                passed <- ok
                if not passed then
                    if DateTime.UtcNow > deadline then
                        raise (TimeoutException(sprintf "Not true after %O: %s" timeout description))
                    do! Task.Delay(200)
        }

module Events =
    /// An event as Rosetta writes it
    let Known (ev: IAggregateEvents) =
        let serialized = KurrentDBSerialization.Serialize(ev, Dictionary<string, obj>())
        EventData(Uuid.NewUuid(), serialized.EventClrName, serialized.Data, ReadOnlyMemory<byte> serialized.Metadata)

    /// An event whose record type is typeName, e.g. one the events DU doesn't have (anymore, or yet), without metadata
    let Written (typeName: string) (json: string) =
        EventData(Uuid.NewUuid(), typeName, Encoding.UTF8.GetBytes json, ReadOnlyMemory<byte>.Empty)

    /// An event as Rosetta 1.37 and earlier wrote it: the stored name and CLR type names in its metadata, and a trace
    let WrittenBy137 (aggregateType: Type) (ev: IAggregateEvents) =
        let typeName, json = EventEncoding.Encode ev
        let case, _ = FSharpValue.GetUnionFields(ev, ev.GetType())
        let metadata =
            JsonSerializer.Serialize(
                dict [ "EventClrTypeName", (GetUnionCaseType case).AssemblyQualifiedName
                       "EventsDiscriminatedUnionClrTypeName", ev.GetType().AssemblyQualifiedName
                       "DiscriminatedUnionCaseType", typeName
                       "AggregateClrTypeName", aggregateType.AssemblyQualifiedName
                       "traceparent", "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01" ])
        EventData(Uuid.NewUuid(), typeName, Encoding.UTF8.GetBytes json, ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes metadata))
