namespace TestFSharp

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open SI.Rosetta.Aggregates

/// Request bodies of the Organization commands - the Metadata is set on the server
[<RequireQualifiedAccess>]
module OrganizationRequests =
    [<CLIMutable>]
    type RegisterOrganization = { Id: string; Name: string }

module OrganizationEndpoints =
    // State-based: the state is the Organization's RavenDB document, its Version is in the document's @metadata
    let LoadOrganization (repo: IStateBasedAggregateRepository) (id: string) () =
        task {
            let! organization = repo.GetAsync<OrganizationAggregate, Organization, OrganizationEvents>(id).ConfigureAwait(false)
            return organization |> Option.map (fun organization -> organization.State :> IAggregateState)
        }

    let Map (app: WebApplication) =
        app.MapPost("/organizations/register", Func<OrganizationRequests.RegisterOrganization, IOrganizationAggregateHandler, IStateBasedAggregateRepository, Task<IResult>>(fun request handler repo ->
            OrganizationCommands.Register { Id = request.Id; Name = request.Name; Metadata = CommandEndpoint.IssuedByAdmin() }
            |> CommandEndpoint.Execute handler (LoadOrganization repo request.Id))) |> ignore
