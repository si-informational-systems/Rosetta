namespace SI.Rosetta.Aggregates


type IAggregateState =
    abstract member Id: obj
    abstract member Version: int