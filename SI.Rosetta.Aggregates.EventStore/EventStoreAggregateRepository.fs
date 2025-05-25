namespace SI.Rosetta.Aggregates.EventStore

open System
open System.Collections.Generic
open EventStore.Client
open SI.Rosetta.Aggregates
open FSharp.Control
open SI.Rosetta.Common

open SI.Rosetta.Serialization
open System.Threading

type EventStoreAggregateRepository(client: EventStoreClient) =
    member private this.ToEventData (event: IEvents) (headers: IDictionary<string, obj>) =
        let serializedEvent = EventStoreSerialization.Serialize(event, headers)
        EventData(Uuid.NewUuid(), serializedEvent.EventClrName, serializedEvent.Data, ReadOnlyMemory<byte> serializedEvent.Metadata)

    member private this.SaveAggregate (aggregate: IAggregate) = 
        task {
            let commitHeaders = 
                let dict = Dictionary<string, obj>()
                dict.Add(EventStoreSerialization.AggregateClrTypeNameHeader, aggregate.GetType().AssemblyQualifiedName :> obj)
                dict

            let streamName = aggregate.Id
            let newEvents = aggregate.Changes |> Seq.toList
            let originalVersion = aggregate.Version - newEvents.Length
            let expectedRevision = 
                if originalVersion = 0 then StreamRevision.None 
                else StreamRevision.FromInt64(int64 (originalVersion - 1))
            
            let eventData = newEvents |> List.map (fun e -> this.ToEventData e commitHeaders)
            
            let! result = 
                client.AppendToStreamAsync(streamName, expectedRevision, eventData)
                |> Async.AwaitTask
            aggregate.Changes.Clear()
        }

    member private this.TrySaveAggregate (aggregate: IAggregate) =
        task {
            try
                do! this.SaveAggregate aggregate
            with
            | :? AggregateException as ex when (ex.InnerException :? WrongExpectedVersionException) -> 
                raise (ConcurrencyException ex.Message)
            | :? WrongExpectedVersionException as ex -> 
                raise (ConcurrencyException ex.Message)
        }

    interface IAggregateRepository with
        member this.StoreAsync aggregate = 
            task {
                do! this.TrySaveAggregate aggregate
            }

        member this.GetAsync<'TAggregate, 'TEvents 
                                    when 'TAggregate :> IAggregate 
                                    and 'TAggregate : (new : unit -> 'TAggregate)
                                    and 'TAggregate : null
                                    and 'TEvents :> IEvents>
            (id: string, version: int) =
            task {
                let streamName = id
                let aggregateType = typeof<'TAggregate>
                let instanceOfState = AggregateStateFactory.CreateStateFor<'TEvents> aggregateType
                let aggregate = new 'TAggregate()

                try
                    let events = client.ReadStreamAsync(Direction.Forwards, streamName, StreamPosition.Start, version)
                    let asyncEvents = AsyncSeq.ofAsyncEnum events
                    
                    do! asyncEvents
                        |> AsyncSeq.takeWhile (fun _ -> instanceOfState.Version <> version)
                        |> AsyncSeq.iter (fun resolvedEvent ->
                            let deserializedEvent = EventStoreSerialization.Deserialize<'TEvents>(resolvedEvent)
                            instanceOfState.Mutate deserializedEvent
                            )
                    
                    aggregate.SetState instanceOfState
                    return aggregate

                with
                | :? AggregateException as ex when (ex.InnerException :? StreamNotFoundException) -> 
                    return null
                | :? StreamNotFoundException -> 
                    return null
                | ex -> return raise ex
            }