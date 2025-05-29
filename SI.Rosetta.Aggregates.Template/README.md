# SI.Rosetta Aggregate Template

This Visual Studio template creates a complete SI.Rosetta aggregate structure with three files:

1. `{Name}AggregateState.fs` - The aggregate state class
2. `{Name}Aggregate.fs` - The aggregate class with command execution logic
3. `{Name}AggregateHandler.fs` - The aggregate handler with async command processing

## Installation

### Method 1: Manual Installation
1. Copy this template folder to your Visual Studio templates directory:
   - For VS 2022: `%USERPROFILE%\Documents\Visual Studio 2022\Templates\ItemTemplates\FSharp\`
   - For VS 2019: `%USERPROFILE%\Documents\Visual Studio 2019\Templates\ItemTemplates\FSharp\`

2. Restart Visual Studio

### Method 2: Export as Template
1. In Visual Studio, go to `Project` > `Export Template...`
2. Select "Item Template"
3. Choose the template files
4. Follow the wizard to create the template

## Usage

1. Right-click on your F# project in Solution Explorer
2. Select "Add" > "New Item..."
3. Choose "SI.Rosetta Aggregate" from the templates
4. Enter the name for your aggregate (e.g., "Product", "Order", "Customer")
5. Click "Add"

The template will create three files with the provided name:
- `ProductAggregateState.fs`
- `ProductAggregate.fs` 
- `ProductAggregateHandler.fs`

## Template Parameters

- `$rootnamespace$` - Uses the current project's namespace
- `$fileinputname$` - The name you provide when creating the template (e.g., "Product")

## Prerequisites

Before using this template, ensure you have:

1. **Published Language (PL) Messages**: Create your Commands and Events types in the PL project:
   ```fsharp
   // In PL project: ProductMessages.fs
   type ProductCommands = 
       | CreateProduct of CreateProduct
       // ... other commands
       interface IAggregateCommands

   type ProductEvents = 
       | ProductCreated of ProductCreated
       // ... other events  
       interface IAggregateEvents
   ```

2. **Project References**: Ensure your aggregate project references:
   - `SI.Rosetta.Aggregates`
   - Your PL project containing the Commands and Events

## Template Structure

The generated files provide a basic structure that you can extend:

### AggregateState
- Inherits from `AggregateState<{Name}Events>`
- Contains a default `ApplyEvent` implementation
- Add your state properties and specific event handling logic

### Aggregate  
- Inherits from `Aggregate<{Name}AggregateState, {Name}Commands, {Name}Events>`
- Contains a default `Execute` implementation
- Add your business logic and command handling

### AggregateHandler
- Inherits from `AggregateHandler<{Name}Aggregate, {Name}Commands, {Name}Events>`
- Implements `I{Name}AggregateHandler` interface
- Contains a default `ExecuteAsync` implementation
- Add your specific command routing logic

## Next Steps

After generating the aggregate files:

1. Implement your specific command handling in the `Execute` method
2. Add state properties and implement proper event application in `ApplyEvent`
3. Implement command routing in the handler's `ExecuteAsync` method
4. Register the handler in your DI container
5. Create unit tests for your aggregate behavior 