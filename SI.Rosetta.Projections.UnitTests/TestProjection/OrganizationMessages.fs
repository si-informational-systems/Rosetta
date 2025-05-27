namespace SI.Rosetta.Projections.UnitTests

open SI.Rosetta.Common

type OrganizationRegistered = {
    Id: string
    Name: string
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
