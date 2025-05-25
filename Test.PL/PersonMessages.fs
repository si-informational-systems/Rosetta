namespace TestFSharp

open SI.Rosetta.Common

type RegisterPerson = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface ICommand with member this.Id = this.Id member this.Metadata = this.Metadata

type ChangePersonName = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface ICommand with member this.Id = this.Id member this.Metadata = this.Metadata

type SetPersonHeight = {
    Id: string
    Height: int
    Metadata: MessageMetadata
} with interface ICommand with member this.Id = this.Id member this.Metadata = this.Metadata


type PersonRegistered = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface IEvent with member this.Id = this.Id member this.Metadata = this.Metadata

type PersonNameChanged = {
    Id: string
    Name: string
    Metadata: MessageMetadata
} with interface IEvent with member this.Id = this.Id member this.Metadata = this.Metadata

type PersonHeightSet = {
    Id: string
    Height: int
    Metadata: MessageMetadata
} with interface IEvent with member this.Id = this.Id member this.Metadata = this.Metadata 

[<RequireQualifiedAccess>]
type PersonCommands =
    | Register of RegisterPerson
    | ChangeName of ChangePersonName
    | SetHeight of SetPersonHeight
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type PersonEvents =
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    | HeightSet of PersonHeightSet
    interface IAggregateEvents
