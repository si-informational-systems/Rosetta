namespace TestFSharp

open System

[<CLIMutable>]
type UserReference = {
    Id: string
    Name: string
}

[<CLIMutable>]
type MessageMetadata = {
    IssuedBy: UserReference
    TimeIssued: DateTime
}

type ICommand = 
    abstract Id: string
    abstract Metadata: MessageMetadata

type IEvent = 
    abstract Id: string
    abstract Metadata: MessageMetadata
