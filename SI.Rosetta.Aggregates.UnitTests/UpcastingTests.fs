namespace SI.Rosetta.Aggregates.UnitTests

open Microsoft.Extensions.DependencyInjection
open Xunit
open Xunit.Sdk
open SI.Rosetta.Common
open SI.Rosetta.TestKit

/// OrganizationNameChanged as an older version of the domain wrote it: OrganizationRenamed, with NewName for Name
type OrganizationRenamed = { Id: string; NewName: string }

/// The events as that older version had them, to write its events in Given
[<RequireQualifiedAccess>]
type OldOrganizationEvents =
    | Renamed of OrganizationRenamed
    interface IAggregateEvents

type OrganizationRenamedUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "OrganizationRenamed" ->
                ev.Data.["Name"] <- ev.Data.["NewName"]
                ev.Data.Remove("NewName") |> ignore
                { TypeName = "OrganizationNameChanged"; Data = ev.Data }
            | _ -> ev

type OrganizationUpcastingTests() =
    inherit UpcastingTestKitBase<OrganizationEvents>()

    override this.ConfigureServices(services) =
        services.AddSingleton<IEventUpcaster, OrganizationRenamedUpcaster>() |> ignore

    [<Fact>]
    member this.``An older version's event is read as the current one, and current events stay as they are``() =
        this.Given(
            OrganizationEvents.Registered { Id = "Organizations-1"; Name = "Rosetta" },
            OldOrganizationEvents.Renamed { Id = "Organizations-1"; NewName = "Rosetta 2" })

        this.Then(
            OrganizationEvents.Registered { Id = "Organizations-1"; Name = "Rosetta" },
            OrganizationEvents.NameChanged { Id = "Organizations-1"; Name = "Rosetta 2" })

    [<Fact>]
    member this.``Then fails when the events are read as other events``() =
        this.Given(OldOrganizationEvents.Renamed { Id = "Organizations-1"; NewName = "Rosetta 2" })

        let ex = Assert.Throws<XunitException>(fun () ->
            this.Then(OrganizationEvents.NameChanged { Id = "Organizations-1"; Name = "Rosetta 3" }))

        Assert.Contains("Name: Values differ", ex.Message)

type WithoutUpcastersTests() =
    inherit UpcastingTestKitBase<OrganizationEvents>()

    override this.ConfigureServices(_services) = ()

    [<Fact>]
    member this.``An older version's event without its upcaster has no case``() =
        this.Given(OldOrganizationEvents.Renamed { Id = "Organizations-1"; NewName = "Rosetta 2" })

        let ex = Assert.Throws<UnknownEventException>(fun () ->
            this.Then(OrganizationEvents.NameChanged { Id = "Organizations-1"; Name = "Rosetta 2" }))

        Assert.Equal("OrganizationRenamed", ex.TypeName)
