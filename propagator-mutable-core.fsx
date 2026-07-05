// propagator-mutable-core.fsx
// Step 1 of the mutable-core plan (docs/mutable-core-plan.md): an array-backed engine that
// should beat the Part-1 baseline on the SAME 4x4 Sudoku WITHOUT changing a law.
//
// Head-to-head, in ONE process (benchmarks.md rule: never compare across runs). This file
// carries BOTH engines:
//   - the Part-1 engine, copied verbatim from tutorial-propagation-part1.fsx (the baseline);
//   - the new array-backed engine, in `module M`.
// Both solve the identical 4x4 Sudoku in Set<int> and uint16 reps. We verify all four solves
// agree with the known solution BEFORE timing (A6 differential), then time four rows.
//
//   dotnet fsi propagator-mutable-core.fsx
//
// Timing results are recorded in docs/benchmarks.md (not inline — they would rot).

open System
open System.Diagnostics
open System.Collections.Generic

// =====================================================================================
// BASELINE engine — copied verbatim from tutorial-propagation-part1.fsx (tutorial 7a-7e).
// Knows nothing of temperatures or Sudoku.
// =====================================================================================

// A premise is a label for "a choice we are making now and could revoke later."
type Premise = int

// Who placed a fact on a cell — an outside assertion, or one of our propagators?
type Origin = Ext of Premise | Prop of int

// A fact about a cell: a value, plus the set of premises it rests on (its "stamp").
type Contribution<'a> = { value: 'a; support: Set<Premise> }

type Cell<'a> =
    { id: int
      contribs: Dictionary<Origin, Contribution<'a>>   // keyed by source, so a re-write replaces
      mutable value: 'a                                // cached meet of everything in contribs
      mutable support: Set<Premise> }                  // why `value` is what it is

// The entire difference between our two problems lives in here.
type Lattice<'a> = { top: 'a; meet: 'a -> 'a -> 'a }

type Propagator<'a> =
    { pid: int                                             // this propagator's id (its slot in a cell)
      reads: Cell<'a> list                                 // which cells to watch
      fire: unit -> (Cell<'a> * Contribution<'a>) list }   // what to conclude, when asked

type Engine<'a when 'a : equality>(L: Lattice<'a>) =
    let cells = ResizeArray<Cell<'a>>()                          // every cell, for retraction sweeps
    let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()   // cell id -> propagators watching it
    let mutable nCell = 0
    let mutable nProp = 0

    // Re-fold a cell's facts into its cached value + stamp.
    member private _.Recompute (c: Cell<'a>) =
        let mutable v = L.top
        let mutable s = Set.empty
        for kv in c.contribs do
            v <- L.meet v kv.Value.value
            if kv.Value.value <> L.top then s <- Set.union s kv.Value.support
        c.value <- v; c.support <- s

    // Fire propagators until the system goes quiet.
    member private this.Quiesce (frontier: seq<Cell<'a>>) =
        let q = Queue<Propagator<'a>>()
        let wake (c: Cell<'a>) = for p in watch.[c.id] do q.Enqueue p
        Seq.iter wake frontier
        while q.Count > 0 do
            let p = q.Dequeue()
            for (target, fact) in p.fire () do
                let before = target.value
                target.contribs.[Prop p.pid] <- fact          // record this propagator's latest word
                this.Recompute target
                if target.value <> before then wake target    // changed? then wake its watchers

    member _.NewCell () =
        let c = { id = nCell; contribs = Dictionary(); value = L.top; support = Set.empty }
        nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

    member _.AddProp (reads, fire) =
        let p = { pid = nProp; reads = reads; fire = fire }
        nProp <- nProp + 1
        for c in reads do watch.[c.id].Add p     // register p as a watcher of each cell it reads
        p

    member _.Value (c: Cell<'a>) = c.value

    member this.Assert (p: Premise, c: Cell<'a>, v: 'a) =
        c.contribs.[Ext p] <- { value = v; support = Set.singleton p }
        this.Recompute c
        this.Quiesce [c]

    member this.Retract (p: Premise) =
        let touched = ResizeArray<Cell<'a>>()
        for c in cells do
            let dead = [ for kv in c.contribs do if kv.Value.support.Contains p then yield kv.Key ]
            if not dead.IsEmpty then
                for k in dead do c.contribs.Remove k |> ignore   // drop every fact stamped p
                this.Recompute c                                 // value rises back toward Top
                touched.Add c
        this.Quiesce touched                                     // re-derive whatever still has support

// =====================================================================================
// Shared scenario — the 4x4 clues + units (tutorial 7l/7j). Representation-independent.
// =====================================================================================

let givens = [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]

let unitCoords : (int*int) list list =
    [ for r in 0..3 -> [ for c in 0..3 -> r, c ] ]
  @ [ for c in 0..3 -> [ for r in 0..3 -> r, c ] ]
  @ [ for br in 0..1 do for bc in 0..1 -> [ for dr in 0..1 do for dc in 0..1 -> 2*br+dr, 2*bc+dc ] ]

let expected = [| [|1;2;3;4|]; [|3;4;1;2|]; [|2;1;4;3|]; [|4;3;2;1|] |]
let gridEq (a: int[][]) (b: int[][]) = Array.forall2 (fun r1 r2 -> Array.forall2 (=) r1 r2) a b
let showGrid (g: int[][]) = for r in g do printfn "   %A" (List.ofArray r)

// =====================================================================================
// BASELINE Sudoku wirings — verbatim from the tutorial (Set<int> and uint16 bitset).
// =====================================================================================

let solveSudokuSet () : int[][] =
    let setL = { top = set [1..4]; meet = Set.intersect }   // start open; combine by intersection
    let solved (d: Set<int>) = Set.count d = 1              // pinned to exactly one digit?
    let s    = Engine<Set<int>>(setL)
    let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
    let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]
    for u in unitCoords do
        let cs    = at u
        let sup () = cs |> List.map (fun p -> p.support) |> Set.unionMany
        // naked single: digits taken by a solved peer are no longer available here
        s.AddProp(cs, fun () ->
            [ for t in cs ->
                let gone = cs |> List.fold (fun a p -> if p.id <> t.id && solved p.value then Set.union a p.value else a) Set.empty
                t, { value = Set.difference (set [1..4]) gone; support = sup () } ]) |> ignore
        // hidden single: a digit with a unique home in the group lands there
        s.AddProp(cs, fun () ->
            [ for v in 1..4 do
                match cs |> List.filter (fun p -> p.value.Contains v) with
                | [t] when not (solved t.value) -> yield t, { value = set [v]; support = sup () }
                | _ -> () ]) |> ignore
    givens |> List.iteri (fun i ((r, c), v) -> s.Assert(100 + i, grid.[r, c], set [v]))
    let digit (d: Set<int>) = if solved d then Set.minElement d else 0
    [| for r in 0..3 -> [| for c in 0..3 -> digit (s.Value grid.[r,c]) |] |]

let solveSudokuBit () : int[][] =
    let bitL = { top = 0xFus; meet = (&&&) }                       // 1111; meet = intersection = AND
    let bit v : uint16 = 1us <<< (v - 1)                           // digit v -> its single bit
    let single (d: uint16) = d <> 0us && (d &&& (d - 1us)) = 0us   // exactly one bit set?
    let s    = Engine<uint16>(bitL)
    let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
    let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]
    for u in unitCoords do
        let cs    = at u
        let sup () = cs |> List.map (fun p -> p.support) |> Set.unionMany
        s.AddProp(cs, fun () ->                                    // naked single
            [ for t in cs ->
                let gone = cs |> List.fold (fun a p -> if p.id <> t.id && single p.value then a ||| p.value else a) 0us
                t, { value = 0xFus &&& ~~~gone; support = sup () } ]) |> ignore
        s.AddProp(cs, fun () ->                                    // hidden single
            [ for v in 1..4 do
                match cs |> List.filter (fun p -> p.value &&& bit v <> 0us) with
                | [t] when not (single t.value) -> yield t, { value = bit v; support = sup () }
                | _ -> () ]) |> ignore
    givens |> List.iteri (fun i ((r, c), v) -> s.Assert(100 + i, grid.[r, c], bit v))
    let digit (d: uint16) = if single d then [1..4] |> List.find (fun v -> d &&& bit v <> 0us) else 0
    [| for r in 0..3 -> [| for c in 0..3 -> digit (s.Value grid.[r,c]) |] |]

// =====================================================================================
// Timing harness — verbatim from the tutorial.
// =====================================================================================

/// best-of-`trials` mean microseconds per solve; also returns the across-trials mean.
let bench (name: string) (trials: int) (iters: int) (f: unit -> 'r) : float * float =
    f () |> ignore                                   // touch
    for _ in 1 .. 2000 do f () |> ignore             // warm the JIT
    let mutable best = Double.MaxValue
    let mutable sum  = 0.0
    for _ in 1 .. trials do
        GC.Collect(); GC.WaitForPendingFinalizers()
        let sw = Stopwatch.StartNew()
        for _ in 1 .. iters do f () |> ignore
        sw.Stop()
        let perUs = sw.Elapsed.TotalMilliseconds * 1000.0 / float iters
        best <- min best perUs
        sum  <- sum + perUs
    let mean = sum / float trials
    printfn "  %-12s  best %9.3f us/solve   mean %9.3f us/solve   (%d trials x %d iters)" name best mean trials iters
    best, mean

// =====================================================================================
// NEW array-backed engine — module M. Same laws (meet/top, Assert/Retract,
// propagate-to-quiescence), different machinery:
//   - cells are int indices into structure-of-arrays (values/supports) — no per-cell record,
//     no per-cell Dictionary;
//   - support is a uint64 premise bitmask (union = |||, no allocation) — caps at 64 live premises;
//   - the worklist is a Queue<int> of prop-ids with a `queued` dedup flag — each prop enqueued
//     at most once (kills the baseline's duplicate-enqueue churn);
//   - propagators emit via a single reused callback — no per-fire list allocation;
//   - change-detection uses the lattice's own `equals` — no generic-equality boxing of value types;
//   - retraction resets the premise's support-cone and replays (correct by confluence).
// =====================================================================================

module M =

    /// 4-field lattice: adds `isBot` (contradiction test) and `equals` (rep-specific change
    /// detector, replacing the baseline's generic `<>` which boxes value types like uint16).
    type Lattice<'a> =
        { top: 'a
          meet: 'a -> 'a -> 'a
          isBot: 'a -> bool
          equals: 'a -> 'a -> bool }

    /// A propagator contributes by calling `emit targetCell value support` per output.
    type Emit<'a> = int -> 'a -> uint64 -> unit

    type Engine<'a>(L: Lattice<'a>) =
        // ---- setup-time, growable ----
        let watchB = ResizeArray<ResizeArray<int>>()        // cell id -> prop ids watching it
        let firesB = ResizeArray<Emit<'a> -> unit>()        // prop id -> its fire(emit)
        let exts   = Dictionary<struct(int * int), 'a>()    // (premise, cell) -> the asserted value
        let mutable nCell = 0
        let mutable nProp = 0
        // ---- frozen at first Assert ----
        let mutable values   : 'a[]     = [||]
        let mutable supports : uint64[] = [||]
        let mutable watch    : int[][]  = [||]
        let mutable fires    : (Emit<'a> -> unit)[] = [||]
        let mutable queued   : bool[]   = [||]
        let mutable frozen   = false
        let q = Queue<int>()

        // The one emit closure per engine: meet a contribution into a cell; iff it narrowed,
        // OR-in its premises and wake the cell's watchers (dedup via `queued`).
        let emit (t: int) (v: 'a) (s: uint64) =
            let nv = L.meet values.[t] v
            if not (L.equals nv values.[t]) then
                values.[t]   <- nv
                supports.[t] <- supports.[t] ||| s
                let ws = watch.[t]
                for i in 0 .. ws.Length - 1 do
                    let p = ws.[i]
                    if not queued.[p] then queued.[p] <- true; q.Enqueue p

        let quiesce () =
            while q.Count > 0 do
                let p = q.Dequeue()
                queued.[p] <- false
                fires.[p] emit

        // Snap the growable setup state into flat arrays the first time we run.
        member private _.Freeze () =
            if not frozen then
                frozen   <- true
                values   <- Array.create nCell L.top
                supports <- Array.zeroCreate nCell
                watch    <- [| for w in watchB -> w.ToArray() |]
                fires    <- firesB.ToArray()
                queued   <- Array.zeroCreate nProp

        member _.NewCell () =
            let id = nCell
            nCell <- nCell + 1
            watchB.Add(ResizeArray())
            id

        member _.AddProp (reads: int list, fire: Emit<'a> -> unit) =
            let pid = nProp
            nProp <- nProp + 1
            firesB.Add fire
            for c in reads do watchB.[c].Add pid
            pid

        member _.Value   (c: int) = values.[c]
        member _.Support (c: int) = supports.[c]
        member _.IsBot   (c: int) = L.isBot values.[c]

        member this.Assert (p: int, c: int, v: 'a) =
            this.Freeze ()
            exts.[struct(p, c)] <- v
            emit c v (1UL <<< p)
            quiesce ()

        member this.Retract (p: int) =
            this.Freeze ()
            let bit = 1UL <<< p
            // 1. drop p's external assertions
            let dead =
                [ for kv in exts do
                    let struct(pr, _) = kv.Key
                    if pr = p then yield kv.Key ]
            for k in dead do exts.Remove k |> ignore
            // 2. reset p's support-cone back to Top (dependency-directed, read off the bitmask)
            for c in 0 .. nCell - 1 do
                if supports.[c] &&& bit <> 0UL then
                    values.[c]   <- L.top
                    supports.[c] <- 0UL
            // 3. restore the surviving external assertions directly
            for kv in exts do
                let struct(pr, cc) = kv.Key
                values.[cc]   <- L.meet values.[cc] kv.Value
                supports.[cc] <- supports.[cc] ||| (1UL <<< pr)
            // 4. replay every propagator to rebuild derived narrowing; confluence => unique fixpoint
            for pid in 0 .. nProp - 1 do
                if not queued.[pid] then queued.[pid] <- true; q.Enqueue pid
            quiesce ()

// =====================================================================================
// NEW Sudoku wirings on module M — same two propagators, same clues, same premises'
// meaning. Differences: cells are ints, support is read as a uint64, propagators `emit`
// instead of returning a list, and the lattice supplies `isBot`/`equals`.
// Premises are 0..7 (the bitmask caps at 64; the baseline's 100+i would overflow).
// =====================================================================================

let solveSudokuSetM () : int[][] =
    let setL : M.Lattice<Set<int>> =
        { top = set [1..4]; meet = Set.intersect
          isBot = Set.isEmpty; equals = (=) }
    let solved (d: Set<int>) = Set.count d = 1
    let s    = M.Engine<Set<int>>(setL)
    let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
    let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]
    for u in unitCoords do
        let cs     = at u
        let sup () = cs |> List.fold (fun acc c -> acc ||| s.Support c) 0UL
        s.AddProp(cs, fun emit ->                                  // naked single
            let support = sup ()
            for t in cs do
                let gone = cs |> List.fold (fun a p -> if p <> t && solved (s.Value p) then Set.union a (s.Value p) else a) Set.empty
                emit t (Set.difference (set [1..4]) gone) support) |> ignore
        s.AddProp(cs, fun emit ->                                  // hidden single
            let support = sup ()
            for v in 1..4 do
                match cs |> List.filter (fun p -> (s.Value p).Contains v) with
                | [t] when not (solved (s.Value t)) -> emit t (set [v]) support
                | _ -> ()) |> ignore
    givens |> List.iteri (fun i ((r, c), v) -> s.Assert(i, grid.[r, c], set [v]))
    let digit (d: Set<int>) = if solved d then Set.minElement d else 0
    [| for r in 0..3 -> [| for c in 0..3 -> digit (s.Value grid.[r,c]) |] |]

let solveSudokuBitM () : int[][] =
    let bitL : M.Lattice<uint16> =
        { top = 0xFus; meet = (&&&)
          isBot = (fun d -> d = 0us); equals = (fun (a: uint16) b -> a = b) }
    let bit v : uint16 = 1us <<< (v - 1)
    let single (d: uint16) = d <> 0us && (d &&& (d - 1us)) = 0us
    let s    = M.Engine<uint16>(bitL)
    let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
    let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]
    for u in unitCoords do
        let cs     = at u
        let sup () = cs |> List.fold (fun acc c -> acc ||| s.Support c) 0UL
        s.AddProp(cs, fun emit ->                                  // naked single
            let support = sup ()
            for t in cs do
                let gone = cs |> List.fold (fun a p -> if p <> t && single (s.Value p) then a ||| (s.Value p) else a) 0us
                emit t (0xFus &&& ~~~gone) support) |> ignore
        s.AddProp(cs, fun emit ->                                  // hidden single
            let support = sup ()
            for v in 1..4 do
                match cs |> List.filter (fun p -> (s.Value p) &&& bit v <> 0us) with
                | [t] when not (single (s.Value t)) -> emit t (bit v) support
                | _ -> ()) |> ignore
    givens |> List.iteri (fun i ((r, c), v) -> s.Assert(i, grid.[r, c], bit v))
    let digit (d: uint16) = if single d then [1..4] |> List.find (fun v -> d &&& bit v <> 0us) else 0
    [| for r in 0..3 -> [| for c in 0..3 -> digit (s.Value grid.[r,c]) |] |]

// =====================================================================================
// Retraction on module M — the C<->F "change your mind" demo, to exercise the new
// reset-cone + replay Retract (Sudoku never retracts, so it wouldn't cover this path).
// Returns true iff the observable behavior matches the baseline engine's.
// =====================================================================================

type Scalar = Top | Val of float | Bot       // unknown / a number / impossible

let runTemperaturesM () : bool =
    let scalarL : M.Lattice<Scalar> =
        { top = Top
          meet = (fun a b ->
            match a, b with
            | Top, x | x, Top       -> x
            | Val x, Val y when x=y -> Val x
            | _                     -> Bot)
          isBot  = (fun s -> s = Bot)
          equals = (=) }
    let e  = M.Engine<Scalar>(scalarL)
    let cC = e.NewCell()   // Celsius
    let fF = e.NewCell()   // Fahrenheit
    e.AddProp([cC], fun emit -> match e.Value cC with Val x -> emit fF (Val (x*9.0/5.0 + 32.0)) (e.Support cC) | _ -> ()) |> ignore  // C->F
    e.AddProp([fF], fun emit -> match e.Value fF with Val y -> emit cC (Val ((y-32.0)*5.0/9.0)) (e.Support fF) | _ -> ()) |> ignore  // F->C
    e.Assert(0, cC, Val 100.0)
    let f1 = e.Value fF
    printfn "  set C=100  ->  F = %A" f1
    e.Retract 0
    let cR, fR = e.Value cC, e.Value fF
    printfn "  retract C  ->  C = %A,  F = %A" cR fR
    e.Assert(1, cC, Val 0.0)
    let f2 = e.Value fF
    printfn "  set C=0    ->  F = %A" f2
    f1 = Val 212.0 && cR = Top && fR = Top && f2 = Val 32.0

// =====================================================================================
// Main — verify all four solvers against the known solution and check M's retraction
// (A6 differential), then time the 4-row head-to-head. Refuses to time a wrong solver.
// =====================================================================================

let main () =
    printfn "runtime: %s" (Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn ""
    let grids =
        [ "baseline Set", solveSudokuSet ()
          "baseline bit", solveSudokuBit ()
          "M Set",        solveSudokuSetM ()
          "M bit",        solveSudokuBitM () ]
    for name, g in grids do
        printfn "== %s ==" name
        showGrid g
    let gridsOk = grids |> List.forall (fun (_, g) -> gridEq g expected)
    printfn ""
    printfn "== M retraction (C<->F change-your-mind) =="
    let retractOk = runTemperaturesM ()
    printfn ""
    let ok = gridsOk && retractOk
    printfn "correctness: %s"
        (if ok then "PASS (four solvers match the known solution; M retraction behaves)"
         else sprintf "FAIL (grids=%b retraction=%b)" gridsOk retractOk)
    printfn ""

    if not ok then
        eprintfn "Refusing to time a wrong solver."
        1
    else
        let envInt name dflt =
            match Environment.GetEnvironmentVariable name with
            | null | "" -> dflt
            | s -> int s
        let trials, iters = envInt "TRIALS" 5, envInt "ITERS" 2000
        printfn "== timing (build engine + assert givens + solve, from scratch each iteration) =="
        let bSet, _ = bench "baseline Set" trials iters solveSudokuSet
        let bBit, _ = bench "baseline bit" trials iters solveSudokuBit
        let mSet, _ = bench "M Set"        trials iters solveSudokuSetM
        let mBit, _ = bench "M bit"        trials iters solveSudokuBitM
        printfn ""
        printfn "  M-Set vs baseline-Set : %5.2fx" (bSet / mSet)
        printfn "  M-bit vs baseline-bit : %5.2fx" (bBit / mBit)
        printfn "  M-bit vs baseline-Set : %5.2fx" (bSet / mBit)
        0

exit (main ())
