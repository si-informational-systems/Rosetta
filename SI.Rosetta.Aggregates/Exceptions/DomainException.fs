namespace SI.Rosetta.Aggregates

open System

[<Serializable>]
type DomainException(?message: string) =
    inherit Exception(defaultArg message null)
    let mutable name = ""
    
    member this.Name
        with get() = name
        and private set value = name <- value
        
    static member Named(name: string, message: string) =
        let error = DomainException message
        error.Name <- name
        error