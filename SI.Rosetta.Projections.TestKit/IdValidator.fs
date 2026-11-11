namespace SI.Rosetta.Projections.TestKit

open System

[<AutoOpen>]
module IdValidator =
    /// A key value the stores key by: what a RavenDB or MongoDB Id or a PostgreSQL key column can be
    let ValidateIdType(id: obj) =
        match id with
        | :? string
        | :? int
        | :? int64
        | :? Guid
        | :? DateTime
        | :? DateTimeOffset
        | :? DateOnly -> ()
        | null -> raise (ArgumentException("A key value can't be null."))
        | id when id.GetType().IsEnum -> ()
        | _ -> raise (ArgumentException("Key value type not supported! Available options are: string; int; int64; Guid; DateTime; DateTimeOffset; DateOnly; an enum"))