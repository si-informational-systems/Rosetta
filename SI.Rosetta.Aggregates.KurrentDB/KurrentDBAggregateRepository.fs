namespace SI.Rosetta.Aggregates.KurrentDB

open System
open System.Collections.Generic
open KurrentDB.Client
open SI.Rosetta.Aggregates
open FSharp.Control
open SI.Rosetta.Common
open SI.Rosetta.Serialization

type KurrentDBAggregateRepository(client: KurrentDBClient) =
    member private this.ToEventData (event: IAggregateEvents) (headers: IDictionary<string, obj>) =
        let serializedEvent = KurrentDBSerialization.Serialize(event, headers)
        EventData(Uuid.NewUuid(), serializedEvent.EventClrName, serializedEvent.Data, ReadOnlyMemory<byte> serializedEvent.Metadata)

    member private this.SaveAggregate<'TAggregate, 'TAggregateState
                            when 'TAggregate :> IAggregate<'TAggregateState>> 
                            (aggregate: 'TAggregate) = 
        task {
            let commitHeaders = 
                let dict = Dictionary<string, obj>()
                dict.Add(KurrentDBSerialization.AggregateClrTypeNameHeader, aggregate.GetType().AssemblyQualifiedName :> obj)
                dict

            let newEvents = aggregate.Changes |> Seq.toList
            let originalVersion = aggregate.Version - int64 newEvents.Length
            let expectedState =
                if originalVersion = 0L then StreamState.NoStream
                else StreamState.StreamRevision(uint64 (originalVersion - 1L))

            let eventData = newEvents |> List.map (fun e -> this.ToEventData e commitHeaders)

            let! result =
                client.AppendToStreamAsync(aggregate.Id, expectedState, eventData).ConfigureAwait(false)
            aggregate.Changes.Clear()
        }

    member private this.TrySaveAggregate<'TAggregate, 'TAggregateState
                                when 'TAggregate :> IAggregate<'TAggregateState>> 
                                (aggregate: 'TAggregate) =
        task {
            try
                do! this.SaveAggregate<'TAggregate, 'TAggregateState>(aggregate).ConfigureAwait(false)
            with
            | :? AggregateException as ex when (ex.InnerException :? WrongExpectedVersionException) -> 
                raise (ConcurrencyException ex.Message)
            | :? WrongExpectedVersionException as ex ->
                raise (ConcurrencyException ex.Message)
        }

    interface IEventSourcedAggregateRepository with
        member this.StoreAsync<'TAggregate, 'TAggregateState
                                when 'TAggregate :> IAggregate<'TAggregateState>> 
                                aggregate = 
            task {
                do! this.TrySaveAggregate<'TAggregate, 'TAggregateState>(aggregate).ConfigureAwait(false)
            }

        member this.GetAsync<'TAggregate, 'TAggregateState, 'TEvents 
                                when 'TAggregate : (new : unit -> 'TAggregate) 
                                and 'TAggregate :> IAggregate<'TAggregateState>
                                and 'TAggregate : not struct
                                and 'TAggregateState : (new : unit -> 'TAggregateState)
                                and 'TAggregateState :> IAggregateStateInstance<'TEvents>
                                and 'TEvents :> IAggregateEvents>
            (id: string, version: int64) =
            task {
                let state = new 'TAggregateState()
                let aggregate = new 'TAggregate()

                try
                    let events = client.ReadStreamAsync(Direction.Forwards, id, StreamPosition.Start, version)
                    
                    do! events
                        |> AsyncSeq.takeWhile (fun _ -> state.Version <> version)
                        |> AsyncSeq.iter (fun resolvedEvent ->
                            let deserializedEvent = KurrentDBSerialization.Deserialize<'TEvents>(resolvedEvent)
                            state.Mutate deserializedEvent
                            )
                    
                    aggregate.SetState state
                    return Some aggregate

                with
                | :? AggregateException as ex when (ex.InnerException :? StreamNotFoundException) -> 
                    return None
                | :? StreamNotFoundException -> 
                    return None
                | ex -> return raise ex
            }