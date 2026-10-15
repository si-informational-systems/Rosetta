namespace SI.Rosetta.Projections

open System
open SI.Rosetta.Common

/// A projection of 'TEvent: Projection<'TEvent> implements it
type IProjectionOf<'TEvent when 'TEvent :> IAggregateEvents> =
    /// The events DU the projection projects. A member, not an empty marker: an IAmHandledBy of another DU compiles only
    /// when nothing is left for the projection to implement, and Projection<'TEvent> implements this for its own DU only
    abstract member EventsType: Type

/// Declares a Handler of the projection. Only a projection of 'TEvent can declare it: a Handler of another events DU fails
/// to compile on this declaration, with FS0366 "No implementation was given for 'abstract IProjectionOf.EventsType…'"
type IAmHandledBy<'THandler, 'TEvent 
    when 'THandler :> IProjectionHandler<'TEvent>
    and 'TEvent :> IAggregateEvents> =
    inherit IProjectionOf<'TEvent> 