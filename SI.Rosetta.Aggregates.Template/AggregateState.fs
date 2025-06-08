namespace $rootnamespace$.$fileinputname$

open SI.Rosetta.Aggregates

type $fileinputname$AggregateState() =
    inherit AggregateState<$fileinputname$Events>()

    override this.ApplyEvent(ev: $fileinputname$Events) =
        match ev with
        | _ -> () 