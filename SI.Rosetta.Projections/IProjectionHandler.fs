namespace SI.Rosetta.Projections

open System.Threading.Tasks
open SI.Rosetta.Common

type IProjectionHandler<'TEvent when 'TEvent :> IEvents> =
    abstract member Handle: event: 'TEvent * checkpoint: uint64 -> Task 