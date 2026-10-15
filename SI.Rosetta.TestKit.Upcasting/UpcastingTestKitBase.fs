namespace SI.Rosetta.TestKit

open System
open Microsoft.Extensions.DependencyInjection
open Xunit.Sdk
open SI.Rosetta.Common

/// Tests IEventUpcasters on their own: Given events as an older version of the domain wrote them, Then the 'TEvents
/// they're read as. Each Given event is encoded as it's stored and read back through the upcasters registered in
/// ConfigureServices, as Rosetta reads events from KurrentDB. The Aggregates and Projections TestKits then only need the
/// current 'TEvents, which is all an upcast event becomes
[<AbstractClass>]
type UpcastingTestKitBase<'TEvents when 'TEvents :> IAggregateEvents>() as this =
    let mutable TestValid = false
    let GivenEvents = ResizeArray<IAggregateEvents>()
    let Services = ServiceCollection()
    let Provider = lazy (
        this.ConfigureServices(Services)
        Services.BuildServiceProvider())

    let Read (upcasters: IEventUpcaster array) (eventNumber: int) (ev: IAggregateEvents) : 'TEvents =
        let typeName, json = EventEncoding.Encode ev
        match EventEncoding.Decode<'TEvents> upcasters typeName json with
        | Ok current -> current
        | Error unknown -> raise (UnknownEventException(unknown, typeof<'TEvents>, "Given", uint64 eventNumber))

    /// Register the upcasters under test, as the service does: services.AddSingleton<IEventUpcaster, MyUpcaster>()
    abstract ConfigureServices : services: IServiceCollection -> unit

    /// Events of any events DU, typically one declared in the test with the record types an older version wrote
    member this.Given([<ParamArray>] events: IAggregateEvents array) =
        GivenEvents.AddRange events

    /// The Given events, read through the upcasters, are these, in order.
    /// One that still has no 'TEvents case throws UnknownEventException
    member this.Then([<ParamArray>] expectedEvents: 'TEvents array) =
        TestValid <- true
        let upcasters = Provider.Value.GetServices<IEventUpcaster>() |> Seq.toArray
        let readEvents = GivenEvents |> Seq.mapi (Read upcasters) |> Seq.toArray

        let difference = ObjectComparer.DeepCompare(expectedEvents, readEvents)
        if difference <> String.Empty then
            raise (XunitException(sprintf "[TEST FAILED] The Given events were read as other events:%s%s" Environment.NewLine difference))

    interface IDisposable with
        member this.Dispose() =
            if Provider.IsValueCreated then
                Provider.Value.Dispose()
            if not TestValid then
                raise (XunitException("[TEST INVALID]: Then was not called!"))
