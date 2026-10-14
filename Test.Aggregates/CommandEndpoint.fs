namespace TestFSharp

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.FSharp.Reflection
open SI.Rosetta.Aggregates
open SI.Rosetta.Common

type DomainError = {
    Name: string
    Message: string
}

type PublishedEvent = {
    Type: string
    Data: obj
}

/// What a command did to its Aggregate - Applied, NoChanges (idempotent), Rejected (DomainException) or Conflict (ConcurrencyException)
type CommandResult = {
    Outcome: string
    Error: DomainError option
    VersionBefore: int64
    VersionAfter: int64
    PublishedEvents: PublishedEvent list
    State: obj
}

module CommandEndpoint =
    let IssuedByAdmin () : MessageMetadata = {
        IssuedBy = { Id = "User-1"; Name = "ADMIN" }
        TimeIssued = DateTime.Now
    }

    // F# unions don't serialize to JSON: describe the event by its record, the same way KurrentDB stores it
    let Describe (event: IAggregateEvents) =
        let _, fields = FSharpValue.GetUnionFields(event, event.GetType())
        { Type = fields.[0].GetType().Name; Data = fields.[0] }

    let VersionOf (state: IAggregateState option) =
        state |> Option.map (fun state -> state.Version) |> Option.defaultValue 0L

    let ResultOf outcome error before after events = {
        Outcome = outcome
        Error = error
        VersionBefore = VersionOf before
        VersionAfter = VersionOf after
        PublishedEvents = events
        State = after |> Option.map box |> Option.toObj
    }

    /// Executes the command through its Aggregate Handler, reading the Aggregate's state before and after it
    let Execute (handler: IAggregateHandler) (loadState: unit -> Task<IAggregateState option>) (command: IAggregateCommands) =
        task {
            let! before = loadState().ConfigureAwait(false)
            try
                do! handler.ExecuteAsync(command).ConfigureAwait(false)
                let! after = loadState().ConfigureAwait(false)
                let outcome = if VersionOf after = VersionOf before then "NoChanges" else "Applied"
                let events = handler.GetPublishedEvents() |> Seq.map Describe |> List.ofSeq
                return Results.Ok(ResultOf outcome None before after events)
            with
            | :? DomainException as ex ->
                return Results.UnprocessableEntity(ResultOf "Rejected" (Some { Name = ex.Name; Message = ex.Message }) before before [])
            | :? ConcurrencyException as ex ->
                return Results.Conflict(ResultOf "Conflict" (Some { Name = "Concurrency"; Message = ex.Message }) before before [])
        }
