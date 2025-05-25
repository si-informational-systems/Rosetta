namespace TestFSharp

open System

type UserReference = {
    Id: string
    Name: string
}

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
