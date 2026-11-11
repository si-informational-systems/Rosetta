namespace SI.Rosetta.Aggregates

open System

/// Another command changed the aggregate after this one loaded it (or created it after this one found it missing):
/// nothing was stored. Run the command again, on the aggregate as it is now
type ConcurrencyException(message: string, inner: exn) =
    inherit Exception(message, inner)

    new(message: string) = ConcurrencyException(message, null)

    /// A new aggregate whose Id exists by the time it's stored
    static member Created(stateType: Type, id: string, ?inner: exn) =
        ConcurrencyException(
            sprintf "%s %s already exists: another command created it after this one found it missing." stateType.Name id,
            Option.toObj inner)

    /// A loaded aggregate whose stored state is no longer at the version it was loaded at
    static member Changed(stateType: Type, id: string, loaded: int64, ?inner: exn) =
        ConcurrencyException(
            sprintf "%s %s is no longer at version %d: another command changed it after this one loaded it." stateType.Name id loaded,
            Option.toObj inner)
