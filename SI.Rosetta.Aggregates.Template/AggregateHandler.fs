namespace $rootnamespace$.$fileinputname$

open SI.Rosetta.Aggregates

type I$fileinputname$AggregateHandler =
    inherit IAggregateHandler

// Use IStateBasedAggregateRepository instead to persist the AggregateState without Event Sourcing
type $fileinputname$AggregateHandler(repo: IEventSourcedAggregateRepository) =
    inherit AggregateHandler<$fileinputname$Aggregate, $fileinputname$Commands>()
    do base.AggregateRepository <- repo
    interface I$fileinputname$AggregateHandler
    
    override this.ExecuteAsync(command: $fileinputname$Commands) =
        task {
            match command with
            | _ -> ()
        } 