namespace TestFSharp

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open SI.Rosetta.Aggregates

/// Request bodies of the Person commands - the Metadata is set on the server
[<RequireQualifiedAccess>]
module PersonRequests =
    [<CLIMutable>]
    type RegisterPerson = { Id: string; Name: string }

    [<CLIMutable>]
    type ChangePersonName = { Id: string; Name: string }

    [<CLIMutable>]
    type SetPersonHeight = { Id: string; Height: int }

module PersonEndpoints =
    // Event-Sourced: the state is rebuilt from the Person's KurrentDB stream
    let LoadPerson (repo: IEventSourcedAggregateRepository) (id: string) () =
        task {
            let! person = repo.GetAsync<PersonAggregateState>(id).ConfigureAwait(false)
            return person |> Option.map (fun person -> person :> IAggregateState)
        }

    let Map (app: WebApplication) =
        app.MapPost("/people/register", Func<PersonRequests.RegisterPerson, IPersonAggregateHandler, IEventSourcedAggregateRepository, Task<IResult>>(fun request handler repo ->
            PersonCommands.Register { Id = request.Id; Name = request.Name; Metadata = CommandEndpoint.IssuedByAdmin() }
            |> CommandEndpoint.Execute handler (LoadPerson repo request.Id))) |> ignore

        app.MapPost("/people/change-name", Func<PersonRequests.ChangePersonName, IPersonAggregateHandler, IEventSourcedAggregateRepository, Task<IResult>>(fun request handler repo ->
            PersonCommands.ChangeName { Id = request.Id; Name = request.Name; Metadata = CommandEndpoint.IssuedByAdmin() }
            |> CommandEndpoint.Execute handler (LoadPerson repo request.Id))) |> ignore

        app.MapPost("/people/set-height", Func<PersonRequests.SetPersonHeight, IPersonAggregateHandler, IEventSourcedAggregateRepository, Task<IResult>>(fun request handler repo ->
            PersonCommands.SetHeight { Id = request.Id; Height = request.Height; Metadata = CommandEndpoint.IssuedByAdmin() }
            |> CommandEndpoint.Execute handler (LoadPerson repo request.Id))) |> ignore
