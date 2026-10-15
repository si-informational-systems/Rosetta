namespace SI.Rosetta.Aggregates.KurrentDB

open System
open System.Collections.Generic
open System.Runtime.ExceptionServices
open KurrentDB.Client
open SI.Rosetta.Aggregates
open FSharp.Control
open SI.Rosetta.Common
open SI.Rosetta.Serialization

/// Event-sourced aggregates in KurrentDB. Events are upcast while the aggregate is read; one its events DU has no case for
/// fails the read with UnknownEventException, as skipping it would rebuild the wrong state
type KurrentDBAggregateRepository(client: KurrentDBClient, upcasters: IEventUpcaster seq) =
    let Upcasters = Seq.toArray upcasters

    new(client: KurrentDBClient) = KurrentDBAggregateRepository(client, Seq.empty)

    member private this.ToEventData (event: IAggregateEvents) (headers: IDictionary<string, obj>) =
        let serializedEvent = KurrentDBSerialization.Serialize(event, headers)
        EventData(Uuid.NewUuid(), serializedEvent.EventClrName, serializedEvent.Data, ReadOnlyMemory<byte> serializedEvent.Metadata)

    member private this.SaveAggregate (aggregate: IAggregate) =
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
        member this.StoreAsync<'TAggregateState
                                when 'TAggregateState :> IAggregateState>
                                (aggregate: IAggregate<'TAggregateState>) =
            task {
                do! this.TrySaveAggregate(aggregate).ConfigureAwait(false)
            }

        member this.GetAsync<'TAggregateState
                                when 'TAggregateState : (new : unit -> 'TAggregateState)
                                and 'TAggregateState :> IAggregateState>
            (id: string, version: int64) =
            task {
                let state = new 'TAggregateState()

                try
                    let events = client.ReadStreamAsync(Direction.Forwards, id, StreamPosition.Start, version)

                    do! events
                        |> AsyncSeq.takeWhile (fun _ -> state.Version <> version)
                        |> AsyncSeq.iter (fun resolvedEvent ->
                            let deserializedEvent = KurrentDBSerialization.DeserializeAs state.EventsType Upcasters resolvedEvent
                            state.Mutate deserializedEvent
                            )

                    return Some state

                with
                | :? AggregateException as ex when (ex.InnerException :? StreamNotFoundException) -> 
                    return None
                | :? StreamNotFoundException ->
                    return None
                | :? AggregateException as ex when (ex.InnerException :? UnknownEventException) ->
                    ExceptionDispatchInfo.Throw ex.InnerException
                    return None
                | ex -> return raise ex
            }