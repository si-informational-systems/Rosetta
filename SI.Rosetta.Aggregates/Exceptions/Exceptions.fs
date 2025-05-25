namespace SI.Rosetta.Aggregates

open System

type ConcurrencyException(message: string) =
    inherit Exception(message) 