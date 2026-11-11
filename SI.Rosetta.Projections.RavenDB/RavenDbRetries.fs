namespace SI.Rosetta.Projections.RavenDB

open System
open System.Threading.Tasks
open Raven.Client.Exceptions

/// A small retry policy for transient RavenDB errors
module internal RavenDbRetries =
    let MaxRetries = 3
    let Delay = TimeSpan.FromMilliseconds(100.0)

    let IsTransient (ex: Exception) =
        match ex with
        | :? AggregateException as ex when (ex.InnerException :? RavenException) -> true
        | :? RavenException -> true
        | _ -> false

    let rec Retry (retryCount: int) (operation: unit -> Task<'T>) : Task<'T> =
        task {
            try
                return! operation().ConfigureAwait(false)
            with ex ->
                if not (IsTransient ex) || retryCount >= MaxRetries then
                    raise ex
                do! Task.Delay(Delay).ConfigureAwait(false)
                return! Retry (retryCount + 1) operation
        }
