namespace SI.Rosetta.Common

open Pluralize.NET

[<AutoOpen>]
module Pluralizer =
    let PluralizeName(word: string) : string =
        let pluralizer = Pluralizer()
        if pluralizer.IsPlural(word) then word
        else
            let pluralized = pluralizer.Pluralize(word)
            pluralized




