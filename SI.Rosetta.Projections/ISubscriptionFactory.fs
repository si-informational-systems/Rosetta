namespace SI.Rosetta.Projections

open SI.Rosetta.Common

type ISubscriptionFactory =
    abstract member Create<'TEvent when 'TEvent :> IEvents> : unit -> ISubscription<'TEvent>