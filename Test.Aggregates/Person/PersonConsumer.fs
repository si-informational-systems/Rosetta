namespace TestFSharp

open Microsoft.Extensions.Logging

type PersonConsumer(handler: IPersonAggregateHandler, logger: ILogger<PersonConsumer>) =
    inherit AggregateConsumerBase<PersonCommands>()
    
    member this.Consume(cmd: RegisterPerson) =
        task {
            do! this.TryHandle(PersonCommands.Register cmd, handler, logger).ConfigureAwait(false)
        }

    member this.Consume(cmd: AddPersonRecord) =
        task {
            do! this.TryHandle(PersonCommands.AddRecord cmd, handler, logger).ConfigureAwait(false)
        }
            
    member this.Consume(cmd: ChangePersonName) =
        task {
            do! this.TryHandle(PersonCommands.ChangeName cmd, handler, logger).ConfigureAwait(false)
        }
