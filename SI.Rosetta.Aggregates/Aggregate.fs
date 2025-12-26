namespace SI.Rosetta.Aggregates

open SI.Rosetta.Common
open System.Collections.Generic

[<AbstractClass>]
type Aggregate<'TAggregateState, 'TCommands, 'TEvents
        when 'TAggregateState :> IAggregateStateInstance<'TEvents>
        and 'TCommands :> IAggregateCommands
        and 'TEvents :> IAggregateEvents
        and 'TAggregateState : (new : unit -> 'TAggregateState)>() =

    member val State = new 'TAggregateState() with get, set
    member val Changes = List<IAggregateEvents>() with get, set
    member val PublishedEvents = List<IAggregateEvents>() with get, set
        
    interface IAggregateInstance<'TAggregateState, 'TCommands> with
        member this.Id = this.State.Id
        member this.Version = this.State.Version
        member this.Changes = this.Changes
        member this.PublishedEvents = this.PublishedEvents
        member this.SetState state = 
            this.State <- state
        member this.GetState() = 
            this.State
        member this.Execute command =
            this.Execute command
            
    member this.Id = this.State.Id
    member this.Version = this.State.Version
    member this.ShouldHandleIdempotency = this.State.Version > 0
        
    member this.Apply event =
        this.State.Mutate event
        this.Changes.Add event

    abstract Execute: command: 'TCommands -> unit
    