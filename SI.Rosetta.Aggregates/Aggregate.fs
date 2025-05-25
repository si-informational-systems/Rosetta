namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Collections.Generic

[<AllowNullLiteral>]
[<AbstractClass>]
type Aggregate<'TAggregateState, 'TCommands, 'TEvents 
    when 'TAggregateState :> AggregateState<'TEvents>
    and 'TCommands :> ICommands
    and 'TEvents :> IEvents 
    and 'TAggregateState : (new : unit -> 'TAggregateState)>() =
    let mutable state = new 'TAggregateState()
    let changes = List<IEvents>()
    let publishedEvents = List<IEvents>()
        
    interface IAggregateInstance<'TCommands> with
        member this.Id = state.Id
        member this.Version = state.Version
        member this.Changes = changes
        member this.PublishedEvents = publishedEvents
        member this.SetState state = 
            this.State <- state :?> 'TAggregateState     
        member this.Execute command =
            this.Execute command
            
    member this.Id = state.Id
    member this.Version = state.Version
    member this.Changes = changes
    member this.PublishedEvents = publishedEvents
    member this.ShouldHandleIdempotency = state.Version > 0
    member this.State
        with get() = state
        and set value = state <- value
        
    member this.Apply event =
        state.Mutate event
        changes.Add event

    abstract Execute: command: 'TCommands -> unit