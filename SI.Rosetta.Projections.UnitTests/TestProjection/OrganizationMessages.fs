namespace SI.Rosetta.Projections.UnitTests

open SI.Rosetta.Common
open System

type OrganizationRegistered = {
    Id: string
    Name: string
    DateRegistered: DateTime
}

type OrganizationNameChanged = {
    Id: string
    Name: string
}

[<RequireQualifiedAccess>]
type OrganizationEvents =
    | Registered of OrganizationRegistered
    | NameChanged of OrganizationNameChanged
    interface IAggregateEvents
