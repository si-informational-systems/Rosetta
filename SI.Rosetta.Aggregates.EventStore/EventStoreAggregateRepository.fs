namespace SI.Rosetta.Aggregates.EventStore

open System
open System.Collections.Generic
open EventStore.Client
open SI.Rosetta.Aggregates
open FSharp.Control
open SI.Rosetta.Common
open SI.Rosetta.Serialization

type EventStoreAggregateRepository(client: EventStoreClient) =
    member private this.ToEventData (event: IAggregateEvents) (headers: IDictionary<string, obj>) =
        let serializedEvent = EventStoreSerialization.Serialize(event, headers)
        EventData(Uuid.NewUuid(), serializedEvent.EventClrName, serializedEvent.Data, ReadOnlyMemory<byte> serializedEvent.Metadata)

    member private this.SaveAggregate (aggregate: IAggregate) = 
        task {
            let commitHeaders = 
                let dict = Dictionary<string, obj>()
                dict.Add(EventStoreSerialization.AggregateClrTypeNameHeader, aggregate.GetType().AssemblyQualifiedName :> obj)
                dict

            let newEvents = aggregate.Changes |> Seq.toList
            let originalVersion = aggregate.Version - int64 newEvents.Length
            let expectedRevision = 
                if originalVersion = 0L then StreamRevision.None 
                else StreamRevision.FromInt64(originalVersion - 1L)
            
            let eventData = newEvents |> List.map (fun e -> this.ToEventData e commitHeaders)
            
            let! result = 
                client.AppendToStreamAsync(aggregate.Id, expectedRevision, eventData).ConfigureAwait(false)
            aggregate.Changes.Clear()
        }

    member private this.TrySaveAggregate (aggregate: IAggregate) =
        task {
            try
                do! this.SaveAggregate(aggregate).ConfigureAwait(false)
            with
            | :? AggregateException as ex when (ex.InnerException :? WrongExpectedVersionException) -> 
                raise (ConcurrencyException ex.Message)
            | :? WrongExpectedVersionException as ex ->
                raise (ConcurrencyException ex.Message)
        }

    interface IEventSourcedAggregateRepository with
        member this.StoreAsync aggregate = 
            task {
                do! this.TrySaveAggregate(aggregate).ConfigureAwait(false)
            }

        member this.GetAsync<'TAggregate, 'TEvents 
                                    when 'TAggregate :> IAggregate 
                                    and 'TAggregate : (new : unit -> 'TAggregate)
                                    and 'TAggregate : not struct
                                    and 'TEvents :> IAggregateEvents>
            (id: string, version: int64) =
            task {
                let aggregateType = typeof<'TAggregate>
                let instanceOfState = AggregateStateFactory.CreateStateFor<'TEvents> aggregateType
                let aggregate = new 'TAggregate()

                try
                    let events = client.ReadStreamAsync(Direction.Forwards, id, StreamPosition.Start, version)
                    let asyncEvents = AsyncSeq.ofAsyncEnum events
                    
                    do! asyncEvents
                        |> AsyncSeq.takeWhile (fun _ -> instanceOfState.Version <> version)
                        |> AsyncSeq.iter (fun resolvedEvent ->
                            let deserializedEvent = EventStoreSerialization.Deserialize<'TEvents>(resolvedEvent)
                            instanceOfState.Mutate deserializedEvent
                            )
                    
                    aggregate.SetState instanceOfState
                    return Some aggregate

                with
                | :? AggregateException as ex when (ex.InnerException :? StreamNotFoundException) -> 
                    return None
                | :? StreamNotFoundException -> 
                    return None
                | ex -> return raise ex
            }