namespace SI.Rosetta.Common

open Microsoft.FSharp.Reflection
open System

[<AutoOpen>]
module UnionUtils =
    let IsUnion (t: Type) =
        FSharpType.IsUnion t && not t.IsNested

    let GetUnionCaseType (unionCaseInfo: UnionCaseInfo) =
        unionCaseInfo.GetFields().[0].PropertyType
