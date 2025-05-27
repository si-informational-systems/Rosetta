namespace SI.Rosetta.Projections.TestKit

open System

[<AutoOpen>]
module IdValidator =
    let ValidateIdType(id: obj) =
        match id with
        | :? string -> ()
        | :? int -> ()
        | :? int64 -> ()
        | :? Guid -> ()
        | _ -> raise (ArgumentException("Id type not supported! Available options are: string; int; int64; Guid"))