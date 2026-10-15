namespace SI.Rosetta.Aggregates

open System.Threading.Tasks
open SI.Rosetta.Common

/// The aggregate as its handler sees it. It loads and stores itself, as only it knows its state type
type IAggregateInstance<'TCommands
    when 'TCommands :> IAggregateCommands> =
    inherit IAggregate
    abstract member Execute: command: 'TCommands -> unit
    /// Loads the aggregate's state from the repository; false when the aggregate doesn't exist
    abstract member LoadAsync: repository: IAggregateRepository * id: string -> Task<bool>
    abstract member StoreAsync: repository: IAggregateRepository -> Task
