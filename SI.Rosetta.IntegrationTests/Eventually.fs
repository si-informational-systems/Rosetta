namespace SI.Rosetta.IntegrationTests

open System
open System.Collections.Generic
open System.Text
open System.Threading.Tasks
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

    /// An event whose record type is typeName, e.g. one the events DU doesn't have (anymore, or yet)
    let Written (typeName: string) (json: string) =
        let metadata = sprintf """{"%s":"%s"}""" KurrentDBSerialization.DiscriminatedUnionCaseTypeHeader typeName
        EventData(Uuid.NewUuid(), typeName, Encoding.UTF8.GetBytes json, ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes metadata))
