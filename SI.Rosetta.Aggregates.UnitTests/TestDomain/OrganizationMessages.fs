namespace SI.Rosetta.Aggregates.UnitTests

open SI.Rosetta.Common
open System.Collections.Generic

type RegisterOrganization = {
    Id: string
    Name: string
}

type ChangeOrganizationName = {
    Id: string
    Name: string
}

type OrganizationRegistered = {
    Id: string
    Name: string
}

type OrganizationNameChanged = {
    Id: string
    Name: string
}

[<RequireQualifiedAccess>]
type OrganizationCommands =
    | Register of RegisterOrganization
    | ChangeName of ChangeOrganizationName
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type OrganizationEvents =
    | Registered of OrganizationRegistered
    | NameChanged of OrganizationNameChanged
    interface IAggregateEvents
