namespace $rootnamespace$.$fileinputname$

open SI.Rosetta.Aggregates

[<AllowNullLiteral>]
type $fileinputname$Aggregate() =
    inherit Aggregate<$fileinputname$AggregateState, $fileinputname$Commands, $fileinputname$Events>()

    override this.Execute(command: $fileinputname$Commands) =
        match command with
        | _ -> () 