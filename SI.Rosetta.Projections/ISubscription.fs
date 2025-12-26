namespace SI.Rosetta.Projections

open System.Threading.Tasks
open SI.Rosetta.Common

type ISubscription<'TEvent when 'TEvent :> IAggregateEvents> =
    abstract member Name: string with get, set
    abstract member StreamName: string with get, set
    abstract member StartAsync: startingCheckpoint: uint64 -> Task
    abstract member EventReceived: ('TEvent * uint64 -> Task<unit>) with get, set
