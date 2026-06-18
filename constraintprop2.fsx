#I @"C:\Users\cybernetic\Jupyter-Notebooks"
#load "maths-repl.fsx"

open System
open Prelude.Common
open Prelude.Trie
open Prelude
open Prelude.TrieDictionarySearch
open MathNet.Symbolics.Core
open MathNet.Symbolics
open MathNet.Symbolics.Core.Vars
open MathNet.Symbolics.Utils
open MathNet.Symbolics.NumberProperties
open MathNet.Symbolics.Units
open MathNet.Symbolics.Utils.Constants
open MathNet.Symbolics.NumberProperties.Expression
open System.Net
open Prelude.Math
open System.Net.Http
open Newtonsoft.Json
open MathNet.Symbolics.LinearAlgebra
open MathNet.Symbolics.Equations
open MathNet.Symbolics.Solving
open Hansei
open Hansei.TreeSearch.LazyList
open Hansei.FSharpx.Collections

#r "nuget: FSharp.Data.Adaptive, 1.2.16"

open FSharp.Data
open FSharp.Data.Adaptive
open MathNet.Numerics.LinearAlgebra
open MathNet.Numerics.LinearAlgebra.Double
open MathNet.Numerics.Distributions

#time "on"

(*
F# Adaptive:
Focus: Fine-grained reactivity at the data level. It provides a way to define and manage changeable values and their dependencies.
Mechanism: Relies on a dependency graph and incremental recomputation to efficiently propagate changes.
Data-centric: Primarily concerned with how data changes and how those changes affect dependent computations.
Integration with UI: Can be used as a building block for implementing UI frameworks or integrated with existing ones like Elmish.*)

// F# Adaptive is an incremental computation library that defines computations that automatically update when inputs change by tracking dependencies between changeable values (cells) and derived values (adaptive values). It operates on a Directed Acyclic Graph (DAG) of dependencies, ensuring efficient updates by re-evaluating only the necessary parts of the computation. This approach allows for efficient management and propagation of changes without redundant computations, but it cannot handle circular dependencies due to the uni-directional dependency tracking inherent to DAGs.

//However we can build such a datastructure atop F# Adaptive. A cell consists of a changeable value and a list of dependent cells. When we change the value of a cell, we can propagate the change to all dependent cells. When an upstream cell is changed which affects any of the list of dependent cells, the changable value for the Cell is also changed as long as there are no conflicts.

/// The AdaptiveExpression<'a> class provides a wrapper around F# Adaptive's adaptive values (aval<'a>)
/// to enable arithmetic operations on adaptive values in a more natural way. It implements operator overloading
/// for basic arithmetic operations (+, -, *, /) allowing adaptive values to be combined mathematically while
/// maintaining their reactive/adaptive nature.
///
/// The class handles multiple type combinations in the arithmetic operations:
/// - Between two AdaptiveExpressions
/// - Between AdaptiveExpression and raw adaptive values (aval)
/// - Between AdaptiveExpression and float constants
/// - Between optional (Option<'a>) and non-optional values
///
/// This enables building complex mathematical expressions with adaptive values that automatically
/// update when any input value changes, similar to how spreadsheet formulas work. The class acts as
/// a bridge between F# Adaptive's incremental computation system and mathematical operations,
/// making it easier to express mathematical relationships between changing values.
///
/// The class preserves the adaptive nature of values while providing a more familiar mathematical syntax,
/// effectively creating a domain-specific language for reactive mathematical expressions.
type AdaptiveExpression<'a>(adaptiveVal: aval<'a>) =
    //constructor from a float
    member this.Value = AVal.force adaptiveVal
    member this.State = adaptiveVal

    member this.map f =
        AdaptiveExpression(AVal.map f adaptiveVal)

    override this.ToString() : string = this.Value.ToString()

    static member inline (+)(a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State b.State)

    static member inline (+)(a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (+) a.State b.State)

    static member inline (+)(a: aval<_>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) (AVal.map Some a) b.State)

    static member inline (+)(a: AdaptiveExpression<Option<_>>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State (AVal.map Some b))

    static member inline (+)(a: AdaptiveExpression<_>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (+) a.State b)

    static member inline (+)(a: aval<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (+) a b.State)

    static member inline (+)(a: AdaptiveExpression<Option<_>>, b: float) =
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State (AVal.constant (Some b)))

    static member inline (+)(a: AdaptiveExpression<_>, b: float) =
        AdaptiveExpression(AVal.map2 (+) a.State (AVal.constant b))

    static member inline (+)(a: float, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (+) (AVal.constant a) b.State)

    static member inline (-)(a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) a.State b.State)

    static member inline (-)(a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (-) a.State b.State)

    static member inline (-)(a: aval<_>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) (AVal.map Some a) b.State)

    static member inline (-)(a: AdaptiveExpression<Option<_>>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) a.State (AVal.map Some b))

    static member inline (-)(a: AdaptiveExpression<_>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (-) a.State b)

    static member inline (-)(a: aval<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (-) a b.State)

    static member inline (-)(a: AdaptiveExpression<_>, b: float) =
        AdaptiveExpression(AVal.map2 (-) a.State (AVal.constant b))

    static member inline (-)(a: float, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (-) (AVal.constant a) b.State)

    static member inline (*)(a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State b.State)

    static member inline (*)(a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (*) a.State b.State)

    static member inline (*)(a: aval<_>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) (AVal.map Some a) b.State)

    static member inline (*)(a: AdaptiveExpression<Option<_>>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State (AVal.map Some b))

    static member inline (*)(a: AdaptiveExpression<_>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (*) a.State b)

    static member inline (*)(a: aval<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (*) a b.State)

    static member inline (*)(a: float, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) (AVal.constant (Some a)) b.State)

    static member inline (*)(a: AdaptiveExpression<Option<_>>, b: float) =
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State (AVal.constant (Some b)))

    static member inline (*)(a: AdaptiveExpression<_>, b: float) =
        AdaptiveExpression(AVal.map2 (*) a.State (AVal.constant b))

    static member inline (*)(a: float, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (*) (AVal.constant a) b.State)

    static member inline (/)(a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State b.State)

    static member inline (/)(a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (/) a.State b.State)

    static member inline (/)(a: aval<_>, b: AdaptiveExpression<Option<_>>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) (AVal.map Some a) b.State)

    static member inline (/)(a: AdaptiveExpression<Option<_>>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State (AVal.map Some b))

    static member inline (/)(a: AdaptiveExpression<_>, b: aval<_>) =
        AdaptiveExpression(AVal.map2 (/) a.State b)

    static member inline (/)(a: aval<_>, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (/) a b.State)

    static member inline (/)(a: AdaptiveExpression<Option<_>>, b: float) =
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State (AVal.constant (Some b)))

    static member inline (/)(a: AdaptiveExpression<_>, b: float) =
        AdaptiveExpression(AVal.map2 (/) a.State (AVal.constant b))

    static member inline (/)(a: float, b: AdaptiveExpression<_>) =
        AdaptiveExpression(AVal.map2 (/) (AVal.constant a) b.State)


/// Structured error model for Cell
type CellError<'T> =
  /// Minimize could not reconcile two values
  | MinimizeConflict   of cellName:string * left:'T * right:'T * candidates:'T list * userMessage:string 
  /// General inconsistency detected with a human message (escape hatch)
  | InconsistencyConflict of cellName:string * message:string
  /// Circuit breaker tripped due to rapid oscillations
  | CircuitBreakerTriggered of cellName:string 
  /// Upstream dependency emitted a generic error string
  | UpstreamError      of cellName:string * message:string
  /// Unexpected/unhandled failure
  | Unexpected         of cellName:string * message:string
  /// Domain became empty (e.g., constraint intersection = ∅)
  | DomainEmpty        of cellName:string
  /// One or more dependencies errored; we capture first and count
  | DependencyErrors    of cellName:string * firstError:string * totalErrors:int 

  override this.ToString() =
    match this with 
    | MinimizeConflict(n,l,r,cs, "") ->
        sprintf "[MinimizeConflict] %s: '%A' vs '%A' among %A" n l r cs
    | MinimizeConflict(n,l,r,cs,msg) ->
        sprintf "[MinimizeConflict] %s: '%A' vs '%A' among %A (user: %s)" n l r cs msg
    | InconsistencyConflict(n,m) -> sprintf "[Inconsistency] %s: %s" n m
    | CircuitBreakerTriggered(n) -> sprintf "[CircuitBreaker] triggered for %s" n
    | DependencyErrors(n,e,k) -> sprintf "[DependencyErrors] %s: first=%s total=%d" n e k
    | DomainEmpty n -> sprintf "[DomainEmpty] %s" n
    | UpstreamError(n,m) -> sprintf "[UpstreamError] %s: %s" n m
    | Unexpected(n,m) -> sprintf "[Unexpected] %s: %s" n m

/// Represents a cell interface that manages a value with dependencies in an adaptive computation system.
/// This interface defines the core functionality for a cell that can hold a value and track dependencies
/// to other cells, enabling reactive updates when dependent values change.
/// The interface supports:
/// - Managing a cell value with error handling (Cell property)
/// - Accessing the current value safely as an option (CellValue property)
/// - Tracking dependent cells (DependentCells property)
/// - Adding new dependencies (AddDependency method)
/// - Proper resource cleanup (IDisposable inheritance)
type ICell<'T when 'T : equality> =
    abstract member Value: aval<Result<'T, CellError<'T>>>
    abstract member AddDependency: aval<Result<'T, CellError<'T>>> -> unit
    inherit IDisposable

/// <summary>
/// Represents a reactive cell implementation that manages bidirectional dependencies between values,
/// with support for value coarsening, equality comparison, and circuit breaker protection against
/// infinite update loops.
///
/// The Cell type provides:
/// - Bidirectional dependency tracking between cells
/// - Value coarsening to handle floating point precision issues
/// - Custom equality comparison for values
/// - Circuit breaker protection against infinite update loops
/// - Safe and unsafe value access methods
/// - Automatic dependency cleanup through IDisposable
///
/// The cell implements a form of constraint propagation where changes to dependent cells
/// are reconciled using the minimize function, with circuit breaker protection against
/// oscillating updates.
/// </summary>
/// <param name="initial">The initial value to store in the cell.</param>
/// <param name="coarsen">Optional function to coarsen values for comparison (e.g., rounding for floating point numbers). Defaults to identity function if not provided.</param>
/// <param name="isEqual">Optional custom equality comparison function. If not provided, uses coarsen function for equality comparison.</param>
/// <param name="cellname">Optional name for the cell used in debug messages and error reporting. Empty string if not provided.</param>
/// <param name="minimize">Optional function to resolve conflicts between multiple dependencies. Takes two values and returns Some value if they can be reconciled, None if they conflict. Defaults to equality check if not provided.</param>
/// <param name="circuitFailureThreshold">Optional threshold for the number of updates allowed within the update window before the circuit breaker triggers. Defaults to 50 if not provided.</param>
/// <param name="logErrors">Optional flag to enable/disable error logging. Defaults to false if not provided.</param> 
type Cell<'T when 'T : equality> 
    ( initial: 'T,
      ?coarsen: 'T -> 'T,
      ?isEqual: 'T -> 'T -> bool,
      ?cellname: string,
      ?minimize: 'T -> 'T -> Result<'T,string>,
      ?circuitFailureThreshold: int,
      ?logErrors: bool ) =   

    // --- ARCHITECTURAL SEPARATION: Two distinct values ---
    let baseValue = cval initial                                    // User's intent only
    let cell = cval (Ok initial )       // Computed result only
    let disposables = ResizeArray<IDisposable>()

    let cellname = defaultArg cellname ""
    let cellnameDbg = if cellname = "" then "" else cellname + " "
    let logErrors = defaultArg logErrors false
    let coarsen = defaultArg coarsen id
    let isEqual = defaultArg isEqual (fun a b -> coarsen a = coarsen b)

    // helpers to make errors
    let mkMinConflict (l:'T) (r:'T) (cs:'T list) (userMsg:string) =
        MinimizeConflict(cellname, l, r, cs, userMsg)
    let mkCircuitError() =
        CircuitBreakerTriggered(cellname)
    let mkDepErrors (errs: CellError<'T> list) =
        let first =
            match errs with
            | [] -> Unexpected(cellname, "no dependency error provided")
            | e :: _ -> e
        DependencyErrors(cellname, first.ToString(), errs.Length)

    // helper to update an error (and optionally dump it)
    let setError (e: CellError<'T>) =
        if logErrors then
            printfn "Cell %s error: %O" cellnameDbg e
        transact (fun () -> cell.Value <- Error e)
        
    // adapter: convert user minimize (string) -> internal minimize (CellError)
    let userMinimize =
        defaultArg minimize (fun a b -> if isEqual a b then Ok a else Error "Default minimize: values are not equal")

    let minimizeCE (a: 'T) (b: 'T) : Result<'T, CellError<'T>> =
        match userMinimize a b with
        | Ok v      -> Ok v
        | Error msg -> Error (InconsistencyConflict(cellname, msg))

    // let rec runMinimize acc rest original : Result<'T, CellError<'T>> =
    //     match rest with
    //     | [] -> Ok acc
    //     | x::xs ->
    //         match minimizeCE acc x with
    //         | Ok v -> runMinimize v xs original
    //         | Error (InconsistencyConflict(cellName, userMessage)) ->
    //             Error (MinimizeConflict(cellName, acc, x, original, userMessage))
    //         | Error otherError ->
    //             Error otherError

    let mutable updateCount = 0
    let mutable lastUpdateTime = System.DateTime.MinValue
    let mutable failureThreshold = defaultArg circuitFailureThreshold 50
    let updateWindow = System.TimeSpan.FromSeconds(1.0)

    let checkCircuitBreaker () =
        let now = System.DateTime.Now
        if (now - lastUpdateTime) < updateWindow then
            updateCount <- updateCount + 1
            if updateCount >= failureThreshold then
                false
            else
                lastUpdateTime <- now
                true
        else
            updateCount <- 1
            lastUpdateTime <- now
            true

    // --- REACTIVE ARCHITECTURE: All inputs managed centrally ---
    let allInputs = clist [ (baseValue :> aval<_>) |> AVal.map Ok ]

    // Aggregator function for folding over all inputs
    let reduceValues acc next =
        AVal.map2 (fun accRes nextRes ->
            match accRes, nextRes with
            | Error e, _   // First error wins
            | _, Error e -> Error e  
            | Ok l, Ok r -> minimizeCE l r
        ) acc next
        
    let finalResult =
        allInputs
        |> AList.fold reduceValues (AVal.constant (Ok initial)) 
        |> AVal.bind id
     
    // Single callback that updates the cell
    let setupReactiveLogic() =
       finalResult.AddCallback(fun result -> 
            if checkCircuitBreaker() then 
                    transact (fun () -> cell.Value <- result) 
            else 
                setError (mkCircuitError())
       ) |> disposables.Add

    do setupReactiveLogic()
    member this.FinalResult = finalResult
    // --- PUBLIC INTERFACE ---
    member this.CircuitFailureThreshold
        with get () = failureThreshold
        and set v = failureThreshold <- v

    member this.Cell = cell     
    member this.CellUnsafe = AVal.map (Result.toOption >> Option.get) cell
    
    member this.Value
        with get () =
            match AVal.force cell with
            | Ok v -> Some v
            | Error _ -> None
        and set v =
            match v with
            | Some value -> transact (fun () -> baseValue.Value <- value)
            | None -> ()

    member this.ValueUnsafe
        with get () =
            match AVal.force cell with
            | Ok v -> v
            | Error e -> failwithf "Error: %scell has an error value: %O" cellnameDbg e
        and set v = transact (fun () -> baseValue.Value <- v)

    member this.SetValue v = this.Value <- Some v
    member this.Values =  
        allInputs.Value |> Seq.map AVal.force
    
    member this.DependentCells = allInputs

    // Lifting overloads
    member this.AddDependency(adaptiveval: aval<'U>, f: 'U -> 'T) =
        this.AddDependency(AVal.map (f >> Ok) adaptiveval)

    member this.AddDependency(adaptiveval: aval<Result<'U, CellError<'U>>>, f: 'U -> 'T) =
        this.AddDependency(AVal.map (fun r ->
            match r with
            | Ok u -> Ok (f u)
            | Error e -> Error (UpstreamError(cellname, e.ToString()))
        ) adaptiveval)

    member this.AddDependency(adaptivevalue: aval<'T>) =
        this.AddDependency(AVal.map Ok adaptivevalue)

    // AddDependency now simply adds to the clist
    member this.AddDependency(dependency: aval<Result<'T, CellError<'T>>>) =
        transact (fun () -> allInputs.Add dependency |> ignore)

    interface ICell<'T> with
        member this.Value = cell
        member this.AddDependency(d) = this.AddDependency(d)

    interface IDisposable with
        member this.Dispose() =
            for d in disposables do d.Dispose()


type Cell2<'T when 'T : equality> 
    ( initial: 'T,
      ?coarsen: 'T -> 'T,
      ?isEqual: 'T -> 'T -> bool,
      ?cellname: string,
      ?minimize: 'T -> 'T -> Result<'T,string>,
      ?circuitFailureThreshold: int,
      ?checkAssignments: bool,
      ?logErrors: bool ) =   
    let cell = cval (Ok initial)
    let dependentCells = ResizeArray<aval<Result<'T, CellError<'T>>>>()
    let disposables = ResizeArray<IDisposable>()

    let cellname = defaultArg cellname ""
    let cellnameDbg = if cellname = "" then "" else cellname + " "
    let logErrors = defaultArg logErrors false
    let coarsen = defaultArg coarsen id
    let isEqual = defaultArg isEqual (fun a b -> coarsen a = coarsen b)
    let checkAssignments = defaultArg checkAssignments true

    // helpers to make errors
    let mkMinConflict (l:'T) (r:'T) (cs:'T list) (userMsg:string) =
        MinimizeConflict(cellname, l, r, cs, userMsg)
    let mkCircuitError() =
        CircuitBreakerTriggered(cellname)
    let mkDepErrors (errs: CellError<'T> list) =
        let first =
            match errs with
            | [] -> Unexpected(cellname, "no dependency error provided")
            | e :: _ -> e
        DependencyErrors(cellname, first.ToString(), errs.Length)

    // helper to update an error (and optionally dump it)
    let setError (e: CellError<'T>) =
        if logErrors then
            printfn "Cell %s error: %O" cellnameDbg e
        transact (fun () -> cell.Value <- Error e)
        
    // adapter: convert user minimize (string) -> internal minimize (CellError)
    let userMinimize =
        defaultArg minimize (fun a b -> if isEqual a b then Ok a else Error "Default minimize: values are not equal")

    // This function is the "boundary" between user code and system code.
    // It can ONLY produce an InconsistencyConflict on failure.
    let minimizeCE (a: 'T) (b: 'T) : Result<'T, CellError<'T>> =
        match userMinimize a b with
        | Ok v      -> Ok v
        | Error msg -> Error (InconsistencyConflict(cellname, msg))

    let rec runMinimize acc rest original : Result<'T, CellError<'T>> =
        match rest with
        | [] -> Ok acc
        | x::xs ->
            match minimizeCE acc x with
            | Ok v -> runMinimize v xs original
            | Error (InconsistencyConflict(cellName, userMessage)) ->
                // This is the ONLY possible error from minimizeCE.
                // We now enrich it with the full structural context.
                Error (MinimizeConflict(cellName, acc, x, original, userMessage))
            | Error otherError ->
                // This path is logically unreachable, but required by the type checker.
                // We handle it by simply passing the unexpected error up.
                // This is robust; if we change minimizeCE later, this code won't break.
                Error otherError
    
    // The recursive loop function that checks a value against a sequence of constraints.
    let rec checkValue (index: int) = function 
        | Error e -> Error e // Early exit if we have a conflict
        | Ok _ as valueToTest when index >= dependentCells.Count ->
            // We've successfully checked all dependencies
            valueToTest
        | Ok currentVal ->
            // Get the dependency at the current index
            let currentConstraint = dependentCells.[index]
            match AVal.force currentConstraint with
            | Error e ->
                Error (Unexpected(cellname, sprintf "Constraint provider at index %d failed: %A" index e))
            | Ok constraintVal ->
                let nextValueToTest = minimizeCE currentVal constraintVal
                // Continue the loop with the next index
                checkValue (index + 1) nextValueToTest

    let mutable updateCount = 0
    let mutable lastUpdateTime = System.DateTime.MinValue
    let mutable failureThreshold = defaultArg circuitFailureThreshold 50
    let updateWindow = System.TimeSpan.FromSeconds(1.0)

    let checkCircuitBreaker () =
        let now = System.DateTime.Now
        if (now - lastUpdateTime) < updateWindow then
            updateCount <- updateCount + 1
            if updateCount >= failureThreshold then
                printfn $"Circuit breaker triggered for {cellname}cell due to rapid oscillations."
                false
            else
                lastUpdateTime <- now
                true
        else
            updateCount <- 1
            lastUpdateTime <- now
            true

    member this.CircuitFailureThreshold
        with get () = failureThreshold
        and set v = failureThreshold <- v

    member this.Cell = cell    
    member this.CellUnsafe = AVal.map (Result.toOption >> Option.get) cell
    member this.Value
        with get () =
            match AVal.force cell with
            | Ok v -> Some v
            | Error _ -> None
        and set v =
            if not checkAssignments then
                transact (fun () ->
                    match v with
                    | None -> ()
                    | Some v -> cell.Value <- Ok v)
            else 
                match v with
                | None -> ()
                | Some newValue ->
                    // Start the check
                    let initialValue = Ok newValue
                    let testResult = checkValue 0 initialValue

                    // Commit the final result of the check
                    transact (fun () ->
                        match testResult with
                        | Ok reconciledValue ->
                            cell.Value <- Ok reconciledValue
                        | Error e ->
                            // The assignment was invalid. Report a clear error.
                            let assignmentError =
                                InconsistencyConflict(cellname, sprintf "Invalid assignment of '%A' failed validation. Details: %O" newValue e)
                            cell.Value <- Error assignmentError
                    )

    member this.ValueUnsafe
        with get () =
            match AVal.force cell with
            | Ok v -> v
            | Error e -> failwithf "Error: %scell has an error value: %O" cellnameDbg e
        and set v = transact (fun () -> cell.Value <- Ok v)

    member this.SetValue v = this.Value <- Some v

    member this.Values = [ for dcell in dependentCells -> AVal.force dcell ]
    member this.DependentCells = dependentCells

    member this.AddDependency(adaptiveval: aval<'U>, f) =
       this.AddDependency(AVal.map (f >> Ok) adaptiveval)

    member this.AddDependency(adaptiveval: aval<Result<'U, CellError<'U>>>, f: 'U -> 'T) =
        this.AddDependency(AVal.map (fun r ->
            match r with
            | Ok u -> Ok (f u)
            | Error e -> Error (UpstreamError(cellname, e.ToString()))
        ) adaptiveval)

    member this.AddDependency(adaptivevalue: aval<'T>) =
        this.AddDependency(AVal.map Ok adaptivevalue)

    member this.AddDependency(dependency: aval<Result<'T, CellError<'T>>>) =
        dependentCells.Add dependency

        // Create a callback that triggers when the dependency changes
        let disposable =
            dependency.AddCallback(fun _ ->
                // Check circuit breaker before processing updates
                if checkCircuitBreaker () then
                    // Collect all dependecies and
                    // partition the results into 'Ok' candidates and 'Error's. 
                    let candidates, errors = 
                        dependentCells |> Seq.fold (fun (oks, errs) dep -> 
                            match AVal.force dep with
                            | Ok v -> v :: oks, errs
                            | Error e -> oks, e :: errs
                        ) ([], [])
                    match errors with
                    | _ :: _ ->
                        let err = mkDepErrors errors
                        //transact (fun () -> cell.Value <- Error err)
                        setError err
                    | [] ->
                        match candidates with
                        | h :: t ->
                            // Try to reconcile all candidate values using minimize function
                            match runMinimize h t candidates with
                            | Ok v ->
                                match AVal.force cell with
                                | Ok currentVal when isEqual currentVal v -> ()
                                | _ -> transact (fun () -> cell.Value <- Ok v)
                            | Error e ->
                                setError e
                                //transact (fun () -> cell.Value <- Error e)
                        | [] -> ()
                else
                    transact (fun () -> cell.Value <- Error (mkCircuitError()))
            )

        // Register the callback for proper cleanup
        disposables.Add disposable

    interface ICell<'T> with
        member this.Value = cell :> aval<Result<'T, CellError<'T>>>
        member this.AddDependency(d) = this.AddDependency(d)

    interface IDisposable with
        member this.Dispose() =
            for d in disposables do d.Dispose()



//Example usage
let cellFahrenheit =
    new Cell2<float>(32., isEqual = (fun a b -> round 5 a = round 5 b), logErrors = true, coarsen = round 5)

let cellCelsius = new Cell2<float>(0., coarsen = round 5, logErrors = true)

cellFahrenheit.AddDependency(cellCelsius.Cell, (fun c -> (c * 9. / 5.) + 32.))

cellCelsius.AddDependency(cellFahrenheit.Cell, (fun f -> (f - 32.) * 5. / 9.))

cellFahrenheit.Cell

cellCelsius.Value <- Some 100
//cellCelsius.ValueUnsafe <- 100

cellFahrenheit.SetValue 98

cellCelsius.Value


let scrate = new Cell<_>(1., round 5)
let acurate = new Cell<_>(1., round 5)

let numsupercomputers = AdaptiveExpression(scrate.CellUnsafe)
let adaptiveControlUnit = AdaptiveExpression(acurate.CellUnsafe)

let numcomputers = (numsupercomputers * 7.5 + adaptiveControlUnit * 2. + 0.5) / 2.5
let numcomputersCell = new Cell<_>(numcomputers.Value, round 5)

numcomputersCell.AddDependency(numcomputers.State)

scrate.AddDependency(
    AVal.map (fun nc -> -adaptiveControlUnit.Value * 4. / 15. - 1. / 15. + nc / 3.) numcomputersCell.CellUnsafe
)

let highspeedconnector = numsupercomputers * 5.625 / 3.75

let circuitboards =
    (10. * numcomputers + 5. * adaptiveControlUnit + 3.75 * highspeedconnector)
    / 7.5

let plastic = circuitboards * 30. + numcomputers * 40. + numsupercomputers * 52.5

scrate.ValueUnsafe <- 0.5
acurate.SetValue 0.25

highspeedconnector.map ceilf, circuitboards.map ceilf, plastic.map ceilf, numcomputers.map ceilf
(plastic / 20.).map ceilf

numcomputersCell.ValueUnsafe <- 2.
numcomputers * 2.5
numsupercomputers

//Sudoku 4x4 Solver using Cell

// The domain for a 4x4 Sudoku puzzle.
let domain = set [ 1; 2; 3; 4 ]

// The minimize function for Sudoku cells. It takes the current possibilities
// and a set of solved numbers in the group, and returns the difference.
// let sudokuMinimize (possibilities: Set<int>) (solvedInGroup: Set<int>) =
//     let newPossibilities = Set.difference possibilities solvedInGroup

//     if Set.isEmpty newPossibilities then
//         None
//     else
//         Some newPossibilities

// Helper to create/ a Sudoku cell.
// let createSudokuCell initialPossibilities =
//     new Cell<Set<int>>(initialPossibilities, minimize = sudokuMinimize, isEqual = (=))


// The minimize function for Sudoku cells. It takes the current possibilities
// and a set of constraints, and returns the intersection.
// let sudokuMinimize (possibilities: Set<int>) (constraintSet: Set<int>) =
//     let newPossibilities = Set.intersect possibilities constraintSet
//     if Set.isEmpty newPossibilities then None else Some newPossibilities

// Helper to create a Sudoku cell.
// let createSudokuCell cellName initialPossibilities =
//     new Cell<Set<int>>(initialPossibilities, minimize = sudokuMinimize, ?cellname = cellName)
let sudokuMinimize (setA: Set<int>) (setB: Set<int>) : Result<Set<int>, _> =
    let intersection = Set.intersect setA setB
    if Set.isEmpty intersection then
        Error ((sprintf "Conflict: intersection of %A and %A is empty" setA setB))
    else
        Ok intersection

// New cell creator
let createSudokuCell initialPossibilities =
    new Cell<Set<int>>(initialPossibilities, minimize = sudokuMinimize, logErrors = true)

//========================
//Step by step build up

// Testing single cell
// Create a single Sudoku cell with all possible values
// let singleCell = createSudokuCell None domain

// Initial state (all possibilities)
// printfn "Initial possibilities: %A" singleCell.Value.Value // Set [1; 2; 3; 4]

// Set the cell to a specific value
// singleCell.Value <- Some(set [ 3 ])

// After setting
// printfn "After setting: %A" singleCell.Value.Value // Set [3]

// Reset to multiple possibilities
// singleCell.Value <- Some(set [ 1; 2 ])
// printfn "Reset to: %A" singleCell.Value.Value // Set [1; 2]

//test that setting cell2 to a value restricts cell1 but that cell1 can still be set to a value that is not in cell2

//========================
// Step 2 (Revised): Testing two interacting cells with new minimize function

// A helper to create a constraint from a peer cell.
// It returns an adaptive value representing the numbers still allowed by this peer.
//let createConstraintFromPeer (peer: Cell<Set<int>>) =
    // AVal.map (fun peerStateOption ->
    //     match peerStateOption with
    //     | Some peerPossibilities when Set.count peerPossibilities = 1 ->
    //         // If the peer is solved, the constraint is "all numbers EXCEPT the peer's value".
    //         Set.difference domain peerPossibilities
    //     | _ ->
    //         // If the peer is unsolved, it places no restriction, so it allows the full domain.
    //         domain
    // ) peer.Cell // We depend on the public, option-wrapped Cell value.
    // |> AVal.map Some // The result must be aval<Set<int> option> for AddDependency
// This is the function that determines how a peer's state affects us.
let createConstraintFromPeer (peer: Cell<Set<int>>) =
    AVal.map (fun peerResult ->
        let solvedValue =
            match peerResult with
            // If peer is Ok and solved, it imposes a constraint.
            | Ok possibilities when Set.count possibilities = 1 -> possibilities
            // In ALL other cases (unsolved, error), it provides no information.
            | _ -> Set.empty

        // The constraint itself is ALWAYS an 'Ok' value.
        // It's the set of allowed possibilities.
        Ok (Set.difference domain solvedValue)
    ) peer.Cell   

printfn "\n//========================"
printfn "// Step 2 (Revised): Testing two interacting cells with Set.intersection"
 
let runTest() =
    // (Assuming the Cell class and helper functions have been updated)

    printfn "\n//========================"
    printfn "// Step 2 (Corrected): Testing two interacting cells"

    let cellA = createSudokuCell domain
    let cellB = createSudokuCell domain

    // --- Set up the dependencies ---
    let constraintFromB = createConstraintFromPeer cellB
    let constraintFromA = createConstraintFromPeer cellA

    // Self-dependency preserves state, external dependency applies constraint.
    cellA.AddDependency(cellA.Cell)
    cellA.AddDependency(constraintFromB)

    cellB.AddDependency(cellB.Cell)
    cellB.AddDependency(constraintFromA)

    //cellA.FinalResult |> AVal.force

    printfn "\nInitial state:"
    printfn "  Cell A possibilities: %A" cellA.Value.Value
    printfn "  Cell B possibilities: %A" cellB.Value.Value

    printfn "\nSetting Cell B to a fixed value (2)..."
    cellB.Value <- Some(set [2])

    printfn "After propagation:"
    printfn "  Cell B value is fixed: %A" cellB.Value.Value
    printfn "  Cell A possibilities are restricted by Cell B: %A" cellA.Value.Value

    printfn "\nNow, setting Cell A to a different fixed value (4)..."
    cellA.Value <- Some(set [4])
    // cellA.DependentCells
    // cellA.Values
    // cellB.DependentCells
    // cellB.Values
    printfn "Final state:"
    printfn "  Cell A value is fixed: %A" cellA.Value.Value
    printfn "  Cell B value remains fixed: %A" cellB.Value.Value

    printfn "\nAttempting to set Cell B to the same value as Cell A (4), creating a conflict..."
    // This transaction will cause a flurry of updates.
    // 1. cellB is set to {4}.
    // 2. cellB's update logic runs: minimize({4}, constraintFromA which is {1,2,3}) -> Conflict!
    // 3. cellB's RawCell is set to Error(...).
    // 4. Because cellB changed, constraintFromB re-evaluates. With the fix, it now correctly
    //    sees the Error and produces the full domain {1,2,3,4} as its constraint.
    // 5. cellA re-evaluates. It minimizes its own state {4} with the new constraint {1,2,3,4}.
    //    The result is {4}, so its state is correctly preserved.
    cellB.Value <- Some(set [4])

    printfn "State after conflict:"
    printfn "  Cell A value is unaffected and remains correct: %A" cellA.Value//.Value
    printfn "  Cell B has an error, so its value is None: %A" cellB.Value
    printfn "  Cell B raw cell state shows the new detailed error: %A" (AVal.force cellB.Cell)

runTest()


// The domain of possibilities is very small
let initialDomain = set [1; 2]

   // --- Create the three cells ---
// This function is PURE. It only knows how to reconcile sets of possibilities.
let simpleIntersection (left: Set<'T>) (right: Set<'T>) : Result<Set<'T>, string> =
    let intersection = Set.intersect left right
    if Set.isEmpty intersection then
        Error "Domain Collapse: Contradictory inputs led to an empty set."
    else
        Ok intersection

// The cells are now generic and "dumb". They only know how to intersect.
let cellA = new Cell<Set<int>>(initialDomain, cellname = "A", minimize = simpleIntersection)
let cellB = new Cell<Set<int>>(initialDomain, cellname = "B", minimize = simpleIntersection)
let cellC = new Cell<Set<int>>(initialDomain, cellname = "C", minimize = simpleIntersection)
 
// --- Wire up the dependencies ---
// --- The Grid Logic ---
// This is where the rules of Sudoku are encoded.

let universe = set [1; 2] // The total set of possibilities for this puzzle

// A function that transforms a peer's state into a constraint.
let createConstraintFromPeer2 (peerCell: ICell<Set<int>>) =
    AVal.map (fun result ->
        match result with
        // If the peer is solved to a single value...
        | Ok solvedSet when Set.count solvedSet = 1 ->
            // ...the constraint is "everything EXCEPT that value".
            Ok (Set.difference universe solvedSet)
        // If the peer is unsolved or in error, it imposes no constraint.
        | _ -> Ok universe
    ) peerCell.Value

// Wire up the dependencies using the transformed constraints.
// Notice we are NOT just adding cellB.Cell. We are adding a transformed version.

// A is constrained by B and C
cellA.AddDependency(createConstraintFromPeer2 cellB)
cellA.AddDependency(createConstraintFromPeer2 cellC)

// B is constrained by A and C
cellB.AddDependency(createConstraintFromPeer2 cellA)
cellB.AddDependency(createConstraintFromPeer2 cellC)

// C is constrained by A and B
cellC.AddDependency(createConstraintFromPeer2 cellA) 
cellC.AddDependency(createConstraintFromPeer2 cellB)

let runTest2() =
    // Test the setup with a simple scenario
    printfn "\n//===== Simple 2x2 Grid Test ====="

    // Initially, all cells should have the full domain
    printfn "Initial state:"
    printfn "  Cell A: %A" cellA.Value
    printfn "  Cell B: %A" cellB.Value
    printfn "  Cell C: %A" cellC.Value

    // Let's say we know A is 1
    cellA.Value <- Some (set [1])
    printfn "\nAfter setting A to {1}:"
    printfn "  Cell A: %A" cellA.Value
    printfn "  Cell B: %A" cellB.Cell  
    printfn "  Cell C: %A" cellC.Cell  

    // // Now let's try to set B to 1 as well, which should create a conflict
    // printfn "\nTrying to set B to {1} (should cause conflict):"
    // cellB.Value <- Some (set [1])
    // printfn "  Cell A: %A" cellA.Value
    // printfn "  Cell B: %A" cellB.Cell // Should be None due to conflict
    // printfn "  Cell C: %A" cellC.Cell

    printfn "\nTrying to set C to {1} (should cause conflict):"
    cellC.Value <- Some (set [1])
    printfn "  Cell A: %A" cellA.Value
    printfn "  Cell B: %A" cellB.Cell // Should be None due to conflict
    printfn "  Cell C: %A" cellC.Cell

runTest2()


// --- Create the cells for the top row and leftmost column ---
// We need 7 unique cells in total for a 4x4 grid's top row and left column.
// C00, C01, C02, C03 (Top Row)
// C10, C20, C30 (Rest of Left Column)
/// The full set of possible numbers for a 4x4 Sudoku.
let universe2 = set [1; 2; 3; 4]

let initialDomain2 = universe2

let C00 = new Cell<_>(initialDomain2, cellname="C00", minimize=simpleIntersection)
let C01 = new Cell<_>(initialDomain2, cellname="C01", minimize=simpleIntersection)
let C02 = new Cell<_>(initialDomain2, cellname="C02", minimize=simpleIntersection)
let C03 = new Cell<_>(initialDomain2, cellname="C03", minimize=simpleIntersection)

let C10 = new Cell<_>(initialDomain2, cellname="C10", minimize=simpleIntersection)
let C20 = new Cell<_>(initialDomain2, cellname="C20", minimize=simpleIntersection)
let C30 = new Cell<_>(initialDomain2, cellname="C30", minimize=simpleIntersection)
let C11 = new Cell<_>(initialDomain2, cellname="C11", minimize=simpleIntersection)
/// The full set of possible numbers for a 4x4 Sudoku.
/// Creates a final constraint for a specific cell from its group (row, col, etc.).
/// It calculates the set of values its peers have solved, and returns the
/// set of remaining available values.
let createConstraintForCell (group) (cell:Cell<_>) =
    // 1. Get the list of peers by excluding the cell itself.
    let peers = group |> List.filter (fun p -> p <> cell)

    // 2. Create a clist of avals, where each aval holds the solved value of a peer, or an empty set.
    let solvedValuesOfPeers =
        peers
        |> List.map (fun peer ->
            AVal.map (fun result ->
                match result with
                | Ok solvedSet when Set.count solvedSet = 1 -> solvedSet
                | _ -> Set.empty
            ) peer.Cell
        )
        |> clist

    // 3. Fold over the list of peer's solved sets, unioning them together.
    let nestedUnionOfPeerValues =
        AList.fold
            (AVal.map2 Set.union)
            (AVal.constant Set.empty)
            solvedValuesOfPeers

    // 4. Flatten the nested aval.
    let solvedPeerValues = AVal.bind id nestedUnionOfPeerValues

    // 5. Transform the set of "taken" values into a set of "available" values.
    //    This is the final constraint.
    AVal.map (fun takenValues ->
        Ok (Set.difference universe2 takenValues)
    ) solvedPeerValues

// --- Define the cell groups ---
let topRowCells = [C00; C01; C02; C03]
let leftColCells = [C00; C10; C20; C30]
// --- Define the top-left 2x2 box group ---
let topLeftBoxCells = [C00; C01; C10; C11] 
// --- Apply the constraints to each cell ---

// Apply row constraints
for cell in topRowCells do
    let rowConstraint = createConstraintForCell topRowCells cell
    cell.AddDependency(rowConstraint)

// Apply column constraints
for cell in leftColCells do
    let colConstraint = createConstraintForCell leftColCells cell
    cell.AddDependency(colConstraint)

// Apply box constraints
for cell in topLeftBoxCells do
    let boxConstraint = createConstraintForCell topLeftBoxCells cell
    cell.AddDependency(boxConstraint)

(*C00, C01, C02, C03
C10, C11, C12, C13
C20, C21, C22, C23
C30, C31, C32, C33*)
let printall() =
    printfn "C00: %A" C00.Value
    printfn "C01: %A" C01.Value
    printfn "C02: %A" C02.Value
    printfn "C03: %A" C03.Value
    printfn "C10: %A" C10.Value
    printfn "C20: %A" C20.Value
    printfn "C30: %A" C30.Value
    printfn "C11: %A" C11.Value

// Reset all cells for next test
let resetAll () =
    printfn "\nResetting all cells to initial state."
    for c in [C00; C01; C02; C03; C10; C20; C30; C11] do c.Value <- Some universe2
 
printfn "\n===== Simple Propagation Test ====="
printfn "Setting C00 to {1}"
C00.Value <- Some (set [1])

printall()

resetAll ()

printfn "\n===== Two-step Propagation Test ====="
printfn "Setting C00 to {1} and C01 to {2}"
C00.Value <- Some (set [1])
C01.Value <- Some (set [2])
printall()

resetAll ()

printfn "\n===== Conflict Test ====="
C00.Value <- Some (set [1])
C01.Value <- Some (set [1])
printall()

printfn "C01 raw cell: %A" (AVal.force C01.Cell)
printfn "C00 raw cell: %A" (AVal.force C00.Cell)    

// 1. Create a 2D array of cells
let size = 4
let universe3 = set [1..size]
let initialDomain3 = universe3

let cells : Cell<Set<int>>[,] =
    Array2D.init size size (fun row col ->
        new Cell<_>(initialDomain3, cellname = sprintf "C%d%d" row col, minimize = simpleIntersection)
    )

// 2. Helpers to get rows, columns, and boxes
let getRow r = [ for c in 0 .. size-1 -> cells.[r, c] ]
let getCol c = [ for r in 0 .. size-1 -> cells.[r, c] ]
let getBox boxRow boxCol =
    let boxSize = int (sqrt (float size))
    [ for dr in 0 .. boxSize-1 do
        for dc in 0 .. boxSize-1 ->
            let r = boxRow * boxSize + dr
            let c = boxCol * boxSize + dc
            cells.[r, c]
    ]

// 3. Apply constraints to every cell
for r in 0 .. size-1 do
    for c in 0 .. size-1 do
        let cell = cells.[r, c]
        // Row constraint
        cell.AddDependency(createConstraintForCell (getRow r) cell)
        // Column constraint
        cell.AddDependency(createConstraintForCell (getCol c) cell)
        // Box constraint
        let boxSize = int (sqrt (float size))
        let boxR, boxC = r / boxSize, c / boxSize
        cell.AddDependency(createConstraintForCell (getBox boxR boxC) cell)

// 4. Print helper
let printSudoku () =
    for r in 0 .. size-1 do
        for c in 0 .. size-1 do
            let v = cells.[r, c].Value
            match v with
            | Some s when Set.count s = 1 -> printf "%d " (Set.minElement s)
            | Some s -> printf "{%s} " (String.concat "," (s |> Set.toList |> List.map string))
            | None -> printf "X "
        printfn ""
// ... existing code ...

// 4. Prettier Print helper that properly handles sets
// ... existing code ...
 
let resetSudoku () =
    for r in 0 .. size-1 do
        for c in 0 .. size-1 do
            cells.[r, c].Value <- Some universe3

resetSudoku ()

// 5. Example: set a value and print 
let setcellsTest() =
    cells.[0,0].Value <- Some (set [1])
    cells.[0,1].Value <- Some (set [2])
    cells.[1,0].Value <- Some (set [3])
    cells.[2,2].Value <- Some (set [3])
setcellsTest()

printSudoku ()

let printSudoku2 (domain: Set<int>) (cells: Cell<Set<int>>[,]) =
    let size = Array2D.length1 cells
    let domainSize = Set.count domain
    let blockSize = int (sqrt (float domainSize))
    
    // Create cell width based on domain size (need space for comma-separated values)
    let maxDigits = domain |> Set.toList |> List.map (string >> String.length) |> List.max
    let maxPossibleWidth = (domainSize * maxDigits) + (domainSize - 1) // digits + commas
    let cellWidth = max 5 (maxPossibleWidth + 2) // minimum 5, or calculated width + padding
    
    // Create border strings
    let cellBorder = String.replicate cellWidth "─"
    let topBorder = "┌" + String.replicate (blockSize - 1) (cellBorder + "┬") + cellBorder + String.replicate (blockSize - 1) ("┬" + String.replicate (blockSize - 1) (cellBorder + "┬") + cellBorder) + "┐"
    let midBorder = "├" + String.replicate (blockSize - 1) (cellBorder + "┼") + cellBorder + String.replicate (blockSize - 1) ("┼" + String.replicate (blockSize - 1) (cellBorder + "┼") + cellBorder) + "┤"
    let bottomBorder = "└" + String.replicate (blockSize - 1) (cellBorder + "┴") + cellBorder + String.replicate (blockSize - 1) ("┴" + String.replicate (blockSize - 1) (cellBorder + "┴") + cellBorder) + "┘"
    
    printfn "%s" topBorder
    for r in 0 .. size-1 do
        printf "│"
        for c in 0 .. size-1 do
            let v = cells.[r, c].Value
            let cellContent = 
                match v with
                | Some s when Set.count s = 1 -> 
                    let value = Set.minElement s |> string
                    value.PadLeft((cellWidth-2)/2 + value.Length).PadRight(cellWidth-2)
                | Some s -> 
                    let vals = s |> Set.toList |> List.map string |> String.concat ","
                    vals.PadLeft((cellWidth-2)/2 + vals.Length).PadRight(cellWidth-2)
                | None -> 
                    "ERR".PadLeft((cellWidth-2)/2 + 3).PadRight(cellWidth-2)
            printf " %s │" cellContent
            // Add block separator
            if (c + 1) % blockSize = 0 && c < size - 1 then
                printf "│"
        printfn ""
        // Add horizontal block separators
        if (r + 1) % blockSize = 0 && r < size - 1 then
            printfn "%s" midBorder
    printfn "%s" bottomBorder  

printSudoku2 universe3 cells

// let printSudoku2 (cells: Cell<Set<int>>[,]) =
//     let size = Array2D.length1 cells
//     printfn "┌─────┬─────┬─────┬─────┐"
//     for r in 0 .. size-1 do
//         printf "│"
//         for c in 0 .. size-1 do
//             let v = cells.[r, c].Value
//             let cellContent = 
//                 match v with
//                 | Some s when Set.count s = 1 -> 
//                     sprintf "  %d  " (Set.minElement s)
//                 | Some s -> 
//                     let vals = s |> Set.toList |> List.map string |> String.concat ","
//                     let padded = vals.PadLeft(3) 
//                     sprintf " %s " padded
//                 | None -> 
//                     " ERR "
//             printf "%s│" cellContent
//         printfn ""
//         if r = 1 then
//             printfn "├─────┼─────┼─────┼─────┤"
//         elif r < size-1 then
//             printfn "├─────┼─────┼─────┼─────┤"
//     printfn "└─────┴─────┴─────┴─────┘"

  