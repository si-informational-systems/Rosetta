namespace SI.Rosetta.Projections

open SI.Rosetta.Common

type IAmHandledBy<'THandler, 'TEvent 
    when 'THandler :> IProjectionHandler<'TEvent>
    and 'TEvent :> IEvents> 
    = interface end 