namespace SI.Rosetta.Projections

open SI.Rosetta.Common

type ISubscriptionFactory =
    abstract member Create<'TEvent when 'TEvent :> IAggregateEvents> : unit -> ISubscription<'TEvent>