namespace SI.Rosetta.Aggregates.TestKit

open System
open System.Text
open System.Threading.Tasks
open Xunit.Sdk
open SI.Rosetta.Common
open SI.Rosetta.TestKit
open SI.Rosetta.Aggregates
open System.Collections.Generic

module OutputFormatting =
    
    let FormatHumanReadable (adj: string) (text: string) : string =
        let formatLine lineIndex line =
            let prefix = if lineIndex = 0 then adj else String.replicate adj.Length " "
            sprintf "%s%s" prefix line
            
        text.Split([|Environment.NewLine|], StringSplitOptions.None)
        |> Array.mapi formatLine
        |> String.concat Environment.NewLine
        |> sprintf "%s%s" <| Environment.NewLine

    let CompareEquality (expected: IAggregateEvents array) (result: IAggregateEvents array) : ThenResult array =
        let largerEventSize = Math.Max(expected.Length, result.Length)
        
        let results = List<ThenResult>()
        for i in 0 .. largerEventSize - 1 do
            let ex = if i < expected.Length then Some expected.[i] else None
            let ac = if i < result.Length then Some result.[i] else None
            
            let expectedString = 
                match ex with 
                | Some e -> e.GetType().ToString()
                | None -> "[EVENT MISSING] Expected"
                
            let resultString = 
                match ac with
                | Some a -> a.GetType().ToString()
                | None -> "[EVENT MISSING] Result"
                
            let result = { Expectation = expectedString; Failure = None }
            
            let difference = ObjectComparer.DeepCompare(ex |> Option.toObj) (ac |> Option.toObj)
            if difference <> String.Empty then
                let failure =
                    if expectedString <> resultString then
                        FormatHumanReadable "Received: " resultString
                    else
                        FormatHumanReadable String.Empty difference
                results.Add { result with Failure = Some failure }
            else
                results.Add result
        
        results.ToArray()