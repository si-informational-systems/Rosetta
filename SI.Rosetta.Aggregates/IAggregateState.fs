namespace SI.Rosetta.Aggregates

[<AllowNullLiteral>]
type IAggregateState =
    abstract member Id: obj
    abstract member Version: int