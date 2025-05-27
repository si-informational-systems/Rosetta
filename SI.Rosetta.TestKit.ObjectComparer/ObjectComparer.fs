namespace SI.Rosetta.TestKit

open System
open System.Collections
open System.Reflection

module ObjectComparer =

    /// Performs a deep comparison between two objects of the same type.
    /// Returns an empty string if objects are equal, otherwise returns a string containing all differences.
    /// Handles primitive types, strings, collections, pointers, and complex objects with their properties and fields.
    /// Also detects and handles circular references to prevent infinite recursion.
    let DeepCompare (a: 'A, b: 'A) =
        // Set to track visited object pairs to prevent circular reference infinite loops
        let visited = Generic.HashSet<obj * obj>()
        
        /// Recursively compares two objects and yields their differences
        /// path: The property/field path to the current comparison point
        /// a: First object to compare
        /// b: Second object to compare
        let rec compareObjects path (a: obj) (b: obj) = seq {
            match a, b with
            // Handle null cases
            | null, null -> ()
            | null, _ -> yield sprintf "%s: Left is null, Right is %A" path b
            | _, null -> yield sprintf "%s: Left is %A, Right is null" path a
            | _ ->
                // Only proceed with comparison if this object pair hasn't been visited
                // This prevents infinite recursion with circular references
                if visited.Add(a, b) then
                    let aType = a.GetType()
                    
                    // Special handling for pointer types (IntPtr and UIntPtr)
                    if aType = typeof<IntPtr> then
                        let pointerA = (a :?> IntPtr).ToInt64()
                        let pointerB = (b :?> IntPtr).ToInt64()
                        if pointerA <> pointerB then
                            yield sprintf "%s: IntPtr values differ (%A vs %A)" path pointerA pointerB
                    elif aType = typeof<UIntPtr> then
                        let pointerA = (a :?> UIntPtr).ToUInt64()
                        let pointerB = (b :?> UIntPtr).ToUInt64()
                        if pointerA <> pointerB then
                            yield sprintf "%s: UIntPtr values differ (%A vs %A)" path pointerA pointerB
                    
                    // Handle primitive types and strings using direct equality comparison
                    elif aType.IsPrimitive || aType = typeof<string> then
                        if not (a.Equals(b)) then
                            yield sprintf "%s: Values differ (%A vs %A)" path a b
                    else
                        // Handle collections (except strings which are also IEnumerable)
                        if aType <> typeof<string> && typeof<IEnumerable>.IsAssignableFrom(aType) then
                            // Convert both collections to lists of objects for comparison
                            let enumA = (a :?> IEnumerable) |> Seq.cast<obj> |> List.ofSeq
                            let enumB = (b :?> IEnumerable) |> Seq.cast<obj> |> List.ofSeq
                                
                            // Compare elements up to the shorter list length
                            let minLength = min enumA.Length enumB.Length
                            for i in 0 .. minLength - 1 do
                                yield! compareObjects (sprintf "%s[%d]" path i) enumA[i] enumB[i]
                                
                            // Report size difference if lists are not equal length
                            if enumA.Length <> enumB.Length then
                                yield sprintf "%s: Size not equal (Left: %d, Right: %d)" path enumA.Length enumB.Length
                        else
                            // Handle complex objects by comparing their public properties and fields
                            let flags = BindingFlags.Public ||| BindingFlags.Instance
                                
                            // Compare all public instance properties
                            for prop in aType.GetProperties(flags) do
                                let propA = prop.GetValue(a)
                                let propB = prop.GetValue(b)
                                let newPath = if String.IsNullOrEmpty(path) then prop.Name else sprintf "%s.%s" path prop.Name
                                yield! compareObjects newPath propA propB
                                
                            // Compare all public instance fields
                            for field in aType.GetFields(flags) do
                                let fieldA = field.GetValue(a)
                                let fieldB = field.GetValue(b)
                                let newPath = if String.IsNullOrEmpty(path) then field.Name else sprintf "%s.%s" path field.Name
                                yield! compareObjects newPath fieldA fieldB
        }

        // Perform the comparison and collect all differences
        let differences = compareObjects "" a b |> Seq.toList
        if differences.IsEmpty then 
            String.Empty  // Objects are equal
        else 
            // Join all differences with newlines and add a trailing newline
            let result = String.concat $"{Environment.NewLine}" differences
            $"{result}{Environment.NewLine}"