namespace SI.Rosetta.Projections

open System
open SI.Rosetta.Common

type IProjectionHandlerFactory =
    abstract member Create<'TEvent when 'TEvent :> IEvents> : handlerType: Type -> IProjectionHandler<'TEvent>