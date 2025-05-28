# 🚀 Rosetta Framework

**Domain-Driven Design, Event Sourcing, and Projections for .NET & F# — with RavenDB, EventStoreDB, and modern HostBuilder integration.**

![.NET](https://img.shields.io/badge/.NET-9-blueviolet)
![F#](https://img.shields.io/badge/F%23-functional-blue)
![Event Sourcing](https://img.shields.io/badge/Event%20Sourcing-Enabled-brightgreen)
![RavenDB](https://img.shields.io/badge/RavenDB-Integrated-red)
![MongoDB](https://img.shields.io/badge/MongoDB-Integrated-red)
![EventStoreDB](https://img.shields.io/badge/EventStoreDB-Integrated-orange)
![CI](https://img.shields.io/badge/CI-GitHub%20Actions-blue)

---

## ✨ Overview

**Rosetta** is a powerful, opinionated framework for building robust, Event-Sourced, Domain-Driven Microservices in .NET

Levarage the power of **F# Discriminated Unions** inside Rosetta to create error-proof Domain Solutions!

It provides:

- 🧩 **Aggregate Roots** and **Domain Events** (DDD)
- ⚡ **Event Sourcing** with EventStoreDB
- 📊 **Projections** with RavenDB and MongoDB (NoSQL) and extensible stores
- 📊 **Custom Projection Definitions** with RavenDB and MongoDB (NoSQL) and extensible stores
- 🏗️ **HostBuilder Extensions** for seamless DI and service registration
- 🛠️ **CQRS**-friendly patterns and handler abstractions
- 🧪 **Testability** and clean separation of concerns

---

## 📝 Note:

 - While **Rosetta** is opinionated in its choice of technologies, it is built to be easily extensible.
 - You can integrate any Event Sourcing Store and SQL/NoSQL database implementation with minimal effort, adapting the framework to your unique requirements and infrastructure.
 - Any additional implementations are very welcome and would contribute to the Framework's out-of-the-box usability!

## 📦 Features

- **Aggregate & Event Abstractions**: Write expressive, testable domain logic.
- **EventStoreDB Integration**: Reliable, scalable event storage.
- **RavenDB/MongoDB Projections**: Fast, flexible read models.
- **Plug-and-Play HostBuilder Extensions**: Register everything with a single line.
- **CQRS Handlers**: Command and event handler patterns out-of-the-box.
- **Idempotency & Consistency**: Built-in support for safe, repeatable operations.
- **Modern F# & C# Support**: Idiomatic, clean code for both languages.

---

## 🏗️ Architecture

```mermaid
graph TD
    A[Client/API] -->|Command| B[Aggregate Handler]
    B -->|Event| C[EventStoreDB]
    C -->|Event| D[Projection Handler]
    D -->|Read Model| E[RavenDB]
    E -->|Query| F[Client/API]
```

---

## 🚦 Quick Start

### 1. Install NuGet Packages

```shell
dotnet add package SI.Rosetta.Aggregates
dotnet add package SI.Rosetta.Projections
dotnet add package EventStore.Client
dotnet add package RavenDB.Client
```

### 2. Register Aggregates & Projections

```fsharp
open SI.Rosetta.Aggregates.HostBuilder
open SI.Rosetta.Projections.HostBuilder

Host
  .CreateDefaultBuilder()
  .UseAggregatesWith<SI.Rosetta.HostBuilder.EventStore>(typeof<_Assembly_Containing_Aggregates_>.Assembly)
  .UseProjectionsWith<SI.Rosetta.HostBuilder.EventStore, SI.Rosetta.HostBuilder.RavenDB>(typeof<_Assembly_Containing_Projections_>.Assembly)
  .Build()
  .Run()
```

---

## 🧑‍💻 Example: Person Aggregate

### Domain Messages
```fsharp
type RegisterPerson = {
    Id: string
    Name: string
}

type ChangePersonName = {
    Id: string
    Name: string
}

type PersonRegistered = {
    Id: string
    Name: string
}

type PersonNameChanged = {
    Id: string
    Name: string
}

type PersonCommands =
    | Register of RegisterPerson
    | ChangeName of ChangePersonName
    interface IAggregateCommands // [REQUIRED]

type PersonEvents =
    | Registered of PersonRegistered
    | NameChanged of PersonNameChanged
    interface IAggregateEvents // [REQUIRED]
```

### Domain Model

```fsharp
type PersonAggregate() =
    inherit Aggregate<PersonAggregateState, PersonCommands, PersonEvents>() // <IAggregateState, IAggregateCommands, IAggregateEvents>
    override this.Execute(command: PersonCommands) =
        match command with
            | PersonCommands.Register cmd -> this.Register cmd
            | PersonCommands.ChangeName cmd -> this.ChangeName cmd

    member private this.Register(cmd: RegisterPerson) =
        let event = PersonEvents.Registered { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
        this.PublishedEvents.Add event //OPTIONAL
        this.Apply event

    member private this.ChangeName(cmd: ChangePersonName) =
        let event = PersonEvents.NameChanged { Id = cmd.Id; Name = cmd.Name; Metadata = cmd.Metadata }
        this.PublishedEvents.Add event //OPTIONAL
        this.Apply event
```

### State

```fsharp
type PersonAggregateState() =
    inherit AggregateState<PersonEvents>() //<IAggregateEvents>
    override this.ApplyEvent(ev) =
        match ev with
        | Registered e -> 
            this.Id <- e.Id
            this.Name <- e.Name
        | NameChanged e -> 
            this.Name <- e.Name
```

### Handler

```fsharp
type PersonAggregateHandler(repo: IAggregateRepository) =
    inherit AggregateHandler<PersonAggregate, PersonCommands, PersonEvents>() // <IAggregateState, IAggregateCommands, IAggregateEvents>
    override this.ExecuteAsync(command) =
        task {
            match command with
            | Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
            | ChangeName cmd | SetHeight cmd -> do! this.IdempotentlyUpdateAggregate cmd.Id command
        }
```

---

## 📊 Example: Person Projection

```fsharp
[<HandlesStream(ProjectionStreamNames.PersonStream)>]
type PersonProjection() =
    inherit Projection<PersonEvents>() // <IAggregateEvents>
    interface IAmHandledBy<PersonProjectionHandler, PersonEvents> // <IProjectionHandler, IAggregateEvents>

type PersonProjectionHandler(store: INoSqlStore) =
    interface IProjectionHandler<PersonEvents> with
        member this.Handle(event, checkpoint) =
            task {
                match event with
                | Registered e -> let person = { ... }; do! store.StoreAsync person
                | NameChanged e -> do! this.Project(e.Id, fun p -> { p with Name = e.Name })
            }
```

---

## 📊 Example: Custom Projections

```fsharp
[<CustomProjectionStream(ProjectionStreamNames.TotalPeopleStream)>]
type TotalPeopleCustomProjectionEvents = 
    | Registered of PersonRegistered
    interface ICustomProjectionEvents

[<HandlesStream(ProjectionStreamNames.TotalPeopleStream)>]
type TotalPeopleCustomProjection() =
    inherit Projection<TotalPeopleCustomProjectionEvents>()
    interface IAmHandledBy<TotalPeopleCustomProjectionHandler, TotalPeopleCustomProjectionEvents>

type TotalPeopleCustomProjectionHandler(store: INoSqlStore) =
    let Store = store
    
    interface IProjectionHandler<TotalPeopleCustomProjectionEvents> with
        member this.Handle(event: TotalPeopleCustomProjectionEvents, checkpoint: uint64) =
            task {
                match event with
                | TotalPeopleCustomProjectionEvents.Registered _ ->
                    do! this.Project(fun custom ->
                        { custom with
                            TotalPersons = custom.TotalPersons + 1 }).ConfigureAwait(false)
```

---


## 🏆 Why Rosetta?

- **DDD-first**: Designed for rich, expressive Domain Models.
- **Event Sourcing**: Every state change is an Event, not just a row update.
- **Projections**: Build fast, query-optimized Read Models.
- **Extensible**: Add your own Aggregates, (Custom) Projections, and Stores.
- **Modern .NET**: Async/await, DI, HostBuilder, and more.

---

## 🛡️ Best Practices

- Use **CQRS**: Separate command and query responsibilities.
- Keep **Domain logic** in Aggregates, not Handlers or Controllers.
- Register all services via HostBuilder extensions.
- Write minimal-code **Unit Tests** for Aggregates and Projections using **Aggregate Test Kit** and **Projections Test Kit**

---

## 📚 Documentation

- [Event Sourcing Concepts](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Domain-Driven Design](https://dddcommunity.org/)
- [RavenDB Docs](https://ravendb.net/docs/)
- [MongoDB Docs](https://www.mongodb.com/docs/)
- [EventStoreDB Docs](https://developers.eventstore.com/)

---

## 🤝 Contributing

PRs, issues, and suggestions are welcome!  
Please follow the DDD and .NET conventions outlined in this repo.

---

## 📝 License

MIT License

---

## 💬 Questions?

Open an issue or start a discussion!

---

**Happy coding!** 🚀

---

Let me know if you want to tailor this further for your organization, add more advanced usage, or include badges for build/test status!
