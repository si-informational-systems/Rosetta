namespace $rootnamespace$.$fileinputname$

open SI.Rosetta.Aggregates

type I$fileinputname$AggregateHandler =
    inherit IAggregateHandler

type $fileinputname$AggregateHandler(repo: IAggregateRepository) = 
    inherit AggregateHandler<$fileinputname$Aggregate, $fileinputname$Commands, $fileinputname$Events>()
    do base.AggregateRepository <- repo
    interface I$fileinputname$AggregateHandler
    
    override this.ExecuteAsync(command: $fileinputname$Commands) =
        task {
            match command with
            | _ -> ()
        } 