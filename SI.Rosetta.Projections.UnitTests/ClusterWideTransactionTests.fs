namespace SI.Rosetta.Projections.UnitTests

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open SI.Rosetta.Projections
open SI.Rosetta.Projections.TestKit

[<ClusterWideTransaction; HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type EveryEventClusterWideProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

[<ClusterWideTransaction(typeof<OrganizationRegistered>); HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type RegisteredClusterWideProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

[<ClusterWideTransaction(typeof<OrganizationRegistered>, typeof<OrganizationNameChanged>); HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type BothEventsClusterWideProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

/// Names a record that isn't one of its events
[<ClusterWideTransaction(typeof<OrganizationRegistered>, typeof<Tally>); HandlesStream(ProjectionStreamNames.OrganizationStream); DisabledProjection>]
type ForeignEventClusterWideProjection() =
    inherit Projection<OrganizationEvents>()
    interface IAmHandledBy<OrganizationProjectionHandler, OrganizationEvents>

/// [<ClusterWideTransaction>] for every event, or for the events it names
type ClusterWideTransactionTests() =
    let Registered = OrganizationEvents.Registered { Id = "Organizations-1"; Name = "Rosetta"; DateRegistered = DateTime.MinValue }
    let NameChanged = OrganizationEvents.NameChanged { Id = "Organizations-1"; Name = "Rosetta 2" }

    [<Fact>]
    member this.``Without events, every event's transaction is cluster-wide``() =
        Assert.True(ClusterWideTransaction.IsOn(typeof<EveryEventClusterWideProjection>, typeof<OrganizationRegistered>))
        Assert.True(ClusterWideTransaction.IsOn(typeof<EveryEventClusterWideProjection>, typeof<OrganizationNameChanged>))

    [<Fact>]
    member this.``With events, only theirs are cluster-wide``() =
        Assert.True(ClusterWideTransaction.IsOn(typeof<RegisteredClusterWideProjection>, typeof<OrganizationRegistered>))
        Assert.False(ClusterWideTransaction.IsOn(typeof<RegisteredClusterWideProjection>, typeof<OrganizationNameChanged>))
        Assert.True(ClusterWideTransaction.IsOn(typeof<BothEventsClusterWideProjection>, typeof<OrganizationNameChanged>))
        // Still a cluster-wide projection: MongoDB and PostgreSQL refuse it
        Assert.True(ClusterWideTransaction.IsOn typeof<RegisteredClusterWideProjection>)
        Assert.Throws<InvalidOperationException>(fun () -> ClusterWideTransaction.Refuse("MongoDB", typeof<RegisteredClusterWideProjection>)) |> ignore

    [<Fact>]
    member this.``A projection without the attribute, or created without a type, is never cluster-wide``() =
        Assert.False(ClusterWideTransaction.IsOn(typeof<OrganizationProjection>, typeof<OrganizationRegistered>))
        Assert.False(ClusterWideTransaction.IsOn(null, typeof<OrganizationRegistered>))

    [<Fact>]
    member this.``Each event's transaction is begun with its record type``() =
        task {
            let store = TestKitProjectionStore()
            let begun = List<Type * Type>()
            let projection =
                Projection<OrganizationEvents>(
                    Name = "Organization",
                    SubscriptionStreamName = ProjectionStreamNames.OrganizationStream,
                    Checkpoint = Checkpoint(Id = "Checkpoints-Organization"),
                    CheckpointStore = StubCheckpointStore(),
                    ProjectionType = typeof<RegisteredClusterWideProjection>,
                    UnitOfWork =
                        { new IProjectionsUnitOfWork with
                            member _.PrepareAsync(_, _) = Task.CompletedTask
                            member _.ClearAsync(_) = Task.CompletedTask
                            member _.BeginAsync(projectionType, eventType) =
                                begun.Add((projectionType, eventType))
                                (store :> IProjectionsUnitOfWork).BeginAsync(projectionType, eventType) },
                    Handlers = Seq.empty)

            do! projection.ProjectAsync(Registered, 1UL)
            do! projection.ProjectAsync(NameChanged, 2UL)

            Assert.Equal<Type * Type>(
                [ typeof<RegisteredClusterWideProjection>, typeof<OrganizationRegistered>
                  typeof<RegisteredClusterWideProjection>, typeof<OrganizationNameChanged> ],
                begun)
        }

    [<Fact>]
    member this.``Naming the records of the projection's events, or none, passes the check``() =
        ClusterWideTransaction.Check(typeof<EveryEventClusterWideProjection>, typeof<OrganizationEvents>)
        ClusterWideTransaction.Check(typeof<RegisteredClusterWideProjection>, typeof<OrganizationEvents>)
        ClusterWideTransaction.Check(typeof<BothEventsClusterWideProjection>, typeof<OrganizationEvents>)
        ClusterWideTransaction.Check(typeof<OrganizationProjection>, typeof<OrganizationEvents>)

    [<Fact>]
    member this.``A type that isn't the record of one of its events fails the projection's start``() =
        task {
            let services = ServiceCollection()
            services
                .AddTransient<OrganizationProjectionHandler>()
                .AddSingleton<INoSqlStore>(TestKitProjectionStore())
                .AddTransient<ICheckpointStore, StubCheckpointStore>()
                .AddTransient<IProjectionHandlerFactory, ProjectionHandlerFactory>()
                .AddTransient<ISubscriptionFactory, TestKitSubscriptionFactory>()
                .AddTransient<IProjectionsFactory, ProjectionsFactory>() |> ignore
            let factory = services.BuildServiceProvider().GetRequiredService<IProjectionsFactory>()

            let! error =
                Assert.ThrowsAsync<InvalidOperationException>(fun () ->
                    factory.CreateAsync(typeof<ForeignEventClusterWideProjection>) :> Task)

            Assert.Contains("ForeignEventClusterWideProjection", error.Message)
            Assert.Contains("for Tally, which is no event of OrganizationEvents", error.Message)
        }
