namespace SI.Rosetta.Aggregates

open System
open SI.Rosetta.Common

type IAggregateState =
    abstract member Id: string
    abstract member Version: int64 with get, set
    /// The events DU the state is built from, which event-sourced repositories read its events as
    abstract member EventsType: Type
    /// Applies one of EventsType's events and counts it in Version
    abstract member Mutate: event: IAggregateEvents -> unit

/// A state built from 'TEvents: Apply takes only that DU's events
type IAggregateStateInstance<'TEvents
    when 'TEvents :> IAggregateEvents> =
    inherit IAggregateState
