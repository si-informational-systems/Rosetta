namespace SI.Rosetta.Aggregates

type IAggregateState =
    abstract member Id: string
    abstract member Version: int