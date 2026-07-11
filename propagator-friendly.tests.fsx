#load "propagator-friendly.fsx"

open System.Diagnostics
open System.Collections.Generic
open ``Propagator-surface-vocab``

// ---- Differential harness (TEST HARNESS, not shipped surface — docs §7.8) --------
// The whole two-faces claim in one call: same model, both faces, equal solutions. A consumer
// picks a face and never calls this; it lives here with the tests, where paying for both engines
// per model is a CI cost, not a hot-path cost.

module Differential =

    /// Lower a model to BOTH faces and solve each. Portability failures on either face are returned
    /// as data; when both lower, the pair comes back for the caller to diff.
    let solveBoth (model: Model<'a> when 'a : comparison)
                  : Result<Solution<'a> option * Solution<'a> option, UnsupportedConstruct list> =
        match General.lower model, Optimized.lower model with
        | Ok fnet, Ok onet -> Ok (General.solve fnet, Optimized.solve onet)
        | Error e, Ok _ -> Error e
        | Ok _, Error e -> Error e
        | Error e1, Error e2 -> Error (List.append e1 e2)

// ---- The two proof slices (now runnable) --------------------------------

module private Slices =

    /// Slice 1 — Celsius/Fahrenheit: general-only dataflow over the rich interval lattice.
    /// Lowers on the general face (settles to F ~ [33.8, 33.8]); `Optimized.lower` returns
    /// [RichLatticeOnOptimized; DataflowOnOptimized] — general-only, visible pre-engine.
    let celsiusFahrenheit () : Model<Interval> =
        let c = Cell.create "celsius"
        let f = Cell.create "fahrenheit"
        let dom = Domain.lattice Interval.entire Interval.meet (fun iv -> iv = Empty)
        let cToF (interval: Interval) = interval * 9.0 / 5.0 + 32.0
        let fToC (interval: Interval) = (interval - 32.0) * 5.0 / 9.0
        let convert =
            Constraint.dataflow [ c; f ] (fun vs ->
                match vs with
                | [ cv; fv ] -> [ fToC fv; cToF cv ]   // each cell's freshly-derived value; engine meets it in
                | _ -> vs)
        { domain = dom; cells = [ c; f ]; constraints = [ convert ]; givens = [ c, Interval.pt 1.0 ] }

    let private sudoku4WithGivens
        (encodeUnit: Cell<int> list -> Constraint<int> list)
        (givens: ((int * int) * int) list)
        : Model<int> =
        let cells = List.init 16 (fun i -> (Cell.create (sprintf "r%dc%d" (i / 4) (i % 4)) : Cell<int>))
        let at r c = List.item (r * 4 + c) cells
        let rows  = [ for r in 0 .. 3 -> [ for c in 0 .. 3 -> at r c ] ]
        let colsC = [ for c in 0 .. 3 -> [ for r in 0 .. 3 -> at r c ] ]
        let boxes = [ for br in 0 .. 1 do for bc in 0 .. 1 -> [ for dr in 0 .. 1 do for dc in 0 .. 1 -> at (2*br+dr) (2*bc+dc) ] ]
        let constraints = rows @ colsC @ boxes |> List.collect encodeUnit
        let authoredGivens = givens |> List.map (fun ((r, c), v) -> at r c, v)
        { domain = Domain.finite [1..4]
          cells = cells
          constraints = constraints
          givens = authoredGivens }

    let private globalAllDifferent group =
        [ Constraint.relation group (fun vs -> List.length (List.distinct vs) = List.length vs) ]

    let private pairwiseNotEqual group =
        [ for i in 0 .. List.length group - 2 do
              for j in i + 1 .. List.length group - 1 ->
                  Constraint.relation [ List.item i group; List.item j group ] (function
                      | [ a; b ] -> a <> b
                      | _ -> false) ]

    /// Slice 2 — 4x4 Sudoku: portable finite CSP. Twelve all-different relations + eight givens,
    /// the same instance the other engines solve; lowers through BOTH faces.
    let sudoku4 () : Model<int> =
        sudoku4WithGivens globalAllDifferent
            [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]

    let sudoku4PairwiseWithGivens givens : Model<int> =
        sudoku4WithGivens pairwiseNotEqual givens

    /// Slice 3 — sparse 4x4 Sudoku through pairwise not-equal relations, plus one nonlocal
    /// symmetry breaker. The five ordinary clues have two completions; excluding the lexicographically
    /// first rectangle orientation makes the authored model unique without making arc consistency complete,
    /// and makes the current value-order driver exercise a failed guess plus retract before succeeding.
    let sudoku4Sparse () : Model<int> =
        let model =
            sudoku4PairwiseWithGivens
                [ (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,2),2 ]
        let at r c = List.item (r * 4 + c) model.cells
        let excludeFirstRectangle =
            Constraint.relation [ at 0 0; at 0 2; at 1 0; at 1 2 ] (fun values -> values <> [ 1; 3; 3; 1 ])
        { model with constraints = model.constraints @ [ excludeFirstRectangle ] }

    let dataflowOnFiniteGuard () : Model<int> =
        let a = Cell.create "finite-a"
        let b = Cell.create "finite-b"
        { domain = Domain.finite [1; 2]
          cells = [ a; b ]
          constraints = [ Constraint.dataflow [ a; b ] id ]
          givens = [] }

    let relationOnLatticeGuard () : Model<Interval> =
        let a = Cell.create "rich-a"
        let b = Cell.create "rich-b"
        let dom = Domain.lattice Interval.entire Interval.meet (fun iv -> iv = Empty)
        { domain = dom
          cells = [ a; b ]
          constraints = [ Constraint.relation [ a; b ] (fun _ -> true) ]
          givens = [] }

    let wideDomainGuard count : Model<int> =
        let c = Cell.create "wide-domain-cell"
        { domain = Domain.finite [0 .. count - 1]
          cells = [ c ]
          constraints = [ Constraint.relation [ c ] (function [ value ] -> value = count - 1 | _ -> false) ]
          givens = [] }

    let premiseWidthGuard () : Model<int> =
        let cells = List.init 65 (fun i -> (Cell.create (sprintf "pwidth-%d" i) : Cell<int>))
        { domain = Domain.finite [1; 2]; cells = cells; constraints = []; givens = [] }

    let authoredOrderGuard () : Model<int> =
        let cell = Cell.create "authored-order"
        { domain = Domain.finite [2; 1]; cells = [ cell ]; constraints = []; givens = [] }


// ---- Differential test oracles ------------------------------------------

module private TestGac =

    // Intentionally copied test machinery: core Gac remains private.
    let narrow (allows: 'a list -> bool) (candidates: 'a list list) : 'a list list =
        let cand = List.toArray candidates
        let n = cand.Length
        let supported = Array.init n (fun _ -> HashSet<'a>())
        let rec go i acc =
            if i = n then
                let tuple = List.rev acc
                if allows tuple then
                    tuple |> List.iteri (fun j value -> supported.[j].Add value |> ignore)
            else
                for value in cand.[i] do
                    go (i + 1) (value :: acc)
        go 0 []
        [ for j in 0 .. n - 1 ->
              [ for value in cand.[j] do
                    if supported.[j].Contains value then yield value ] ]

module private Harness =

    /// Compute the portable finite GAC fixpoint before search. This stays in the harness so the
    /// proof that a slice needs a guess does not add an observation field to the shipped net types.
    let initialFixpoint (model: Model<'a> when 'a : comparison) : Map<Cell<'a>, 'a list> =
        let values =
            match model.domain with
            | Finite values -> values
            | Lattice _ -> invalidArg "model" "initialFixpoint requires a finite domain"
        let givens = Map.ofList model.givens
        let mutable candidates =
            model.cells
            |> List.map (fun cell -> cell, (Map.tryFind cell givens |> Option.map List.singleton |> Option.defaultValue values))
            |> Map.ofList
        let mutable changed = true
        while changed do
            changed <- false
            for constraintBox in model.constraints do
                match constraintBox with
                | Relation box ->
                    for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                        let cells = [ for index in offset .. offset + box.arity - 1 -> box.scopes.[index] ]
                        let before = cells |> List.map (fun cell -> Map.find cell candidates)
                        let after = TestGac.narrow box.allows before
                        if after <> before then
                            changed <- true
                            for cell, narrowed in List.zip cells after do
                                candidates <- Map.add cell narrowed candidates
                | Dataflow _ ->
                    invalidArg "model" "initialFixpoint requires portable finite relations"
        candidates

    /// Exhaustively enumerate assignments, pruning only when a fully assigned authored relation
    /// rejects its tuple. This is deliberately independent of Gac.narrow and both lowering engines.
    let bruteForceSolutions (model: Model<'a> when 'a : comparison) : Solution<'a> list =
        let values =
            match model.domain with
            | Finite values -> values
            | Lattice _ -> invalidArg "model" "bruteForceSolutions requires a finite domain"
        let givens = Map.ofList model.givens
        let relations =
            model.constraints
            |> List.map (function
                | Relation box -> box
                | Dataflow _ -> invalidArg "model" "bruteForceSolutions requires portable finite relations")
        let relationAllows (assignment: Solution<'a>) (box: RelationBox<'a>) =
            [ 0 .. box.arity .. box.scopes.Length - 1 ]
            |> List.forall (fun offset ->
                let assigned =
                    [ for index in offset .. offset + box.arity - 1 ->
                          Map.tryFind box.scopes.[index] assignment ]
                if assigned |> List.forall Option.isSome then
                    assigned |> List.map Option.get |> box.allows
                else true)
        let rec enumerate remaining (assignment: Solution<'a>) =
            seq {
                match remaining with
                | [] -> yield assignment
                | cell :: rest ->
                    let choices = Map.tryFind cell givens |> Option.map List.singleton |> Option.defaultValue values
                    for value in choices do
                        let next = Map.add cell value assignment
                        if relations |> List.forall (relationAllows next) then
                            yield! enumerate rest next
            }
        enumerate model.cells Map.empty |> Seq.toList


// ---- Run: the differential proof + the capability proof -----------------

let private gridOf (model: Model<int>) (sol: Solution<int>) =
    [ for r in 0 .. 3 -> [ for c in 0 .. 3 -> Map.find (List.item (r * 4 + c) model.cells) sol ] ]

let private showGrid (label: string) (g: int list list) =
    printfn "  %s" label
    for row in g do printfn "     %A" row

let expected = [ [1;2;3;4]; [3;4;1;2]; [2;1;4;3]; [4;3;2;1] ]

type private Probe =
    | Open
    | Point of int
    | Conflict

let private probeMeet left right =
    match left, right with
    | Open, value | value, Open -> value
    | Conflict, _ | _, Conflict -> Conflict
    | Point a, Point b when a = b -> Point a
    | Point _, Point _ -> Conflict

let private benchmark warmup trials iterations action =
    for _ in 1 .. warmup do action ()
    let samples =
        [ for _ in 1 .. trials do
              System.GC.Collect()
              System.GC.WaitForPendingFinalizers()
              let timer = Stopwatch.StartNew()
              for _ in 1 .. iterations do action ()
              timer.Stop()
              yield timer.Elapsed.TotalMilliseconds * 1000.0 / float iterations ]
    List.min samples, List.average samples

module private FriendlyRegression =

    open ``Propagator-friendly``

    let private require condition message =
        if not condition then failwith message

    let private showScalar = function
        | Top -> "Top"
        | Bot -> "BOT (contradiction)"
        | Val (value: float) -> sprintf "%.17g" value

    let private showInterval = function
        | Empty -> "BOT (contradiction)"
        | Iv(low, high) -> sprintf "[%.17g, %.17g]" low high

    let private showRange = function
        | Empty -> "BOT (no consistent height)"
        | Iv(low, high) -> sprintf "[%.6g, %.6g]" low high

    let private demoCelsiusScalar () =
        let net = Domain.scalar<float> ()
        let celsius = net.Cell "C"
        let fahrenheit = net.Cell "F"
        net.Convert(
            celsius,
            fahrenheit,
            (fun value -> value * 9.0 / 5.0 + 32.0),
            (fun value -> (value - 32.0) * 5.0 / 9.0))
        net.Given(celsius, 1.0)
        net, celsius, fahrenheit

    let private demoCelsiusScalarAffine () =
        let net = Domain.scalar<float> ()
        let celsius = net.Cell "C"
        let fahrenheit = net.Cell "F"
        let transform : Transform.Affine = { k = 9.0 / 5.0; b = 32.0 }
        net.Convert(celsius, fahrenheit, transform.Apply, transform.Inverse.Apply)
        net.Given(celsius, 1.0)
        net, celsius, fahrenheit

    let private demoCelsiusInterval () =
        network (Domain.interval ()) {
            let! celsius = Ops.cell "C"
            let! fahrenheit = Ops.cell "F"
            do! Ops.convert celsius fahrenheit
                    (fun value -> value * 9.0 / 5.0 + 32.0)
                    (fun value -> (value - 32.0) * 5.0 / 9.0)
            do! Ops.given celsius (Interval.pt 1.0)
            return! fun net -> net.Value celsius, net.Value fahrenheit
        }

    let private verifyFixedPointSurface () =
        let inferredHundredths = FixedPoint(12.50m)
        require (inferredHundredths.Quantum = 0.01m) "core fixed-point scale inference regression"
        let mismatchedQuantumRejected =
            let other = FixedPoint(1.2m)
            [ (fun () -> inferredHundredths + other)
              (fun () -> inferredHundredths - other)
              (fun () -> inferredHundredths * other)
              (fun () -> inferredHundredths / other) ]
            |> List.forall (fun operation ->
                try
                    operation () |> ignore
                    false
                with :? System.ArgumentException ->
                    true)
        require
            mismatchedQuantumRejected
            "core fixed-point mismatched-quantum rejection regression"
        require
            ((inferredHundredths.WithQuantum(0.1m) + FixedPoint(1.2m)).Quantum = 0.1m)
            "core fixed-point explicit quantum reconciliation regression"
        let operatorInterop =
            let value = FixedPoint(2.00m)
            [ value + 3m; 3m + value
              value - 3m; 3m - value
              value * 3m; 3m * value
              value / 3m; 3m / value ]
        require
            (operatorInterop |> List.forall (fun value -> not value.IsBottom && value.Quantum = 0.01m))
            "core fixed-point decimal operator interop regression"

        let net = Domain.fixedPoint 0.1m
        let celsius = net.Cell "fixed-C"
        let fahrenheit = net.Cell "fixed-F"
        net.Convert(
            celsius,
            fahrenheit,
            (fun value -> value * 9m / 5m + 32m),
            (fun value -> (value - 32m) * 5m / 9m))
        net.Assume("reading", celsius, FixedPoint(1.0m))
        require
            ((net.Value celsius).TryPoint = Some 1m
             && (net.Value fahrenheit).TryPoint = Some 33.8m)
            "core fixed-point operator-authored propagation regression"
        net.Assume("conflict", fahrenheit, FixedPoint(100.0m))
        require
            ((net.Value fahrenheit).IsBottom
             && net.ShowSupport(net.Support fahrenheit) = "{reading, conflict}")
            "core fixed-point genuine-conflict regression"
        net.Retract "conflict"
        require
            ((net.Value fahrenheit).TryPoint = Some 33.8m
             && net.ShowSupport(net.Support fahrenheit) = "{reading}")
            "core fixed-point retraction regression"

        let sweep count assertCelsius =
            [ 0 .. count ]
            |> List.forall (fun tick ->
                let expected = decimal tick / 10m
                let sweepNet = Domain.fixedPoint 0.1m
                let sweepC = sweepNet.Cell "sweep-C"
                let sweepF = sweepNet.Cell "sweep-F"
                sweepNet.Convert(
                    sweepC,
                    sweepF,
                    (fun value -> value * 9m / 5m + 32m),
                    (fun value -> (value - 32m) * 5m / 9m))
                let asserted = if assertCelsius then sweepC else sweepF
                sweepNet.Given(asserted, FixedPoint(expected, 0.1m))
                not (sweepNet.Value sweepC).IsBottom
                && not (sweepNet.Value sweepF).IsBottom
                && (sweepNet.Value asserted).TryPoint = Some expected)
        require (sweep 2000 true) "core fixed-point Celsius sweep regression"
        require (sweep 1800 false) "core fixed-point Fahrenheit sweep regression"
        net.Value celsius, net.Value fahrenheit

    let private g = Interval.pt 9.8
    let private half = Interval.pt 0.5
    let private two = Interval.pt 2.0
    let private heightFromFall time = half * g * time * time
    let private fallFromHeight height = Interval.sqrt (two * height / g)
    let private heightFromShadow personHeight personShadow buildingShadow =
        personHeight * buildingShadow / personShadow

    let private demoBarometer () =
        let net = Domain.interval ()
        let time = net.Cell "t"
        let height = net.Cell "h"
        let personHeight = net.Cell "bh"
        let personShadow = net.Cell "bShadow"
        let buildingShadow = net.Cell "bldShadow"
        net.Convert(time, height, heightFromFall, fallFromHeight)
        net.Combine([ personHeight; personShadow; buildingShadow ], height, function
            | [ person; personCast; buildingCast ] -> heightFromShadow person personCast buildingCast
            | _ -> Interval.entire)
        let report label =
            printfn "  %s" label
            printfn "      height    = %-28s support %s"
                (showRange (net.Value height)) (net.ShowSupport (net.Support height))
            printfn "      fall-time = %-28s support %s"
                (showRange (net.Value time)) (net.ShowSupport (net.Support time))
        net.Assume("stopwatch", time, Iv(3.0, 3.2))
        report "after stopwatch  t = [3.0, 3.2] s:"
        require (net.Value height <> Empty) "barometer stopwatch stage regression"
        net.Assume("shadows", personHeight, Iv(0.30, 0.30))
        net.Assume("shadows", personShadow, Iv(0.30, 0.32))
        net.Assume("shadows", buildingShadow, Iv(45.0, 48.0))
        report "after shadows added:"
        require (net.Value height <> Empty && net.Value time <> Empty) "barometer shadow stage regression"
        net.Assume("super", height, Iv(49.0, 49.0))
        report "after superintendent says h = 49 m:"
        require (net.Value height = Empty && net.Value time <> Empty) "barometer partial contradiction regression"
        require
            (net.ShowSupport(net.Support height) = "{stopwatch, shadows, super}"
             && net.ShowSupport(net.Support time) = "{stopwatch, shadows}")
            "barometer contradictory provenance regression"
        net.Retract "shadows"
        report "after retracting the shadow measurement:"
        match net.Value height, net.Value time with
        | Iv(49.0, 49.0), Iv(_, _)
            when net.ShowSupport(net.Support height) = "{stopwatch, super}"
                 && net.ShowSupport(net.Support time) = "{stopwatch, super}" -> ()
        | state -> failwithf "barometer retraction regression: %A" state

    let private givens =
        [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]

    let private unitCoords : (int * int) list list =
        [ for row in 0..3 -> [ for column in 0..3 -> row, column ] ]
        @ [ for column in 0..3 -> [ for row in 0..3 -> row, column ] ]
        @ [ for boxRow in 0..1 do
                for boxColumn in 0..1 ->
                    [ for row in 0..1 do
                          for column in 0..1 -> 2 * boxRow + row, 2 * boxColumn + column ] ]

    let private demoSudoku () =
        let net = Domain.finite [1..4]
        let grid = Array2D.init 4 4 (fun row column -> net.Cell (sprintf "r%dc%d" row column))
        let at coordinates = [ for row, column in coordinates -> grid.[row, column] ]
        let allDifferent values = List.length values = List.length (List.distinct values)
        for unit in unitCoords do
            net.Constrain(Constraint.relation (at unit) allDifferent)
        givens
        |> List.iter (fun ((row, column), value) -> net.Given(grid.[row, column], value))
        let solved = net.Solve ()
        let gridSnapshot =
            solved
            |> Option.map (fun solution ->
                [| for row in 0..3 ->
                       [| for column in 0..3 -> Map.find grid.[row, column] solution |] |])
        solved, gridSnapshot

    let private verifyFiniteSurface () =
        let net = Domain.finite [2; 1]
        let cell = net.Cell "finite"
        net.Constrain(
            Constraint.relation [cell] (function
                | [ value: int ] -> value = 1 || value = 2
                | _ -> false))
        let startsOpen = net.Value cell = Set.ofList [2; 1]
        net.Restrict(cell, Set.ofList [2; 1])
        net.Restrict("allowed", cell, Set.singleton 1)
        let restrictionNarrowed =
            net.Value cell = Set.singleton 1
            && net.ShowSupport(net.Support cell) = "{allowed}"
        net.Retract "allowed"
        let restrictionRetracted = net.Value cell = Set.ofList [2; 1]
        net.Assume("choice", cell, 1)
        let assumptionNarrowed =
            net.Value cell = Set.singleton 1
            && net.ShowSupport(net.Support cell) = "{choice}"
        net.Retract "choice"
        let assumptionRetracted = net.Value cell = Set.ofList [2; 1]
        let authoredOrder = net.Solve () |> Option.map (Map.find cell) = Some 2

        let givenNet = Domain.finite [2; 1]
        let givenCell = givenNet.Cell "given"
        givenNet.Given(givenCell, 1)
        let givenProvenance =
            givenNet.Value givenCell = Set.singleton 1
            && givenNet.ShowSupport(givenNet.Support givenCell) = "{given(given)}"

        let restrictedNet = Domain.finite [2; 1]
        let restrictedCell = restrictedNet.Cell "restricted"
        restrictedNet.Restrict(restrictedCell, Set.singleton 1)
        let restrictionProvenance =
            restrictedNet.Value restrictedCell = Set.singleton 1
            && restrictedNet.ShowSupport(restrictedNet.Support restrictedCell) = "{restrict(restricted)}"

        startsOpen
        && restrictionNarrowed
        && restrictionRetracted
        && assumptionNarrowed
        && assumptionRetracted
        && authoredOrder
        && givenProvenance
        && restrictionProvenance

    let private verifyDialectParity () =
        let methodNet = Domain.finite [3; 2; 1]
        let methodCell = methodNet.Cell "choice"
        methodNet.Restrict("allowed", methodCell, Set.ofList [1; 2])
        methodNet.Assume("choice", methodCell, 2)
        let methodBefore = methodNet.Value methodCell
        let methodSupport = methodNet.ShowSupport(methodNet.Support methodCell)
        methodNet.Retract "choice"
        let methodAfter = methodNet.Value methodCell
        let methodSolution = methodNet.Solve () |> Option.map (Map.find methodCell)

        let ceResult =
            network (Domain.finite [3; 2; 1]) {
                let! cell = Ops.cell "choice"
                do! Ops.restrictNamed "allowed" cell (Set.ofList [1; 2])
                do! Ops.assume "choice" cell 2
                let! before = Ops.read cell
                let! support = fun net -> net.ShowSupport(net.Support cell)
                do! Ops.retract "choice"
                let! after = Ops.read cell
                let! solved = Ops.solve
                return before, support, after, solved |> Option.map (Map.find cell)
            }

        (methodBefore, methodSupport, methodAfter, methodSolution) = ceResult

    let run () =
        require (verifyFiniteSurface ()) "friendly finite value/view/restriction/provenance regression"
        require (verifyDialectParity ()) "friendly method/CE dialect parity regression"
        printfn "== friendly facade regressions =="
        let raw, rawC, rawF = demoCelsiusScalar ()
        let rawCValue, rawFValue = raw.Value rawC, raw.Value rawF
        require (rawCValue = Bot) "raw scalar must preserve the local C contradiction"
        require (match rawFValue with Val _ -> true | _ -> false) "raw scalar must preserve unaffected F"
        printfn "  raw scalar: C = %s, F = %s" (showScalar rawCValue) (showScalar rawFValue)
        let affine, affineC, affineF = demoCelsiusScalarAffine ()
        let affineCValue, affineFValue = affine.Value affineC, affine.Value affineF
        require (affineCValue = Val 1.0) "affine scalar C regression"
        printfn "  affine scalar: C = %s, F = %s" (showScalar affineCValue) (showScalar affineFValue)
        let intervalC, intervalF = demoCelsiusInterval ()
        require (intervalC <> Empty && intervalF <> Empty) "interval conversion regression"
        printfn "  interval: C = %s, F = %s" (showInterval intervalC) (showInterval intervalF)
        let fixedC, fixedF = verifyFixedPointSurface ()
        printfn "  fixed point: C = %O, F = %O" fixedC fixedF
        demoBarometer ()
        let solved, grid = demoSudoku ()
        let expected = [| [|1;2;3;4|]; [|3;4;1;2|]; [|2;1;4;3|]; [|4;3;2;1|] |]
        require (grid = Some expected) "friendly finite Sudoku snapshot regression"
        printfn "  friendly finite Sudoku solved: %b" (Option.isSome solved)
        printfn ""
        true

module private FriendlyFiniteUx =

    open ``Propagator-friendly``

    let run () =
        let net = Domain.finite [ 1; 2; 3 ]
        let a = net.Cell "a"
        let b = net.Cell "b"
        let c = net.Cell "c"
        net.Relate([ a; b; c ], fun values -> List.length values = List.length (List.distinct values))
        net.RelateMany(
            [ [ a; b ]; [ b; c ] ],
            function [ left; right ] -> left <> right | _ -> false)
        net.Given(a, 1)
        let replay = Dictionary<Cell<int>, CellState<int>>()
        use subscription = net.Observe(fun (cell, state) -> replay.[cell] <- state)
        let generated = net.Generate 42UL
        let methodOk =
            match generated with
            | Ok solution ->
                replay.Count = 3
                && replay
                   |> Seq.forall (fun pair ->
                       match pair.Value with
                       | FiniteCandidates [ value ] -> Map.find pair.Key solution = value
                       | _ -> false)
            | Error _ -> false

        let opsOk =
            network (Domain.finite [ 1; 2 ]) {
                let! left = Ops.cell "left"
                let! right = Ops.cell "right"
                do! Ops.relate [ left; right ] (function [ x; y ] -> x <> y | _ -> false)
                do! Ops.given left 1
                let! generated = Ops.generate 7UL
                return
                    match generated with
                    | Ok solution -> Map.find left solution = 1 && Map.find right solution = 2
                    | Error _ -> false
            }
        methodOk && opsOk

module private FiniteCoreRegression =

    let private lowerGeneral model =
        match General.lower model with
        | Ok net -> net
        | Error errors -> failwithf "General.lower failed: %A" errors

    let private lowerOptimized model =
        match Optimized.lower model with
        | Ok net -> net
        | Error errors -> failwithf "Optimized.lower failed: %A" errors

    let private groupedModel arity =
        let cells = List.init 4 (fun index -> Cell.create (sprintf "group-%d-%d" arity index))
        let scopes =
            if arity = 2 then [ [ cells.[0]; cells.[1] ]; [ cells.[1]; cells.[2] ]; [ cells.[2]; cells.[3] ] ]
            else [ [ cells.[0]; cells.[1]; cells.[2] ]; [ cells.[1]; cells.[2]; cells.[3] ] ]
        let allows values = values |> List.forall ((=) (List.head values))
        let grouped =
            { domain = Domain.finite [ 2; 1; 0 ]
              cells = cells
              constraints = [ Constraint.relations scopes allows ]
              givens = [ cells.[0], 1 ] }
        let expanded =
            { grouped with constraints = scopes |> List.map (fun scope -> Constraint.relation scope allows) }
        grouped, expanded

    let groupedEquivalence () =
        [ 2; 3 ]
        |> List.forall (fun arity ->
            let grouped, expanded = groupedModel arity
            let solveGeneral model = General.solve (lowerGeneral model)
            let solveOptimized model = Optimized.solve (lowerOptimized model)
            let expected = Some (grouped.cells |> List.map (fun cell -> cell, 1) |> Map.ofList)
            solveGeneral grouped = expected
            && solveGeneral expanded = expected
            && solveOptimized grouped = expected
            && solveOptimized expanded = expected)

    let wideDomains () =
        [ 65; 128; 130 ]
        |> List.forall (fun count ->
            let model = Slices.wideDomainGuard count
            let cell = List.head model.cells
            match Optimized.solve (lowerOptimized model) with
            | Some solution -> Map.find cell solution = count - 1
            | None -> false)

    let asymmetricBinary () =
        let left = Cell.create "asymmetric-left"
        let right = Cell.create "asymmetric-right"
        let model =
            { domain = Domain.finite [ 0 .. 64 ]
              cells = [ left; right ]
              constraints =
                  [ Constraint.relation [ left; right ] (function
                        | [ a; b ] -> b = (a + 1) % 65
                        | _ -> false) ]
              givens = [ left, 64 ] }
        match Optimized.solve (lowerOptimized model) with
        | Some solution -> Map.find left solution = 64 && Map.find right solution = 0
        | None -> false

    let compileOncePerGroup () =
        let mutable calls = 0
        let cells = List.init 5 (fun index -> Cell.create (sprintf "compile-%d" index))
        let scopes = cells |> List.pairwise |> List.map (fun (left, right) -> [ left; right ])
        let allows = function
            | [ left; right ] -> calls <- calls + 1; left <> right
            | _ -> false
        let model =
            { domain = Domain.finite [ 1; 2; 3 ]
              cells = cells
              constraints = [ Constraint.relations scopes allows ]
              givens = [] }
        let solved = Optimized.solve (lowerOptimized model) |> Option.isSome
        solved && calls = 9

    let generationAndReplay () =
        let cells = List.init 6 (fun index -> Cell.create (sprintf "generate-%d" index))
        let scopes = cells |> List.pairwise |> List.map (fun (left, right) -> [ left; right ])
        let model =
            { domain = Domain.finite [ 2; 0; 1 ]
              cells = cells
              constraints =
                  [ Constraint.relations scopes (function [ left; right ] -> left <> right | _ -> false) ]
              givens = [] }
        let general = lowerGeneral model
        let optimized = lowerOptimized model
        let generalReplay = Dictionary<Cell<int>, CellState<int>>()
        let optimizedReplay = Dictionary<Cell<int>, CellState<int>>()
        use generalSubscription = General.onNet general (fun (cell, state) -> generalReplay.[cell] <- state)
        use optimizedSubscription = Optimized.onNet optimized (fun (cell, state) -> optimizedReplay.[cell] <- state)
        match General.generateWith 8 0x123456789ABCDEF0UL general,
              Optimized.generateWith 8 0x123456789ABCDEF0UL optimized with
        | Ok generalSolution, Ok optimizedSolution ->
            let replayMatches (replay: Dictionary<Cell<int>, CellState<int>>) solution =
                replay.Count = cells.Length
                && replay
                   |> Seq.forall (fun pair ->
                       match pair.Value with
                       | FiniteCandidates [ value ] -> Map.find pair.Key solution = value
                       | _ -> false)
            generalSolution = optimizedSolution
            && replayMatches generalReplay generalSolution
            && replayMatches optimizedReplay optimizedSolution
        | _ -> false

    let rngGolden () =
        let cells = List.init 4 (fun index -> Cell.create (sprintf "rng-%d" index))
        let model =
            { domain = Domain.finite [ 0 .. 9 ]
              cells = cells
              constraints = []
              givens = [] }
        let values solution = cells |> List.map (fun cell -> Map.find cell solution)
        match General.generateWith 1 0UL (lowerGeneral model), Optimized.generateWith 1 0UL (lowerOptimized model) with
        | Ok general, Ok optimized -> values general = [ 5; 0; 9; 4 ] && general = optimized
        | _ -> false

    let restartFailure () =
        let cell = Cell.create "impossible"
        let model =
            { domain = Domain.finite [ 1; 2 ]
              cells = [ cell ]
              constraints = [ Constraint.relation [ cell ] (fun _ -> false) ]
              givens = [] }
        General.generateWith 3 1UL (lowerGeneral model) = Error (RestartLimitExceeded 3)
        && Optimized.generateWith 3 1UL (lowerOptimized model) = Error (RestartLimitExceeded 3)

    let premiseGuard () =
        let model = Slices.premiseWidthGuard ()
        let net = lowerOptimized model
        let generationOk = Optimized.generateWith 1 9UL net |> Result.isOk
        let solveFails =
            try
                Optimized.solve net |> ignore
                false
            with :? System.InvalidOperationException as error ->
                error.Message.Contains("PremiseWidthExceeded(65, 64)")
        generationOk && solveFails

    let liveRetractionAndDisposal () =
        let left = Cell.create "live-left"
        let right = Cell.create "live-right"
        let model =
            { domain = Domain.finite [ 1; 2 ]
              cells = [ left; right ]
              constraints = [ Constraint.relation [ left; right ] (function [ a; b ] -> a = b | _ -> false) ]
              givens = [] }
        let net = lowerOptimized model
        let mutable events = 0
        let subscription = Optimized.onCell net right (fun _ -> events <- events + 1)
        let premise = Optimized.assume net left 1
        let narrowed =
            Optimized.value net right = FiniteCandidates [ 1 ]
            && Optimized.support net right = Set.singleton premise
        subscription.Dispose()
        subscription.Dispose()
        let beforeRetraction = events
        Optimized.retract net premise
        narrowed
        && Optimized.value net right = FiniteCandidates [ 1; 2 ]
        && Set.isEmpty (Optimized.support net right)
        && events = beforeRetraction

    let incrementalCellLookup () =
        let initial = Cell.create "lookup-initial"
        let model =
            { domain = Domain.finite [ 2; 1 ]
              cells = [ initial ]
              constraints = []
              givens = [] }
        let net = lowerOptimized model
        let addedLeft = Optimized.cell net "lookup-added-left"
        let addedRight = Optimized.cell net "lookup-added-right"
        Optimized.constrain net
            (Constraint.relations
                [ [ initial; addedLeft ]; [ addedLeft; addedRight ] ]
                (function [ left; right ] -> left = right | _ -> false))
        Optimized.assume net initial 1 |> ignore
        [ initial; addedLeft; addedRight ]
        |> List.forall (fun cell -> Optimized.value net cell = FiniteCandidates [ 1 ])

    let readSweep count =
        let net = Optimized.createFinite [ 0; 1 ]
        let cells = Array.init count (fun index -> Optimized.cell net (sprintf "read-%d" index))
        Optimized.value net cells.[0] |> ignore
        let timer = Stopwatch.StartNew()
        for cell in cells do Optimized.value net cell |> ignore
        timer.Stop()
        timer.Elapsed.TotalMilliseconds

    let settledBaselineRefresh () =
        let makeNetwork () =
            let net = ``Propagator-friendly``.Domain.finite [ 1; 2; 3 ]
            let cells = List.init 4 (fun index -> net.Cell(sprintf "baseline-%d" index))
            net.RelateMany(
                cells |> List.pairwise |> List.map (fun (left, right) -> [ left; right ]),
                function [ left; right ] -> left <> right | _ -> false)
            net, cells
        let values cells result =
            result |> Result.map (fun solution -> cells |> List.map (fun cell -> Map.find cell solution))
        let visible (net: ``Propagator-friendly``.Network<int, int, Set<int>>) cells =
            cells |> List.map (fun cell -> net.Value cell, net.Support cell)
        let compareEdited editCurrent editFresh =
            let current, currentCells = makeNetwork ()
            let fresh, freshCells = makeNetwork ()
            let replay = Dictionary<Cell<int>, CellState<int>>()
            use subscription = current.Observe(fun (cell, state) -> replay.[cell] <- state)
            editCurrent current currentCells
            let currentResult = current.Generate 42UL
            editFresh fresh freshCells
            let freshResult = fresh.Generate 42UL
            values currentCells currentResult = values freshCells freshResult
            && visible current currentCells = visible fresh freshCells
            && match currentResult with
               | Ok solution ->
                   replay.Count = currentCells.Length
                   && replay
                      |> Seq.forall (fun pair ->
                          match pair.Value with
                          | FiniteCandidates [ value ] -> Map.find pair.Key solution = value
                          | _ -> false)
               | Error _ -> false
        let assumeBetween =
            compareEdited
                (fun net cells ->
                    net.Generate 42UL |> ignore
                    net.Assume("edit", List.head cells, 2))
                (fun net cells -> net.Assume("edit", List.head cells, 2))
        let retractBetween =
            compareEdited
                (fun net cells ->
                    net.Assume("edit", List.head cells, 1)
                    net.Generate 42UL |> ignore
                    net.Retract "edit")
                (fun _ _ -> ())
        let replaceBetween =
            compareEdited
                (fun net cells ->
                    net.Assume("edit", List.head cells, 1)
                    net.Generate 42UL |> ignore
                    net.Assume("edit", List.head cells, 2))
                (fun net cells -> net.Assume("edit", List.head cells, 2))
        let failedRestartRestoresBaseline =
            let net = ``Propagator-friendly``.Domain.finite [ 1; 2 ]
            let cells = List.init 3 (fun index -> net.Cell(sprintf "odd-cycle-%d" index))
            net.RelateMany(
                [ [ cells.[0]; cells.[1] ]; [ cells.[1]; cells.[2] ]; [ cells.[2]; cells.[0] ] ],
                function [ left; right ] -> left <> right | _ -> false)
            let replay = Dictionary<Cell<int>, CellState<int>>()
            use subscription = net.Observe(fun (cell, state) -> replay.[cell] <- state)
            net.Generate(7UL, 3) = Error (RestartLimitExceeded 3)
            && (cells |> List.forall (fun cell -> net.Value cell = Set.ofList [ 1; 2 ] && Set.isEmpty (net.Support cell)))
            && replay.Count = cells.Length
            && (replay.Values |> Seq.forall ((=) (FiniteCandidates [ 1; 2 ])))
        assumeBetween && retractBetween && replaceBetween && failedRestartRestoresBaseline

    let binaryHotPathEdges () =
        let fullNet = ``Propagator-friendly``.Domain.finite [ 0; 1; 2 ]
        let fullSource = fullNet.Cell "full-source"
        let fullTarget = fullNet.Cell "full-target"
        fullNet.Relate(
            [ fullSource; fullTarget ],
            function [ source; _ ] -> source = 0 | _ -> false)
        fullNet.Restrict("source", fullSource, Set.singleton 0)
        let fullRowPreservesTop =
            fullNet.Value fullTarget = Set.ofList [ 0; 1; 2 ]
            && Set.isEmpty (fullNet.Support fullTarget)

        let emptyNet = ``Propagator-friendly``.Domain.finite [ 0; 1; 2 ]
        let emptySource = emptyNet.Cell "empty-source"
        let emptyTarget = emptyNet.Cell "empty-target"
        emptyNet.Relate(
            [ emptySource; emptyTarget ],
            function [ source; target ] -> source = target | _ -> false)
        emptyNet.Restrict("empty", emptySource, Set.empty)
        let emptySourceTransmitsBottom =
            Set.isEmpty (emptyNet.Value emptyTarget)
            && emptyNet.ShowSupport(emptyNet.Support emptyTarget) = "{empty}"
        fullRowPreservesTop && emptySourceTransmitsBottom

    let multiwordObserverReentry () =
        let net = ``Propagator-friendly``.Domain.finite [ 0 .. 64 ]
        let left = net.Cell "reentry-left"
        let middle = net.Cell "reentry-middle"
        let right = net.Cell "reentry-right"
        net.RelateMany(
            [ [ left; middle ]; [ middle; right ] ],
            function [ first; second ] -> first = second | _ -> false)
        let mutable armed = false
        use subscription =
            net.Observe(middle, fun (_, state) ->
                if armed && state = FiniteCandidates [ 42 ] then
                    armed <- false
                    net.Assume("nested", right, 42))
        armed <- true
        net.Assume("outer", left, 42)
        [ left; middle; right ]
        |> List.forall (fun cell -> net.Value cell = Set.singleton 42)
        && net.ShowSupport(net.Support middle) = "{outer}"
        && net.ShowSupport(net.Support right) = "{nested}"
        && not armed

    let run () =
        groupedEquivalence ()
        && wideDomains ()
        && asymmetricBinary ()
        && compileOncePerGroup ()
        && generationAndReplay ()
        && rngGolden ()
        && restartFailure ()
        && premiseGuard ()
        && liveRetractionAndDisposal ()
        && incrementalCellLookup ()
        && settledBaselineRefresh ()
        && binaryHotPathEdges ()
        && multiwordObserverReentry ()

let main () =
    printfn "runtime: %s" (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn ""

    let friendlyOk = FriendlyRegression.run ()
    let friendlyFiniteUxOk = FriendlyFiniteUx.run ()
    printfn "== Friendly finite UX lock =="
    printfn "  methods and Ops cover relations, repeated scopes, generation, and replay: %b" friendlyFiniteUxOk
    printfn ""

    printfn "== slice 2: 4x4 Sudoku through BOTH lowerings (Differential.solveBoth) =="
    let sud = Slices.sudoku4 ()
    let sudokuOk =
        match Differential.solveBoth sud with
        | Ok (Some fSol, Some oSol) ->
            let fg, og = gridOf sud fSol, gridOf sud oSol
            showGrid "general  (closure engine, FiniteRep.set):" fg
            showGrid "optimized (runtime-width finite core):"  og
            let agree = fSol = oSol
            let correct = fg = expected && og = expected
            printfn "  solveBoth agree: %b   both = known solution: %b" agree correct
            agree && correct
        | Ok _ -> printfn "  FAIL: a face returned no solution"; false
        | Error es -> printfn "  FAIL: unexpected lowering error %A" es; false
    printfn ""

    printfn "== slice 3: sparse 4x4 forces DDB and has an independent uniqueness oracle =="
    let sparse = Slices.sudoku4Sparse ()
    let initial = Harness.initialFixpoint sparse
    let openAfterPropagation = initial |> Map.toList |> List.filter (fun (_, values) -> List.length values > 1)
    let noContradiction = initial |> Map.forall (fun _ values -> not (List.isEmpty values))
    let searchRequired = noContradiction && not (List.isEmpty openAfterPropagation)
    printfn "  initial GAC fixpoint: %d open cells; search required: %b" openAfterPropagation.Length searchRequired
    let enumerated = Harness.bruteForceSolutions sparse
    let uniquelySolvable = List.length enumerated = 1
    printfn "  exhaustive authored-relation solutions: %d; unique: %b" enumerated.Length uniquelySolvable
    let sparseDifferentialOk =
        match Differential.solveBoth sparse, enumerated with
        | Ok (Some generalSol, Some optimizedSol), [ oracleSol ] ->
            let agree = generalSol = optimizedSol
            let matchesOracle = generalSol = oracleSol && optimizedSol = oracleSol
            printfn "  both faces solved: true; agree: %b; match unique oracle: %b" agree matchesOracle
            searchRequired && agree && matchesOracle
        | Ok (Some _, Some _), _ ->
            printfn "  FAIL: both faces solved but the exhaustive oracle is not unique"
            false
        | Ok _, _ -> printfn "  FAIL: a face returned no sparse solution"; false
        | Error es, _ -> printfn "  FAIL: unexpected sparse lowering error %A" es; false
    printfn ""

    printfn "== slice 1: C<->F is general-only (capability visible pre-engine) =="
    let cf = Slices.celsiusFahrenheit ()
    let optRejects =
        match Optimized.lower cf with
        | Error es -> printfn "  Optimized.lower rejects, naming why: %A" es; es = [ RichLatticeOnOptimized; DataflowOnOptimized ]
        | Ok _ -> printfn "  FAIL: optimized accepted a general-only model"; false
    let generalRuns =
        match General.lower cf with
        | Ok net ->
            match General.solve net with
            | Some sol ->
                let show iv = match iv with Empty -> "BOT" | Iv(lo, hi) -> sprintf "[%.6g, %.6g]" lo hi
                let read name = cf.cells |> List.tryFind (fun c -> c.name = name) |> Option.bind (fun c -> Map.tryFind c sol)
                printfn "  General.solve: C = %s,  F = %s"
                    (read "celsius"    |> Option.map show |> Option.defaultValue "-")
                    (read "fahrenheit" |> Option.map show |> Option.defaultValue "-")
                true
            | None -> printfn "  FAIL: general C<->F produced no settled value"; false
        | Error es -> printfn "  FAIL: general rejected C<->F %A" es; false
    printfn ""

    printfn "== generic General representation, guardrails, and runtime widths =="
    let duplicateDomain =
        try
            Domain.finite [ 1; 1; 2 ] |> ignore
            printfn "  FAIL: Domain.finite accepted duplicate values"
            false
        with :? System.ArgumentException ->
            printfn "  Domain.finite rejects duplicate values at authoring time"
            true
    let listRep values : FiniteRep<int, int list> =
        { top = values
          ofValues = id
          singleton = List.singleton
          meet = fun left right -> left |> List.filter (fun value -> List.contains value right)
          candidates = id
          isBottom = List.isEmpty
          isSingleton = fun state -> List.length state = 1
          equals = (=) }
    let genericGeneral =
        match General.lowerWith listRep sud with
        | Ok net ->
            match General.solve net with
            | Some solution ->
                let correct = gridOf sud solution = expected
                printfn "  General.lowerWith list representation solves correctly: %b" correct
                correct
            | None -> printfn "  FAIL: list representation produced no solution"; false
        | Error es -> printfn "  FAIL: list representation lowering failed: %A" es; false

    let authoredOrder = Slices.authoredOrderGuard ()
    let authoredOrderPreserved =
        match Differential.solveBoth authoredOrder with
        | Ok (Some generalSolution, Some optimizedSolution) ->
            let cell = List.head authoredOrder.cells
            let generalValue = Map.find cell generalSolution
            let optimizedValue = Map.find cell optimizedSolution
            let preserved = generalValue = 2 && optimizedValue = 2
            printfn "  authored finite-domain order preserved by both faces: %b" preserved
            preserved
        | Ok _ -> printfn "  FAIL: authored-order model produced no solution"; false
        | Error es -> printfn "  FAIL: authored-order model failed to lower: %A" es; false

    let dataflowFinite =
        match General.lower (Slices.dataflowOnFiniteGuard ()) with
        | Error es -> printfn "  General.lower finite+dataflow rejects: %A" es; es = [ DataflowRequiresRichLattice ]
        | Ok _ -> printfn "  FAIL: general silently ignored finite-domain dataflow"; false
    let relationLattice =
        match General.lower (Slices.relationOnLatticeGuard ()) with
        | Error es -> printfn "  General.lower lattice+relation rejects: %A" es; es = [ RelationRequiresFiniteDomain ]
        | Ok _ -> printfn "  FAIL: general silently ignored rich-domain relation"; false

    let wideDomain = FiniteCoreRegression.wideDomains ()
    printfn "  Optimized propagates 65-, 128-, and 130-value domains: %b" wideDomain
    let premiseWidth = FiniteCoreRegression.premiseGuard ()
    printfn "  65-cell model lowers/generates; optimized solve rejects before search: %b" premiseWidth
    let guardOk =
        duplicateDomain && genericGeneral && authoredOrderPreserved && dataflowFinite && relationLattice && wideDomain && premiseWidth
    printfn ""

    printfn "== live General reads, support, and retraction =="
    let liveDomain = Domain.lattice Open probeMeet (function Conflict -> true | _ -> false)
    let live = General.createLattice liveDomain
    let source = General.cell live "source"
    let target = General.cell live "target"
    General.convert live source target
        (function Point value -> Some value | _ -> None)
        Point id id
    let first = General.assume live source (Point 1)
    let second = General.assume live source (Point 2)
    let partialState =
        General.value live source = LatticeValue Conflict
        && General.value live target = LatticeValue (Point 1)
        && General.support live target = Set.singleton first
    General.retract live second
    let restoredFirst =
        General.value live source = LatticeValue (Point 1)
        && General.value live target = LatticeValue (Point 1)
    General.retract live first
    let restoredTop =
        General.value live source = LatticeValue Open
        && General.value live target = LatticeValue Open
        && Set.isEmpty (General.support live source)
        && Set.isEmpty (General.support live target)
    let liveOk = partialState && restoredFirst && restoredTop
    printfn "  partial contradiction remains cell-local; retract restores live state: %b" liveOk
    printfn ""

    let finiteCoreOk = FiniteCoreRegression.run ()
    printfn "== generalized finite core regressions =="
    printfn "  grouping, multiword, asymmetric tables, generation, replay, restart, and edits: %b" finiteCoreOk
    printfn ""

    let ok =
        friendlyOk
        && friendlyFiniteUxOk
        && sudokuOk
        && sparseDifferentialOk
        && optRejects
        && generalRuns
        && guardOk
        && liveOk
        && finiteCoreOk
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark" then
        let timedModel =
            Slices.sudoku4PairwiseWithGivens
                [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]
        match Differential.solveBoth timedModel with
        | Ok (Some generalSolution, Some optimizedSolution)
            when generalSolution = optimizedSolution && gridOf timedModel generalSolution = expected -> ()
        | result -> failwithf "timed pairwise model failed its pre-timing differential check: %A" result
        let generalSolve () =
            match General.lower timedModel with
            | Ok net -> General.solve net |> ignore
            | Error errors -> failwithf "benchmark General.lower failed: %A" errors
        let optimizedSolve () =
            match Optimized.lower timedModel with
            | Ok net -> Optimized.solve net |> ignore
            | Error errors -> failwithf "benchmark Optimized.lower failed: %A" errors
        let editNet = General.createLattice liveDomain
        let editSource = General.cell editNet "edit-source"
        let editTarget = General.cell editNet "edit-target"
        General.convert editNet editSource editTarget
            (function Point value -> Some value | _ -> None)
            Point id id
        let editCycle () =
            let accepted = General.assume editNet editSource (Point 1)
            let conflicting = General.assume editNet editSource (Point 2)
            General.retract editNet conflicting
            General.retract editNet accepted
        editCycle ()
        if General.value editNet editSource <> LatticeValue Open
           || General.value editNet editTarget <> LatticeValue Open then
            failwith "live edit benchmark failed its pre-timing restore check"
        printfn "== benchmark (same process; best/mean microseconds) =="
        let generalBest, generalMean = benchmark 3 3 5 generalSolve
        let optimizedBest, optimizedMean = benchmark 3 3 5 optimizedSolve
        let editBest, editMean = benchmark 100 5 500 editCycle
        printfn "  General lower+solve:     %.1f / %.1f us" generalBest generalMean
        printfn "  Optimized lower+solve:   %.1f / %.1f us" optimizedBest optimizedMean
        printfn "  General live edit cycle: %.1f / %.1f us" editBest editMean
        printfn "  Optimized/General speedup: %.2fx" (generalBest / optimizedBest)
        printfn ""
    if ok && fsi.CommandLineArgs |> Array.contains "--benchmark-read-sweep" then
        let count = 100000
        let elapsed = FiniteCoreRegression.readSweep count
        printfn "== optimized aggregate read sweep =="
        printfn "  %d cells read once: %.3f ms" count elapsed
        printfn ""
    printfn "RESULT: %s" (if ok then "PASS (differential, live edits, and capability witnesses correct)" else "FAIL")
    if ok then 0 else 1

exit (main ())
