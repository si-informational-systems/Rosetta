namespace SI.Rosetta.Projections.UnitTests

open Xunit
open SI.Rosetta.Projections.EventStore

type CustomProjectionSyncTests() =
    let Query = "fromStreams('$ce-People').when({PersonRegistered: function(s,e){linkTo('cp-People', e);return s;}})"
    let Stored query emitEnabled : Result<StoredProjectionDefinition, string> = Ok { Query = query; EmitEnabled = emitEnabled }

    [<Fact>]
    member this.``Creates a projection that doesn't exist``() =
        Assert.Equal(Create, CustomProjectionSync.Decide Query None (Error "it doesn't exist yet"))

    [<Fact>]
    member this.``Leaves an unchanged running projection alone``() =
        Assert.Equal(Unchanged, CustomProjectionSync.Decide Query (Some "Running") (Stored Query true))

    [<Fact>]
    member this.``Updates a projection whose definition changed``() =
        let oldQuery = "fromStreams('$ce-People').when({})"
        Assert.Equal(Update "its definition changed", CustomProjectionSync.Decide Query (Some "Running") (Stored oldQuery true))

    [<Fact>]
    member this.``Updates a projection with emit disabled``() =
        Assert.Equal(Update "emit was disabled", CustomProjectionSync.Decide Query (Some "Running") (Stored Query false))

    [<Fact>]
    member this.``Restarts a faulted projection even when unchanged``() =
        Assert.Equal(Update "it was Faulted (Enabled)", CustomProjectionSync.Decide Query (Some "Faulted (Enabled)") (Stored Query true))

    [<Fact>]
    member this.``Updates without comparing when the definition can't be read``() =
        Assert.Equal(UpdateUnverified "GET failed", CustomProjectionSync.Decide Query (Some "Running") (Error "GET failed"))
