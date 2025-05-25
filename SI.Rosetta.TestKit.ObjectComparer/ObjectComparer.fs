namespace SI.Rosetta.TestKit

open System
open System.Collections
open System.Reflection

module ObjectComparer =

    let DeepCompare (a: 'A) (b: 'A) =
        let visited = System.Collections.Generic.HashSet<obj * obj>()
        
        let rec compareObjects path (a: obj) (b: obj) = seq {
            match a, b with
            | null, null -> ()
            | null, _ -> yield sprintf "%s: Left is null, Right is %A" path b
            | _, null -> yield sprintf "%s: Left is %A, Right is null" path a
            | _ ->
                // Check if we've already compared these objects
                if visited.Add(a, b) then  // Returns false if already present
                    let aType = a.GetType()
                    
                    if aType.IsPrimitive || aType = typeof<string> then
                        if not (a.Equals(b)) then
                            yield sprintf "%s: Values differ (%A vs %A)" path a b
                    else
                        if aType <> typeof<string> && typeof<IEnumerable>.IsAssignableFrom(aType) then
                            let enumA = (a :?> IEnumerable) |> Seq.cast<obj> |> List.ofSeq
                            let enumB = (b :?> IEnumerable) |> Seq.cast<obj> |> List.ofSeq
                                
                            if enumA.Length <> enumB.Length then
                                yield sprintf "%s: Size not equal (Left: %d, Right: %d)" path enumA.Length enumB.Length
                            else
                                for i, (elemA, elemB) in Seq.zip enumA enumB |> Seq.indexed do
                                    yield! compareObjects (sprintf "%s[%d]" path i) elemA elemB
                        else
                            let flags = BindingFlags.Public ||| BindingFlags.Instance
                                
                            for prop in aType.GetProperties(flags) do
                                let propA = prop.GetValue(a)
                                let propB = prop.GetValue(b)
                                let newPath = if String.IsNullOrEmpty(path) then prop.Name else sprintf "%s.%s" path prop.Name
                                yield! compareObjects newPath propA propB
                                
                            for field in aType.GetFields(flags) do
                                let fieldA = field.GetValue(a)
                                let fieldB = field.GetValue(b)
                                let newPath = if String.IsNullOrEmpty(path) then field.Name else sprintf "%s.%s" path field.Name
                                yield! compareObjects newPath fieldA fieldB
        }

        let differences = compareObjects "" a b |> Seq.toList
        if differences.IsEmpty then 
            String.Empty 
        else 
            let result = String.concat "\n" differences
            $"{result}\n"