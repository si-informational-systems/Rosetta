namespace SI.Rosetta.IntegrationTests

open SI.Rosetta.Aggregates
open SI.Rosetta.Common
open SI.Rosetta.Projections

type RegisterParcel = { Id: string; Recipient: string }
type ReaddressParcel = { Id: string; Recipient: string }

type ParcelRegistered = { Id: string; Recipient: string }
type ParcelReaddressed = { Id: string; Recipient: string }

[<RequireQualifiedAccess>]
type ParcelCommands =
    | Register of RegisterParcel
    | Readdress of ReaddressParcel
    interface IAggregateCommands

[<RequireQualifiedAccess>]
type ParcelEvents =
    | Registered of ParcelRegistered
    | Readdressed of ParcelReaddressed
    interface IAggregateEvents

type ParcelAggregateState() =
    inherit AggregateState<ParcelEvents>()
    member val Recipient = "" with get, set

    override this.ApplyEvent(ev) =
        match ev with
        | ParcelEvents.Registered e ->
            this.Id <- e.Id
            this.Recipient <- e.Recipient
        | ParcelEvents.Readdressed e ->
            this.Recipient <- e.Recipient

type ParcelAggregate() =
    inherit Aggregate<ParcelAggregateState, ParcelCommands, ParcelEvents>()

    override this.Execute(command) =
        match command with
        | ParcelCommands.Register cmd when not this.ShouldHandleIdempotency ->
            this.Apply(ParcelEvents.Registered { Id = cmd.Id; Recipient = cmd.Recipient })
        | ParcelCommands.Register _ -> ()
        | ParcelCommands.Readdress cmd when this.State.Recipient <> cmd.Recipient ->
            this.Apply(ParcelEvents.Readdressed { Id = cmd.Id; Recipient = cmd.Recipient })
        | ParcelCommands.Readdress _ -> ()

type IParcelAggregateHandler =
    inherit IAggregateHandler

type ParcelAggregateHandler(repo: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<ParcelAggregate, ParcelAggregateState, ParcelCommands, ParcelEvents>()
    do base.AggregateRepository <- repo
    interface IParcelAggregateHandler

    override this.ExecuteAsync(command) =
        task {
            match command with
            | ParcelCommands.Register cmd -> do! this.IdempotentlyCreateAggregate cmd.Id command
            | ParcelCommands.Readdress cmd -> do! this.IdempotentlyUpdateAggregate cmd.Id command
        }

/// ParcelRegistered as an older version of the domain wrote it: ParcelCreated, with Addressee for Recipient
type ParcelCreatedUpcaster() =
    interface IEventUpcaster with
        member _.Upcast(ev) =
            match ev.TypeName with
            | "ParcelCreated" -> { ev.RenameField("Addressee", "Recipient") with TypeName = "ParcelRegistered" }
            | _ -> ev

[<CLIMutable>]
type Parcel = { Id: string; Recipient: string }

module ParcelStreams =
    [<Literal>]
    let Parcels = "$ce-Parcels"
    [<Literal>]
    let SkippingParcels = "SkippingParcels-1"
    [<Literal>]
    let StrictParcels = "StrictParcels-1"

type ParcelProjectionHandler(store: INoSqlStore) =
    let Store = store

    interface IProjectionHandler<ParcelEvents> with
        member this.Handle(event, _checkpoint) =
            task {
                match event with
                | ParcelEvents.Registered e ->
                    do! Store.StoreAsync({ Id = e.Id; Recipient = e.Recipient } : Parcel).ConfigureAwait(false)
                | ParcelEvents.Readdressed e ->
                    do! Store.StoreAsync({ Id = e.Id; Recipient = e.Recipient } : Parcel).ConfigureAwait(false)
            }

/// Run by the hosted service in HostingTests
[<HandlesStream(ParcelStreams.Parcels)>]
type ParcelProjection() =
    inherit Projection<ParcelEvents>()
    interface IAmHandledBy<ParcelProjectionHandler, ParcelEvents>

/// Started directly by KurrentDBSubscriptionTests, so the hosted service leaves it alone
[<DisabledProjection; SkipUnknownEvents; HandlesStream(ParcelStreams.SkippingParcels)>]
type SkippingParcelProjection() =
    inherit Projection<ParcelEvents>()
    interface IAmHandledBy<ParcelProjectionHandler, ParcelEvents>

/// Started directly by KurrentDBSubscriptionTests, so the hosted service leaves it alone
[<DisabledProjection; HandlesStream(ParcelStreams.StrictParcels)>]
type StrictParcelProjection() =
    inherit Projection<ParcelEvents>()
    interface IAmHandledBy<ParcelProjectionHandler, ParcelEvents>
