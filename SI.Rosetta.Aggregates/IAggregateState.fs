namespace SI.Rosetta.Aggregates

[<AllowNullLiteral>]
type IAggregateState =
    abstract member Id: string
    abstract member Version: int