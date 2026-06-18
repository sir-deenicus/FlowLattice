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
type AdaptiveExpression<'a>(adaptiveVal : aval<'a>) = 
    //constructor from a float
    member this.Value = AVal.force adaptiveVal 
    member this.State = adaptiveVal 
    member this.map f = 
        AdaptiveExpression(AVal.map f adaptiveVal)
 
    override this.ToString (): string =
        this.Value.ToString()
        
    static member inline (+) (a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State b.State)
    static member inline (+) (a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (+) a.State b.State)
    static member inline (+) (a: aval<_>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) (AVal.map Some a) b.State)
    static member inline (+) (a: AdaptiveExpression<Option<_>>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State (AVal.map Some b))
    static member inline (+) (a: AdaptiveExpression<_>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (+) a.State b)
    static member inline (+) (a: aval<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (+) a b.State)
    static member inline (+) (a: AdaptiveExpression<Option<_>>, b: float) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (+)) a.State (AVal.constant (Some b)))
    static member inline (+) (a: AdaptiveExpression<_>, b: float) = 
        AdaptiveExpression(AVal.map2 (+) a.State (AVal.constant b))
    static member inline (+) (a: float, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (+) (AVal.constant a) b.State)

    static member inline (-) (a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) a.State b.State)
    static member inline (-) (a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (-) a.State b.State)
    static member inline (-) (a: aval<_>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) (AVal.map Some a) b.State)
    static member inline (-) (a: AdaptiveExpression<Option<_>>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (-)) a.State (AVal.map Some b))
    static member inline (-) (a: AdaptiveExpression<_>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (-) a.State b)
    static member inline (-) (a: aval<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (-) a b.State)
    static member inline (-) (a: AdaptiveExpression<_>, b: float) = 
        AdaptiveExpression(AVal.map2 (-) a.State (AVal.constant b))
    static member inline (-) (a: float, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (-) (AVal.constant a) b.State)

    static member inline (*) (a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State b.State)
    static member inline (*) (a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (*) a.State b.State)
    static member inline (*) (a: aval<_>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) (AVal.map Some a) b.State)
    static member inline (*) (a: AdaptiveExpression<Option<_>>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State (AVal.map Some b))     
    static member inline (*) (a: AdaptiveExpression<_>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (*) a.State b)
    static member inline (*) (a: aval<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (*) a b.State) 
    static member inline (*) (a: float, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) (AVal.constant (Some a)) b.State)
    static member inline (*) (a: AdaptiveExpression<Option<_>>, b: float) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (*)) a.State (AVal.constant (Some b)))
    static member inline (*) (a: AdaptiveExpression<_>, b: float) = 
        AdaptiveExpression(AVal.map2 (*) a.State (AVal.constant b))
    static member inline (*) (a: float, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (*) (AVal.constant a) b.State)

    static member inline (/) (a: AdaptiveExpression<Option<_>>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State b.State)
    static member inline (/) (a: AdaptiveExpression<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (/) a.State b.State)
    static member inline (/) (a: aval<_>, b: AdaptiveExpression<Option<_>>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) (AVal.map Some a) b.State)
    static member inline (/) (a: AdaptiveExpression<Option<_>>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State (AVal.map Some b))
    static member inline (/) (a: AdaptiveExpression<_>, b: aval<_>) = 
        AdaptiveExpression(AVal.map2 (/) a.State b)
    static member inline (/) (a: aval<_>, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (/) a b.State)
    static member inline (/) (a: AdaptiveExpression<Option<_>>, b: float) = 
        AdaptiveExpression(AVal.map2 (Option.map2 (/)) a.State (AVal.constant (Some b)))
    static member inline (/) (a: AdaptiveExpression<_>, b: float) = 
        AdaptiveExpression(AVal.map2 (/) a.State (AVal.constant b))
    static member inline (/) (a: float, b: AdaptiveExpression<_>) = 
        AdaptiveExpression(AVal.map2 (/) (AVal.constant a) b.State)


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
    abstract member Cell : cval<Result<'T, string>>
    abstract member CellValue : 'T option 
    abstract member DependentCells : ResizeArray<aval<'T option>>
    abstract member AddDependency : aval<'T option> -> unit
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
type Cell<'T when 'T : equality>(initial, ?coarsen, ?isEqual: 'T -> 'T -> bool, ?cellname, ?minimize, ?circuitFailureThreshold) =
    let cell = cval (Ok initial)
    let dependentCells = ResizeArray<aval<'T option>>() 
    let disposables = ResizeArray<IDisposable>()
    
    let cellname = match cellname with | Some n -> n + " " | _ -> ""
    let coarsen = match coarsen with | Some f -> f | None -> id
    let isEqual = match isEqual with | Some f -> f | None -> (fun a b -> coarsen a = coarsen b)

    //minimize is a function that takes two values and returns the minimized value (eg imagine sets , minimize could be the intersection of the two sets)
    let minimize = 
        match minimize with 
        | Some f -> f 
        | None -> (fun a b -> if isEqual a b then Some a else None) 

    let rec runMinimize acc = function
        | [] -> Result.Ok acc 
        | x::xs -> 
            match minimize acc x with
            | None -> Result.Error $"Minimize failed on {cellname}cell"
            | Some v -> runMinimize v xs   
    
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

    member this.Cell = AVal.map (Result.toOption) cell

    member this.RawCell = cell

    member this.CellUnsafe = AVal.map (Result.toOption >> Option.get) cell

    member this.UVal
        with get () = match AVal.force cell with | Ok v -> v | _ -> failwith $"Error: {cellname}cell has an error value."
        and set v = transact (fun () -> cell.Value <- Ok v)

    member this.Value
        with get () = match AVal.force cell with | Ok v -> Some v | _ -> None
        and set v = transact (fun () -> 
            match v with 
            | None -> ()
            | Some v -> cell.Value <- Ok v)

    member this.ValueUnsafe
        with get () = match AVal.force cell with | Ok v -> v | _ -> failwith $"Error: {cellname}cell has an error value."
        and set v = transact (fun () -> cell.Value <- Ok v)

    member this.SetValue v = this.Value <- Some v

    member this.Values = 
        [for dcell in dependentCells -> AVal.force dcell]

    member this.DependentCells = dependentCells

    member this.AddDependency(adaptiveval:aval<'U>, f) = this.AddDependency(AVal.map (f >> Some) adaptiveval)

    member this.AddDependency(adaptiveval:aval<Result<_,_>>, f) =
        this.AddDependency(AVal.map (Result.toOption >> Option.map f) adaptiveval)

    member this.AddDependency (adaptivevalue: aval<'T>) = this.AddDependency(AVal.map Some adaptivevalue) 
    
    member this.AddDependency (adaptivevalue: aval<'T option>, f) =
        this.AddDependency(AVal.map (Option.map f) adaptivevalue)

    member this.AddDependency (adaptivevalue: aval<'T option>) = 
        dependentCells.Add adaptivevalue
        let disposable = adaptivevalue.AddCallback(fun _ ->  
            if checkCircuitBreaker() then
                let candidates = [for dcell in dependentCells do match AVal.force dcell with | Some v -> yield v | None -> ()] 
                match candidates with 
                | h::t ->
                    match (runMinimize h t) with
                    | Result.Ok v ->  
                        printfn $"Setting {cellname}cell value to {v}" 
                        transact (fun () -> cell.Value <- Ok v)
                    | Result.Error e ->
                        printfn $"Error: {e}"
                        transact (fun () -> cell.Value <- Error e) 
                | [] -> 
                    match dependentCells.Count with
                    | 0 -> ()
                    | _ -> 
                        printfn $"Unexpected empty list of candidates for {cellname}cell" 
                        transact (fun () -> cell.Value <- Error $"Unexpected empty list of candidates for {cellname}cell")
            else 
                transact (fun () -> cell.Value <- Error $"Circuit breaker triggered for {cellname}cell due to rapid oscillations.")
        )

        disposables.Add disposable

    interface ICell<'T> with
        member this.Cell = cell
        member this.CellValue = this.Value
        member this.DependentCells = dependentCells
        member this.AddDependency(a) = this.AddDependency(a)

    interface IDisposable with
        member this.Dispose() =
            for d in disposables do
                d.Dispose()
 
//Example usage
let cellFahrenheit = new Cell<float>(32., isEqual = fun a b -> round 5 a = round 5 b)
let cellCelsius = new Cell<_>(0., coarsen = round 5)

cellFahrenheit.AddDependency (cellCelsius.Cell, fun c -> (c * 9./5.) + 32.)

cellCelsius.AddDependency (cellFahrenheit.Cell, fun f -> (f - 32.) * 5./9.)

cellFahrenheit.Value 

cellCelsius.ValueUnsafe <- 100

cellFahrenheit.SetValue 98

cellCelsius.Value


let scrate = new Cell<_>(1., round 5)
let acurate = new Cell<_>(1., round 5)

let numsupercomputers = AdaptiveExpression(scrate.CellUnsafe)
let adaptiveControlUnit = AdaptiveExpression(acurate.CellUnsafe)

let numcomputers = (numsupercomputers * 7.5 + adaptiveControlUnit * 2. + 0.5) / 2.5 
let numcomputersCell = new Cell<_>(numcomputers.Value, round 5)

numcomputersCell.AddDependency(numcomputers.State)

scrate.AddDependency(AVal.map (fun nc -> -adaptiveControlUnit.Value * 4./15. - 1./15. + nc/3.) numcomputersCell.CellUnsafe)

let highspeedconnector = numsupercomputers * 5.625 / 3.75
let circuitboards = (10. * numcomputers + 5. * adaptiveControlUnit + 3.75 * highspeedconnector) / 7.5
let plastic = circuitboards * 30. + numcomputers * 40. + numsupercomputers * 52.5

scrate.ValueUnsafe <- 0.5
acurate.SetValue 0.25

highspeedconnector.map ceilf, circuitboards.map ceilf, plastic.map ceilf, numcomputers.map ceilf
(plastic / 20.).map ceilf

numcomputersCell.UVal <- 2.
numcomputers * 2.5
numsupercomputers

