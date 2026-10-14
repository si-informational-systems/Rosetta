namespace TestFSharp

open System
open System.Net.Http
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Raven.Client.Documents

/// A RavenDB collection's documents exactly as RavenDB returns them, @metadata included.
/// RavenDB refuses cross-origin requests, so the test page reads them through here instead of from RavenDB itself
type RavenDocuments(store: IDocumentStore) =
    let Http =
        let handler = new HttpClientHandler()
        if not (isNull store.Certificate) then handler.ClientCertificates.Add store.Certificate |> ignore
        new HttpClient(handler)

    member this.CollectionAsync(collection: string) =
        task {
            let url =
                sprintf "%s/databases/%s/collections/docs?name=%s"
                    (store.Urls.[0].TrimEnd '/') (Uri.EscapeDataString store.Database) (Uri.EscapeDataString collection)
            let! documents = Http.GetStringAsync(url).ConfigureAwait(false)
            return Results.Text(documents, "application/json")
        }

module QueryEndpoints =
    let Map (app: WebApplication) =
        // Read Model of Test.Projections (PersonProjection): Person is Event-Sourced, so this is how its state reaches RavenDB
        app.MapGet("/people", Func<RavenDocuments, Task<IResult>>(fun documents -> documents.CollectionAsync "People")) |> ignore
        // The state-based Aggregate's own documents
        app.MapGet("/organizations", Func<RavenDocuments, Task<IResult>>(fun documents -> documents.CollectionAsync "Organizations")) |> ignore
        // Read Model of the cp-TotalPeopleAndOrganizations Custom Projection
        app.MapGet("/totals", Func<RavenDocuments, Task<IResult>>(fun documents -> documents.CollectionAsync "TotalPeoples")) |> ignore
