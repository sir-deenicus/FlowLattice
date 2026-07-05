// tutorial-propagation-part1.fsx
// Extracted from docs/tutorial-propagation-part1.md ("Build Your Own Propagation Engine, Part 1").
// One engine; three demos: Celsius<->Fahrenheit (with retraction), 4x4 Sudoku (Set<int>),
// the same Sudoku re-encoded as uint16 bitsets. Plus a timing harness comparing Set vs bit.
//
//   dotnet fsi tutorial-propagation-part1.fsx              (plain)
//   dotnet fsi --optimize+ tutorial-propagation-part1.fsx  (optimized; more representative timing)
//
// The tutorial deliberately reuses variable names between the Set and bit Sudoku (they are
// drop-in swaps of each other). To run BOTH in one process for timing, each build is wrapped
// in its own function so the names no longer collide.
//
// Latest timing results are recorded in docs/benchmarks.md (not inline here — they would rot).

open System
open System.Diagnostics
open System.Collections.Generic
open System.Runtime.InteropServices

// =====================================================================================
// The engine (tutorial 7a-7e) — knows nothing of temperatures or Sudoku.
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
// Demo 1 — temperatures (tutorial 7f-7h): change-your-mind via retraction.
// =====================================================================================

type Scalar = Top | Val of float | Bot       // unknown / a number / impossible

let runTemperatures () =
    let scalarL =
        { top = Top
          meet = fun a b ->
            match a, b with
            | Top, x | x, Top       -> x
            | Val x, Val y when x=y -> Val x
            | _                     -> Bot }
    let e  = Engine<Scalar>(scalarL)
    let cC = e.NewCell()   // Celsius
    let fF = e.NewCell()   // Fahrenheit
    e.AddProp([cC], fun () -> match cC.value with Val x -> [ fF, { value = Val (x*9.0/5.0 + 32.0); support = cC.support } ] | _ -> []) |> ignore   // C->F
    e.AddProp([fF], fun () -> match fF.value with Val y -> [ cC, { value = Val ((y-32.0)*5.0/9.0); support = fF.support } ] | _ -> []) |> ignore   // F->C
    e.Assert(1, cC, Val 100.0)
    printfn "set C=100  ->  F = %A" (e.Value fF)
    e.Retract 1
    e.Assert(2, cC, Val 0.0)
    printfn "set C=0    ->  F = %A" (e.Value fF)

// =====================================================================================
// Shared Sudoku data — the 4x4 clues (tutorial 7l).
// =====================================================================================

let givens = [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]

// The 4 rows, 4 columns, and 4 boxes, as coordinate lists (tutorial 7j). Representation-independent.
let unitCoords : (int*int) list list =
    [ for r in 0..3 -> [ for c in 0..3 -> r, c ] ]
  @ [ for c in 0..3 -> [ for r in 0..3 -> r, c ] ]
  @ [ for br in 0..1 do for bc in 0..1 -> [ for dr in 0..1 do for dc in 0..1 -> 2*br+dr, 2*bc+dc ] ]

// =====================================================================================
// Demo 2 — Sudoku, Set<int> version (tutorial 7i-7l). Returns the solved grid.
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

// =====================================================================================
// Demo 3 — the same Sudoku, uint16 bitset version (tutorial 7m). Returns the solved grid.
// =====================================================================================

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
// Timing harness
// =====================================================================================

let expected = [| [|1;2;3;4|]; [|3;4;1;2|]; [|2;1;4;3|]; [|4;3;2;1|] |]
let gridEq (a: int[][]) (b: int[][]) = Array.forall2 (fun r1 r2 -> Array.forall2 (=) r1 r2) a b
let showGrid (g: int[][]) = for r in g do printfn "   %A" (List.ofArray r)

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
    printfn "  %-10s  best %9.3f us/solve   mean %9.3f us/solve   (%d trials x %d iters)" name best mean trials iters
    best, mean

let main () =
    printfn "runtime: %s" RuntimeInformation.FrameworkDescription
    printfn ""
    printfn "== temperatures (retraction) =="
    runTemperatures ()
    printfn ""

    let gSet = solveSudokuSet ()
    let gBit = solveSudokuBit ()
    printfn "== 4x4 Sudoku (Set<int>) =="
    showGrid gSet
    printfn "== 4x4 Sudoku (uint16 bitset) =="
    showGrid gBit
    let ok = gridEq gSet expected && gridEq gBit expected && gridEq gSet gBit
    printfn "correctness: %s" (if ok then "PASS (both match expected solution)" else "FAIL")
    printfn ""

    if not ok then
        eprintfn "Refusing to time a wrong solver."
        1
    else
        printfn "== timing (build engine + assert givens + solve, from scratch each iteration) =="
        let trials, iters = 5, 2000   // per-solve µs is iter-independent; keep total wall time short
        let setBest, _ = bench "Set<int>" trials iters solveSudokuSet
        let bitBest, _ = bench "uint16"   trials iters solveSudokuBit
        printfn ""
        printfn "  speedup (Set best / bit best): %.2fx" (setBest / bitBest)
        0

exit (main ())
