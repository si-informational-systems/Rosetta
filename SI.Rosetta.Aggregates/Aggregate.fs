namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Collections.Generic
open System.Runtime.CompilerServices

[<AbstractClass>]
type Aggregate<'TAggregateState, 'TCommands
        when 'TAggregateState :> IAggregateState
        and 'TAggregateState : (new : unit -> 'TAggregateState)
        and 'TCommands :> IAggregateCommands>() =

    member val State = new 'TAggregateState() with get, set
    member val Changes = List<IAggregateEvents>() with get, set
    member val PublishedEvents = List<IAggregateEvents>() with get, set

    interface IAggregate with
        member this.Id = this.State.Id
        member this.Version = this.State.Version
        member this.Changes = this.Changes
        member this.PublishedEvents = this.PublishedEvents

    interface IAggregate<'TAggregateState> with
        member this.GetState() =
            this.State

    interface IAggregateInstance<'TCommands> with
        member this.Execute command =
            this.Execute command
        member this.LoadAsync(repository, id) =
            task {
                match! repository.GetAsync<'TAggregateState>(id).ConfigureAwait(false) with
                | Some state ->
                    this.State <- state
                    return true
                | None -> return false
            }
        member this.StoreAsync(repository) =
            repository.StoreAsync<'TAggregateState>(this)

    member this.Id = this.State.Id
    member this.Version = this.State.Version
    member this.ShouldHandleIdempotency = this.State.Version > 0

    abstract Execute: command: 'TCommands -> unit

/// Apply is an extension, not a member of Aggregate, so that its constraint can tie the aggregate's state to the event's DU:
/// the state's events are the only ones it compiles with
[<Extension; AbstractClass; Sealed>]
type AggregateExtensions =
    /// Applies the event to the state and adds it to the Changes to store
    [<Extension>]
    static member Apply<'TAggregateState, 'TCommands, 'TEvents
            when 'TAggregateState :> IAggregateStateInstance<'TEvents>
            and 'TAggregateState : (new : unit -> 'TAggregateState)
            and 'TCommands :> IAggregateCommands
            and 'TEvents :> IAggregateEvents>
        (aggregate: Aggregate<'TAggregateState, 'TCommands>, event: 'TEvents) =
        aggregate.State.Mutate event
        aggregate.Changes.Add event
