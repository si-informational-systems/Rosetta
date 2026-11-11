namespace SI.Rosetta.Common

open System
open System.Threading
open System.Threading.Tasks

/// How a conflict is retried at once: a ConcurrencyException, or a store's own (a MongoDB write conflict, a PostgreSQL
/// deadlock). Up to Retries more attempts, each after a random pause of 1 ms to MaxJitter, so writers that collided
/// don't collide again in step. AggregateHandler retries a command's load, execute and store; a projection retries an
/// event's transaction. Only then does the caller see the failure, or a projection's subscription back off
type ConflictRetries =
    { /// Attempts after the first; 0 turns the retries off
      Retries: int
      /// The longest random pause before a retry
      MaxJitter: TimeSpan }

    /// 3 retries, each after up to 50 ms
    static member Default = { Retries = 3; MaxJitter = TimeSpan.FromMilliseconds 50.0 }

    /// No retries: a conflict fails at once
    static member None = { Retries = 0; MaxJitter = TimeSpan.Zero }

    /// The pause before a retry: 1 ms to MaxJitter
    member this.PauseAsync(cancellationToken: CancellationToken) : Task =
        let longest = max 1 (int this.MaxJitter.TotalMilliseconds)
        Task.Delay(Random.Shared.Next(1, longest + 1), cancellationToken)

module ExceptionCauses =
    /// The exception and every exception inside it: inner ones, and each of an AggregateException's
    let rec Of (ex: exn) : exn seq =
        seq {
            if not (isNull ex) then
                yield ex
                match ex with
                | :? AggregateException as aggregate -> for inner in aggregate.InnerExceptions do yield! Of inner
                | _ -> yield! Of ex.InnerException
        }

    /// Whether a ConcurrencyException caused it
    let IsConcurrencyConflict (ex: exn) =
        Of ex |> Seq.exists (fun cause -> cause :? ConcurrencyException)
