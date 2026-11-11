namespace SI.Rosetta.Projections.HostBuilder

open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open SI.Rosetta.Projections.KurrentDB

/// Every projection's status: Healthy when they all run, Degraded while one starts or retries after a failure,
/// Unhealthy (the registration's failure status) when one is faulted (failing again and again without progress) or
/// stopped. Each projection's name, state, stream, checkpoint, failures in a row, last error and since when is in Data
type ProjectionsHealthCheck(statuses: ProjectionStatuses) =

    /// The health a projection in the state gives
    static member HealthOf(state: ProjectionState, failureStatus: HealthStatus) =
        match state with
        | ProjectionState.Running -> HealthStatus.Healthy
        | ProjectionState.Starting
        | ProjectionState.Retrying -> HealthStatus.Degraded
        | ProjectionState.Faulted
        | ProjectionState.Stopped -> failureStatus

    member this.Check(failureStatus: HealthStatus) =
        let all = statuses.All
        let data = Dictionary<string, obj>()
        for status in all do
            data.[status.Name] <-
                dict [
                    "state", box (string status.State)
                    "stream", box status.Stream
                    "checkpoint", box status.Checkpoint
                    "consecutiveFailures", box status.ConsecutiveFailures
                    "lastError", box status.LastError
                    "since", box status.Since ]
                |> box
        let health =
            if all.Count = 0 then HealthStatus.Healthy
            else all |> Seq.map (fun status -> ProjectionsHealthCheck.HealthOf(status.State, failureStatus)) |> Seq.min
        let notRunning =
            all
            |> Seq.filter (fun status -> status.State <> ProjectionState.Running)
            |> Seq.map (fun status -> sprintf "%s %s" status.Name ((string status.State).ToLowerInvariant()))
            |> Seq.toList
        let description =
            match all.Count, notRunning with
            | 0, _ -> "No projections have started yet."
            | count, [] -> sprintf "%d projections running." count
            | count, others -> sprintf "%d projections: %s." count (String.concat ", " others)
        HealthCheckResult(health, description, null, data)

    interface IHealthCheck with
        member this.CheckHealthAsync(context, _cancellationToken) =
            let failureStatus = if isNull context || isNull context.Registration then HealthStatus.Unhealthy else context.Registration.FailureStatus
            Task.FromResult(this.Check failureStatus)

/// services.AddHealthChecks().AddRosettaProjections()
[<Extension; AbstractClass; Sealed>]
type ProjectionsHealthChecksExtensions =
    /// The "rosetta-projections" health check: every projection's status (see ProjectionsHealthCheck). Needs projections
    /// registered with UseProjectionsWith/AddProjectionsWith
    [<Extension>]
    static member AddRosettaProjections(
        builder: IHealthChecksBuilder,
        [<Optional; DefaultParameterValue("rosetta-projections")>] name: string,
        [<Optional; DefaultParameterValue(null: IEnumerable<string>)>] tags: IEnumerable<string>) =
        builder.Add(
            HealthCheckRegistration(
                name,
                (fun services -> ProjectionsHealthCheck(services.GetRequiredService<ProjectionStatuses>()) :> IHealthCheck),
                System.Nullable<HealthStatus>(),
                tags))
