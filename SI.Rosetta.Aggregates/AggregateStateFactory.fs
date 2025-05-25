namespace SI.Rosetta.Aggregates

open System
open System.Collections.Concurrent
open SI.Rosetta.Common

type AggregateStateFactory private () =
    static let CachedTypes = ConcurrentDictionary<Type, Type>()
    
    static member CreateStateFor<'TEvent when 'TEvent :> IAggregateEvents>(aggregateType: Type) =
        let mutable aggStateType = Unchecked.defaultof<Type>
        if not (CachedTypes.TryGetValue(aggregateType, &aggStateType)) then
            let assemblyContainingTheAggregate = aggregateType.Assembly
            let aggStateTypeName = $"{aggregateType.FullName}State"
            aggStateType <- assemblyContainingTheAggregate.GetType aggStateTypeName
            CachedTypes.TryAdd(aggregateType, aggStateType) |> ignore
            
        Activator.CreateInstance aggStateType :?> AggregateState<'TEvent> 