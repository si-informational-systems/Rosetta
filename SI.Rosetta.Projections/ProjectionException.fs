namespace SI.Rosetta.Projections

open System

type ProjectionException(message: string, innerException: Exception) =
    inherit Exception(message, innerException)
    
    member val ProjectionName = "" with get, set
    member val SubscriptionStreamName = "" with get, set
    member val EventTypeName = "" with get, set 
    member val Checkpoint = 0UL with get, set