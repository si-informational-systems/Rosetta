namespace TestFSharp

open System
open SI.Rosetta.Common

type RegisterOrganization = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface ICommand with member this.Id = this.Id member this.Metadata = this.Metadata

type OrganizationRegistered = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface IEvent with member this.Id = this.Id member this.Metadata = this.Metadata

[<RequireQualifiedAccess>]
type OrganizationCommands =
    | Register of RegisterOrganization
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type OrganizationEvents =
    | Registered of OrganizationRegistered
    interface IAggregateEvents