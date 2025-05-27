namespace SI.Rosetta.Aggregates.TestKit

open System
open System.Threading.Tasks
open Xunit.Sdk
open SI.Rosetta.Common
open SI.Rosetta.Aggregates
open System.Collections.Generic

[<AbstractClass>]
type TestKitBase<'TAggregateHandler when 'TAggregateHandler :> IAggregateHandler>() =
    let mutable AggregateId = String.Empty
    let mutable TestValid = false
    let GivenEvents = ResizeArray<IAggregateEvents>()
    let mutable WhenCommand = Unchecked.defaultof<IAggregateCommands>

    member private this.FormatTestResults(results: ThenResult seq) : string =
        results
        |> Seq.toArray
        |> fun results ->
            match results |> Array.exists (fun r -> r.Failure.IsSome) with
            | false -> "[TEST PASSED]"
            | true ->
                results
                |> Array.mapi (fun i result ->
                    let formatNumber = sprintf "  %d. " (i + 1)
                    let expectationLine = OutputFormatting.FormatHumanReadable formatNumber result.Expectation
                    let statusLine = 
                        result.Failure
                        |> Option.defaultValue "PASS"
                        |> OutputFormatting.FormatHumanReadable "     "
                    [| expectationLine; statusLine |])
                |> Array.collect id
                |> String.concat Environment.NewLine
                |> sprintf "%s%s" <| Environment.NewLine

    abstract ExecuteCommand : aggregateId: string * events: IAggregateEvents array * command: IAggregateCommands -> Task<HandlerExecutedCommandResult>
    default _.ExecuteCommand(aggregateId, events, command) =
        task {
            if String.IsNullOrEmpty aggregateId then
                raise (XunitException("[TEST INVALID] AggregateId is not set through Init!"))

            let repository = TestKitAggregateRepository()
            repository.SeedEvents(aggregateId, events)
            
            let handlerType = typeof<'TAggregateHandler>
            let handler = Activator.CreateInstance(handlerType, repository :> IAggregateRepository) :?> 'TAggregateHandler

            do! handler.ExecuteAsync(command).ConfigureAwait(false)
            let producedEvents = repository.GetProducedEvents
            let publishedEvents = handler.GetPublishedEvents()

            let result : HandlerExecutedCommandResult = {
                ProducedEvents = producedEvents
                PublishedEvents = publishedEvents
            }
            return result
        }

    member this.Init(aggregateId: string) =
        AggregateId <- aggregateId
    
    member this.Given([<ParamArray>] events: IAggregateEvents array) =
        GivenEvents.AddRange events
        
    member this.When(command: IAggregateCommands) =
        WhenCommand <- command
    
    member this.Then(expectedProducedEvents: ResizeArray<IAggregateEvents>, expectedPublishedEvents: ResizeArray<IAggregateEvents>) = 
        task {
            TestValid <- true
            
            let expectedEventsArray = expectedProducedEvents.ToArray()
            let expectedPublishedEventsArray = expectedPublishedEvents.ToArray()
            let givenEventsArray = GivenEvents.ToArray()
            let! res = this.ExecuteCommand(AggregateId, givenEventsArray, WhenCommand).ConfigureAwait(false)
            let resultProducedEvents = res.ProducedEvents |> Seq.toArray
            let resultPublishedEvents = res.PublishedEvents |> Seq.toArray
        
            let producedEventsResults = 
                OutputFormatting.CompareEquality expectedEventsArray resultProducedEvents
            let producedResults = this.FormatTestResults(producedEventsResults)
            Console.Write(producedResults :> obj)
        
            let publishedEventsResults = 
                OutputFormatting.CompareEquality expectedPublishedEventsArray resultPublishedEvents
            let publishedResults = this.FormatTestResults(publishedEventsResults)
            Console.Write(publishedResults :> obj)
        
            if producedEventsResults |> Array.exists (fun r -> r.Failure.IsSome) then
                raise (XunitException(sprintf "[TEST FAILED] Produced Events:%s%s" Environment.NewLine producedResults))
            
            if publishedEventsResults |> Array.exists (fun r -> r.Failure.IsSome) then
                raise (XunitException(sprintf "[TEST FAILED] Published Events:%s%s" Environment.NewLine publishedResults))
        }
    
    member this.ThenError(name: string) = 
        task {
            TestValid <- true
            try
                let givenEvents = GivenEvents.ToArray()
                let! _ = this.ExecuteCommand(AggregateId, givenEvents, WhenCommand).ConfigureAwait(false)
                raise (XunitException(sprintf "[TEST FAILED] Test did not fail on error: %s as was expected" name))
            with 
            | :? DomainException as e when e.Name = name -> ()
            | _ -> raise (XunitException(sprintf "[TEST FAILED] Test did not fail on error: %s as was expected" name))
        }
        
    member this.ThenNoEvents() = 
        task {
            let result = this.Then(ResizeArray<IAggregateEvents>(), ResizeArray<IAggregateEvents>())
            return! result.ConfigureAwait(false)
        }
    
    member this.Then([<ParamArray>] expectedProducedEvents: IAggregateEvents array) = 
        task {
            let result = this.Then(ResizeArray expectedProducedEvents, ResizeArray<IAggregateEvents>())
            return! result.ConfigureAwait(false)
        }
    
    interface IDisposable with
        member this.Dispose() =
            if not TestValid then
                raise (XunitException("[TEST INVALID]: Then or ThenError were not called!"))
                
    static member NoProducedEvents = ResizeArray<IAggregateCommands>()
    static member NoPublishedEvents = ResizeArray<IAggregateCommands>()