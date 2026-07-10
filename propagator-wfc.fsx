// propagator-wfc.fsx
// Work items 1+2 of the WFC roadmap (docs/mutable-core-plan.md, "Work left, ordered"):
//   1. the bit-plane SoA store — tile-count width W = ceil(nTiles/64) is a RUNTIME value,
//      no per-cell heap value (a generic 'a[] can't hold a runtime-width multiword bitset
//      without boxing one array per cell x 250k);
//   2. the tiled adjacency propagator — B ∩= ⋁_{t∈A} allowed[t][dir], looped over words.
// Also pulled forward from the item-4 spec: the ⊥ early-exit in quiescence (abort when a meet
// empties a cell, then drain the queue AND unset the queued[] dedup flags). The drain+clear
// machinery is shared with the item-3 trail unwind, and without the early-exit a contradiction
// avalanches ∅ across the whole grid before control returns — confusing even in small demos.
//
// As-built delta from the plan's sketch: the store is CELL-major (one flat uint64[nCells*W],
// cell c's words contiguous at [c*W .. c*W+W-1]), not plane-major (W separate uint64[nCells]).
// Same laws, same no-per-cell-heap point; chosen because the hot path touches ALL W words of
// two cells per fire — contiguous words = 1-2 cache lines, where plane-major pays W scattered
// lines — and no whole-grid single-plane pass exists that would favor plane-major.
//
// Items 3+4 (2026-07-03, second pass): the TRAIL is engine-side MECHANISM — journal
// (cell, oldWords, oldSupport) per narrow, Mark/UndoTo for LIFO unwind onto the exact
// pre-guess fixpoint — plus a narrow/undo observation hook and a premise-free Collapse.
// The SEARCH DRIVER is POLICY and lives OUTSIDE the engine, in module WfcSearch below,
// built on Hansei.TreeSearch: the search monad is the list monad (Exhaustive) or its lazy
// seq twin (SeqSearch), and `guard` is an if-check that returns [] on fail — a failed
// collapse contributes nothing and the enumeration itself is the backtracking (Wadler's
// list of successes). Min-entropy selection, tile ordering, and the entropy heap are all
// driver-side; the engine never learns what a "guess" is.
//
// NOT here yet (later item): cone-local Retract (5). There is no Retract in this file at
// all — the step-5 cone-local design replaces the step-1 replay-ALL, so building the
// throwaway version here would be churn. Supports (uint64 premise masks, OR-on-narrow)
// ARE tracked: the trail journals them and provenance is the point of keeping them.
//
// Verified on hand-computable grids BEFORE timing (refuses to time a wrong engine); first
// 500x500 timings are recorded in docs/benchmarks.md (not inline — they would rot).
//
//   dotnet fsi propagator-wfc.fsx

#r @"C:\Users\cybernetic\source\repos\Hansei\Hansei.Continuation\bin\Debug\net50\Hansei.Core.dll"

open System
open System.Diagnostics
open System.Collections.Generic
open System.Numerics

module Wfc =

    // Directions: 0=N (row-1), 1=E (col+1), 2=S (row+1), 3=W (col-1). opposite d = (d+2)%4.

    /// allowed.[d].[t] = bitset row (W words) of tiles permitted at the d-neighbor of a cell
    /// holding tile t. Authoring must be consistent — rule d a b = rule (opposite d) b a — or
    /// the two edge directions encode two different constraints; `checkConsistent` tests this.
    /// The engine doesn't assume it (an inconsistent table is still a sound monotone
    /// propagator, just not the constraint you meant). An all-zero row is legitimate: "tile t
    /// admits NO d-neighbor" — asserting t in the interior then contradicts, by design.
    let buildAllowed (nTiles: int) (rule: int -> int -> int -> bool) : uint64[][][] =
        let nWords = (nTiles + 63) / 64
        [| for d in 0 .. 3 ->
            [| for a in 0 .. nTiles - 1 ->
                 let row = Array.zeroCreate nWords
                 for b in 0 .. nTiles - 1 do
                     if rule d a b then row.[b >>> 6] <- row.[b >>> 6] ||| (1UL <<< (b &&& 63))
                 row |] |]

    let checkConsistent (nTiles: int) (rule: int -> int -> int -> bool) : bool =
        let mutable ok = true
        for d in 0 .. 3 do
            for a in 0 .. nTiles - 1 do
                for b in 0 .. nTiles - 1 do
                    if rule d a b <> rule ((d + 2) % 4) b a then ok <- false
        ok

    /// The specialized WFC store + propagator. Same laws as module M in
    /// propagator-mutable-core.fsx (meet-only descent, OR-on-narrow supports, worklist to
    /// quiescence), different machinery: the value rep is W words per cell in one flat array,
    /// and the propagator is not a closure — the grid's structural neighbor enumeration IS the
    /// propagator set (4 directed edges per cell), so the worklist holds CELL ids and firing a
    /// cell pushes its constraint into its ≤4 neighbors. (~1M closure allocations avoided at
    /// 500x500; the write-side of every edge is structurally known, which item 5 needs anyway.)
    type Engine(width: int, height: int, nTiles: int, allowed: uint64[][][]) =
        let nCells = width * height
        let nWords = (nTiles + 63) / 64
        // ⊤ per word: all-ones, except the last word masks off bits >= nTiles
        let topWord =
            Array.init nWords (fun w ->
                if w = nWords - 1 && nTiles &&& 63 <> 0
                then (1UL <<< (nTiles &&& 63)) - 1UL
                else UInt64.MaxValue)
        let words    = Array.zeroCreate<uint64> (nCells * nWords)  // cell-major, see header
        let supports = Array.zeroCreate<uint64> nCells             // premise bitmask per cell
        let queued   = Array.zeroCreate<bool> nCells               // worklist dedup flag
        let q        = Queue<int>()                                // cells whose narrowing is unpushed
        let scratch  = Array.zeroCreate<uint64> nWords             // the union accumulator (single-threaded)
        let mutable botCell = -1                                   // -1 = no contradiction

        // --- item 3: the trail (MECHANISM only — all search policy lives in WfcSearch) ---
        // Journal (cell, oldWords, oldSupport) per narrow. LIFO restore of BOTH fields lands
        // exactly on the pre-mark quiescent fixpoint (the saved values ARE the prior fixpoint),
        // so an unwind needs NO re-propagation; value-only restore would leave strict-superset
        // support masks — fixpoints stay correct, but provenance lies and authored-Retract
        // cones inflate. Struct-of-arrays with doubling: no per-entry allocation on the hot path.
        let mutable tLen   = 0
        let mutable tCell  = Array.zeroCreate<int> 4096
        let mutable tSup   = Array.zeroCreate<uint64> 4096
        let mutable tWords = Array.zeroCreate<uint64> (4096 * nWords)
        let oldBuf  = Array.zeroCreate<uint64> nWords              // meet's pre-image, journaled on narrow
        let oneTile = Array.zeroCreate<uint64> nWords              // Collapse's single-tile mask
        // Observation hook: called with the cell id on every narrow AND every undo-restore.
        // This is the engine's whole contribution to selection policy — a driver subscribes
        // (e.g. to maintain an entropy heap); the engine never learns what a "guess" is.
        let mutable onChange : int -> unit = ignore

        let growTrail () =
            let cap = tCell.Length * 2
            let c2 = Array.zeroCreate cap              in Array.blit tCell 0 c2 0 tLen;             tCell <- c2
            let s2 = Array.zeroCreate cap              in Array.blit tSup 0 s2 0 tLen;              tSup <- s2
            let w2 = Array.zeroCreate (cap * nWords)   in Array.blit tWords 0 w2 0 (tLen * nWords); tWords <- w2

        // Must run BEFORE the support OR — the entry holds the pre-narrow support.
        let journal (c: int) =
            if tLen = tCell.Length then growTrail ()
            tCell.[tLen] <- c
            tSup.[tLen]  <- supports.[c]
            Array.blit oldBuf 0 tWords (tLen * nWords) nWords
            tLen <- tLen + 1

        let resetStore () =
            for c in 0 .. nCells - 1 do
                let b = c * nWords
                for w in 0 .. nWords - 1 do words.[b + w] <- topWord.[w]
            Array.fill supports 0 nCells 0UL
            botCell <- -1
        do resetStore ()

        // ⊥ abort / unwind machinery (item-4 spec): drain the queue AND unset the queued[]
        // flags — q.Clear() alone would leave drained cells flagged queued-forever, and the
        // dedup check would never re-admit them.
        let drainWorklist () =
            while q.Count > 0 do queued.[q.Dequeue()] <- false

        let enqueue (c: int) =
            if not queued.[c] then queued.[c] <- true; q.Enqueue c

        // Meet `mask` into cell t under premise-support s. OR-on-narrow: s joins t's support
        // iff the meet actually narrowed t. On ∅ the support is still ORed first — the bot
        // cell's mask then names exactly the premises implicated in the contradiction.
        let meetInto (t: int) (mask: uint64[]) (s: uint64) =
            let b = t * nWords
            let mutable narrowed = false
            let mutable alive = 0UL
            for w in 0 .. nWords - 1 do
                let ov = words.[b + w]
                oldBuf.[w] <- ov
                let nv = ov &&& mask.[w]
                if nv <> ov then narrowed <- true; words.[b + w] <- nv
                alive <- alive ||| nv
            if narrowed then
                journal t
                supports.[t] <- supports.[t] ||| s
                onChange t
                if alive = 0UL then botCell <- t else enqueue t

        // Push c's constraint into neighbor n through direction d: n ∩= ⋁_{t∈c} allowed[d][t].
        // Cost is proportional to c's popcount — cheap when c is near-collapsed (the common
        // case once a driver is collapsing cells); the dense-⊤-wave benches below are the
        // deliberate worst case, and the AC-4 counter variant is the fallback if they dominate.
        let push (c: int) (d: int) (n: int) =
            Array.fill scratch 0 nWords 0UL
            let rows = allowed.[d]
            let b = c * nWords
            for w in 0 .. nWords - 1 do
                let mutable bits = words.[b + w]
                while bits <> 0UL do
                    let t = (w <<< 6) + BitOperations.TrailingZeroCount bits
                    bits <- bits &&& (bits - 1UL)
                    let row = rows.[t]
                    for i in 0 .. nWords - 1 do scratch.[i] <- scratch.[i] ||| row.[i]
            meetInto n scratch supports.[c]

        let quiesce () =
            while botCell < 0 && q.Count > 0 do
                let c = q.Dequeue()
                queued.[c] <- false
                let r   = c / width
                let col = c % width
                if botCell < 0 && r > 0            then push c 0 (c - width)
                if botCell < 0 && col < width - 1  then push c 1 (c + 1)
                if botCell < 0 && r < height - 1   then push c 2 (c + width)
                if botCell < 0 && col > 0          then push c 3 (c - 1)
            if botCell >= 0 then drainWorklist ()

        member _.WordsPerCell = nWords
        member _.Contradiction = botCell >= 0
        member _.BotCell = botCell
        /// Invariant probe for tests: after any Assert returns, the worklist must be clean
        /// (empty queue, no stray dedup flags) whether it quiesced or ⊥-aborted.
        member _.WorklistClean = q.Count = 0 && Array.forall not queued

        member _.Reset () = drainWorklist (); resetStore (); tLen <- 0

        member _.OnChange with set (f: int -> unit) = onChange <- f

        /// Trail mark = entry count. Capture before a guess; UndoTo it to unwind the guess.
        member _.Mark () = tLen

        /// LIFO-unwind the trail to `mark`, restoring both value words and support per entry,
        /// clearing the worklist (queue AND queued[] dedup flags — q.Clear() alone would leave
        /// cells flagged deaf forever) and the contradiction. The restored state is the
        /// pre-mark quiescent fixpoint: nothing needs re-propagation.
        member _.UndoTo (mark: int) =
            drainWorklist ()
            botCell <- -1
            while tLen > mark do
                tLen <- tLen - 1
                let c = tCell.[tLen]
                Array.blit tWords (tLen * nWords) words (c * nWords) nWords
                supports.[c] <- tSup.[tLen]
                onChange c

        /// Search-driver assert: meet {tile} into cell and propagate, spending NO premise bit
        /// (support mask 0 — premises are for AUTHORED constraints, never for search depth;
        /// provenance from authored pins still flows through the collapse's wave).
        /// Returns false iff the store is (or was already) contradicted.
        member _.Collapse (cell: int, tile: int) : bool =
            if botCell < 0 then
                Array.fill oneTile 0 nWords 0UL
                oneTile.[tile >>> 6] <- 1UL <<< (tile &&& 63)
                meetInto cell oneTile 0UL
                quiesce ()
            botCell < 0

        /// Meet `mask` into `cell` under premise p (0..63), then propagate to quiescence.
        /// Returns false iff the store is (or was already) contradicted.
        member _.Assert (premise: int, cell: int, mask: uint64[]) : bool =
            if premise < 0 || premise > 63 then invalidArg "premise" "premise bitmask caps at 64"
            if botCell < 0 then
                meetInto cell mask (1UL <<< premise)
                quiesce ()
            botCell < 0

        member this.AssertTile (premise: int, cell: int, tile: int) : bool =
            let mask = Array.zeroCreate nWords
            mask.[tile >>> 6] <- 1UL <<< (tile &&& 63)
            this.Assert(premise, cell, mask)

        member _.Domain  (cell: int) : uint64[] = Array.sub words (cell * nWords) nWords
        member _.Has     (cell: int, tile: int) =
            words.[cell * nWords + (tile >>> 6)] &&& (1UL <<< (tile &&& 63)) <> 0UL
        member _.Support (cell: int) = supports.[cell]
        /// Entropy = Σ popcount over the cell's words.
        member _.Count   (cell: int) =
            let b = cell * nWords
            let mutable n = 0
            for w in 0 .. nWords - 1 do n <- n + BitOperations.PopCount words.[b + w]
            n
        member this.Tiles (cell: int) : int list =
            [ for t in 0 .. nTiles - 1 do if this.Has(cell, t) then yield t ]

// =====================================================================================
// Item 4: the search driver — POLICY, deliberately outside the engine. The engine's whole
// public surface for search is Mark/UndoTo/Collapse + the OnChange hook; everything below
// (min-entropy selection, the entropy heap, tile ordering, retry) is replaceable without
// touching module Wfc. The search monad is Hansei.TreeSearch: `guard` = an if-check that
// returns empty on fail, so a failed collapse contributes nothing and the enumeration IS
// the backtracking. Exhaustive (the list monad) enumerates every solution eagerly;
// TreeSearch.LazyList is the lazy twin whose cells are MEMOIZED — effects run at most once
// per node, so re-forcing a prefix is safe (a raw seq would re-run collapses on a second
// traversal). Both are strictly depth-first, which is what makes the mark/undo bracket on
// ONE mutable store coherent; a fair/interleaving strategy (Hansei.Backtracking) resumes
// suspended branches out of LIFO order and needs the step-3 persistent store instead.
// =====================================================================================

module WfcSearch =
    open Hansei
    open Hansei.FSharpx.Collections

    /// Lazy binary min-heap over packed (entropy <<< 32 ||| cell) keys, subscribed to the
    /// engine's OnChange hook. Entries go stale (cells change under them); PickMin validates
    /// on pop — recompute the entropy, discard if the cell is decided, reinsert at the true
    /// entropy on mismatch. Invariant: every undecided cell has at least one live entry,
    /// because every entropy transition (narrow OR undo-restore) pushes one.
    type EntropyHeap(engine: Wfc.Engine, nCells: int) =
        let mutable heap = Array.zeroCreate<uint64> 4096
        let mutable n = 0
        let push (k: uint64) =
            if n = heap.Length then
                let h2 = Array.zeroCreate (n * 2) in Array.blit heap 0 h2 0 n; heap <- h2
            let mutable i = n
            heap.[i] <- k
            n <- n + 1
            while i > 0 && heap.[(i - 1) / 2] > heap.[i] do
                let p = (i - 1) / 2
                let t = heap.[p] in heap.[p] <- heap.[i]; heap.[i] <- t
                i <- p
        let pop () =
            let k = heap.[0]
            n <- n - 1
            heap.[0] <- heap.[n]
            let mutable i = 0
            let mutable sifting = true
            while sifting do
                let l = 2 * i + 1
                let r = l + 1
                let mutable m = i
                if l < n && heap.[l] < heap.[m] then m <- l
                if r < n && heap.[r] < heap.[m] then m <- r
                if m = i then sifting <- false
                else
                    let t = heap.[m] in heap.[m] <- heap.[i]; heap.[i] <- t
                    i <- m
            k
        member _.Notify (c: int) = push ((uint64 (engine.Count c) <<< 32) ||| uint64 c)
        member h.Rebuild () =
            n <- 0
            for c in 0 .. nCells - 1 do h.Notify c
        /// Minimum-entropy undecided cell, or -1 when every cell is decided (= a solution,
        /// since the engine is quiesced and uncontradicted whenever the driver picks).
        member h.PickMin () : int =
            let mutable res = -2
            while res = -2 do
                if n = 0 then res <- -1
                else
                    let k = pop ()
                    let c = int (k &&& 0xFFFFFFFFUL)
                    let recorded = int (k >>> 32)
                    let current = engine.Count c
                    if current <= 1 then ()                   // decided: not a choice point
                    elif current <> recorded then h.Notify c  // stale: reinsert at true entropy
                    else res <- c
            res

    /// Min-entropy picker wired to the engine's observation hook.
    let heapPicker (engine: Wfc.Engine) (nCells: int) : unit -> int =
        let h = EntropyHeap(engine, nCells)
        engine.OnChange <- h.Notify
        h.Rebuild ()
        fun () -> h.PickMin ()

    /// First undecided cell in id order — dumb, deterministic, O(n) per pick. Exists to show
    /// the policy really is swappable (and for closed-form tests where order must be fixed).
    let scanPicker (engine: Wfc.Engine) (nCells: int) : unit -> int =
        fun () ->
            let mutable res = -1
            let mutable c = 0
            while res < 0 && c < nCells do
                if engine.Count c >= 2 then res <- c
                c <- c + 1
            res

    /// Collapsed grid -> tile per cell (call only when every cell is decided).
    let extract (engine: Wfc.Engine) (nCells: int) : int[] =
        Array.init nCells (fun c ->
            let d = engine.Domain c
            let mutable t = -1
            for w in 0 .. d.Length - 1 do
                if t < 0 && d.[w] <> 0UL then
                    t <- (w <<< 6) + BitOperations.TrailingZeroCount d.[w]
            t)

    /// Every cell decided and every E/S edge satisfies the rule (with a consistent table the
    /// W/N checks are the same edges read backward).
    let isValidSolution (width: int) (height: int) (rule: int -> int -> int -> bool) (sol: int[]) =
        let mutable ok = sol |> Array.forall (fun t -> t >= 0)
        for r in 0 .. height - 1 do
            for c in 0 .. width - 1 do
                let i = r * width + c
                if c < width - 1  then ok <- ok && rule 1 sol.[i] sol.[i + 1]
                if r < height - 1 then ok <- ok && rule 2 sol.[i] sol.[i + width]
        ok

    /// ALL solutions, eagerly — TreeSearch.Exhaustive IS the list monad. Each candidate try
    /// is bracketed: UndoTo(mark) runs BEFORE the collapse, so when a candidate's subtree is
    /// exhausted (or its collapse ⊥s) the next candidate starts from the pre-guess fixpoint.
    /// The guard's [] is the failure signal; the trail restore is what makes it true.
    let allSolutions (engine: Wfc.Engine) (nCells: int) (pick: unit -> int) (failures: int ref) : int[] list =
        let rec go () =
            TreeSearch.Exhaustive.search {
                let cell = pick ()
                if cell < 0 then return extract engine nCells
                else
                    let mark = engine.Mark ()
                    let! tile = TreeSearch.Exhaustive.choices (engine.Tiles cell)
                    engine.UndoTo mark
                    let ok = engine.Collapse (cell, tile)
                    if not ok then failures.Value <- failures.Value + 1
                    do! TreeSearch.Exhaustive.guard ok
                    return! go ()
            }
        go ()

    /// Lazily-enumerated solutions (list of successes) — same guard shape, memoized cells.
    /// LazyList.tryHead = first-success generation without exploring the rest of the tree;
    /// forcing the same prefix twice does NOT re-run engine effects (each cell's thunk runs
    /// once). Unforced tails still assume the engine is where DFS left it — don't touch the
    /// engine between forcings. `order` is the tile-choice policy (e.g. a seeded shuffle).
    let solutions (engine: Wfc.Engine) (nCells: int) (pick: unit -> int)
                  (order: int list -> int list) (failures: int ref) : LazyList<int[]> =
        let rec go () =
            TreeSearch.LazyList.search {
                let cell = pick ()
                if cell < 0 then return extract engine nCells
                else
                    let mark = engine.Mark ()
                    let! tile = TreeSearch.LazyList.choices (order (engine.Tiles cell))
                    engine.UndoTo mark
                    let ok = engine.Collapse (cell, tile)
                    if not ok then failures.Value <- failures.Value + 1
                    do! TreeSearch.LazyList.guard ok
                    return! go ()
            }
        go ()

    let shuffledBy (rng: Random) (xs: int list) = xs |> List.sortBy (fun _ -> rng.Next ())

    /// Guess-depth recursion lives on the stack while forcing (one bracket of frames per
    /// un-forced guess), so deep maps run on a dedicated big-stack thread. A cost of the
    /// list-of-successes driver on a mutable store; the step-3 persistent variant removes it.
    let runDeep (stackMB: int) (f: unit -> 'a) : 'a =
        let mutable result = Unchecked.defaultof<'a>
        let t = Threading.Thread((fun () -> result <- f ()), stackMB * 1024 * 1024)
        t.Start ()
        t.Join ()
        result

// =====================================================================================
// Verification — hand-computable grids with closed-form expected domains, checked exactly
// (A6 differential against the closed form, not against printed output). The ramp tileset
// (|a-b| <= 1 between neighbors) has T=100 so domains CROSS the 64-bit word seam; the
// staircase and gravity tilesets exercise the ⊥ early-exit and the direction wiring.
// =====================================================================================

// Ramp: adjacent cells' tiles differ by at most 1. From a pinned tile 0 at cell x, any cell
// at grid distance d has domain {0 .. d}; pin 99 at distance-99 cell y too and every cell on
// the line is forced to exactly its distance from x.
let rampRule (_: int) (a: int) (b: int) = abs (a - b) <= 1

let verifyRampSingle () =
    let T = 100
    let e = Wfc.Engine(100, 1, T, Wfc.buildAllowed T rampRule)
    let mutable ok = e.AssertTile(0, 0, 0) && not e.Contradiction && e.WorklistClean
    for k in 0 .. 99 do
        ok <- ok && e.Count k = k + 1                             // {0..k}, entropy k+1
              && e.Tiles k = [ 0 .. k ]
              && e.Support k = (if k <= 98 then 1UL else 0UL)     // cell 99: {0..99} = ⊤, never
                                                                  // narrowed -> no support (OR-on-narrow)
    printfn "  pin 0 at cell 0: count(63)=%d count(64)=%d (word seam), count(99)=%d, support(99)=%dUL"
        (e.Count 63) (e.Count 64) (e.Count 99) (e.Support 99)
    ok

let verifyRampPinned () =
    let T = 100
    let e = Wfc.Engine(100, 1, T, Wfc.buildAllowed T rampRule)
    let mutable ok = e.AssertTile(0, 0, 0) && e.AssertTile(1, 99, 99) && not e.Contradiction
    for k in 0 .. 99 do
        ok <- ok && e.Tiles k = [ k ]                             // fully forced chain
              && e.Support k = (if k = 0 then 1UL elif k = 99 then 2UL else 3UL)
    printfn "  pins 0@0 + 99@99: every cell k forced to {k}; support(0)=%dUL support(50)=%dUL support(99)=%dUL"
        (e.Support 0) (e.Support 50) (e.Support 99)
    ok

let verifyRampSiteBot () =
    // Conflicting pin: after the 0-pin, cell 2 holds {0,1,2}; asserting {50} empties it AT the
    // assert site. The ⊥ leaves the worklist clean and further Asserts refuse.
    let T = 100
    let e = Wfc.Engine(3, 1, T, Wfc.buildAllowed T rampRule)
    let ok = e.AssertTile(0, 0, 0)
             && not (e.AssertTile(1, 2, 50))
             && e.Contradiction && e.BotCell = 2 && e.Count 2 = 0 && e.WorklistClean
             && not (e.AssertTile(2, 1, 1))                       // contradicted store refuses
    printfn "  pin 0@0 then 50@2: contradiction at cell %d, worklist clean = %b" e.BotCell e.WorklistClean
    ok

let verifyStaircaseMidWaveBot () =
    // Staircase: east neighbor = my tile + 1 (so tile 2, the top step, admits NO east
    // neighbor — its allowed[E] row is empty). Asserting tile 2 mid-line is fine at the
    // assert site but ⊥s its EAST NEIGHBOR during propagation — this exercises the
    // early-exit INSIDE quiesce (not the assert-site path), including the abort before the
    // remaining pushes (cell 0 must stay untouched at ⊤).
    let T = 3
    let rule d a b =
        match d with
        | 1 -> b = a + 1        // E: exact successor
        | 3 -> b = a - 1        // W: exact predecessor (the consistent inverse)
        | _ -> true             // N/S unconstrained (height 1 anyway)
    let mutable ok = Wfc.checkConsistent T rule
    let e = Wfc.Engine(3, 1, T, Wfc.buildAllowed T rule)
    ok <- ok && not (e.AssertTile(0, 1, 2))
             && e.Contradiction && e.BotCell = 2 && e.Count 2 = 0
             && e.WorklistClean
             && e.Tiles 0 = [ 0; 1; 2 ]                           // abort fired before the W-push
    printfn "  tile 2 (top step) @ cell 1: ⊥ at east neighbor (cell %d) mid-wave; cell 0 untouched = %b"
        e.BotCell (e.Tiles 0 = [ 0; 1; 2 ])
    // Reset must return the store to usable ⊤ everywhere:
    e.Reset ()
    ok <- ok && not e.Contradiction
             && e.AssertTile(0, 0, 0) && e.Tiles 1 = [ 1 ] && e.Tiles 2 = [ 2 ]
    printfn "  after Reset, pin 0@0 forces the staircase: cell1=%A cell2=%A" (e.Tiles 1) (e.Tiles 2)
    ok

let verifyGravity () =
    // Direction-ASYMMETRIC tileset to catch N/S wiring bugs: Air=0, Ground=1; no floating
    // ground (above Air only Air; below Ground only Ground); E/W unconstrained.
    let T = 2
    let rule d (a: int) (b: int) =
        match d with
        | 0 -> a = 1 || b = 0   // b sits ABOVE a: lower Air forces upper Air
        | 2 -> a = 0 || b = 1   // b sits BELOW a: upper Ground forces lower Ground
        | _ -> true
    let mutable ok = Wfc.checkConsistent T rule
    let e = Wfc.Engine(4, 4, T, Wfc.buildAllowed T rule)
    let cid r c = r * 4 + c
    ok <- ok && e.AssertTile(0, cid 2 1, 0)   // Air at (2,1): forces (1,1),(0,1) Air; (3,1) free
             && e.AssertTile(1, cid 1 2, 1)   // Ground at (1,2): forces (2,2),(3,2) Ground; (0,2) free
    ok <- ok && e.Tiles (cid 1 1) = [ 0 ] && e.Tiles (cid 0 1) = [ 0 ]
             && e.Tiles (cid 3 1) = [ 0; 1 ]
             && e.Tiles (cid 2 2) = [ 1 ] && e.Tiles (cid 3 2) = [ 1 ]
             && e.Tiles (cid 0 2) = [ 0; 1 ]
             && e.Tiles (cid 0 0) = [ 0; 1 ]                      // untouched column
             && e.Support (cid 0 1) = 1UL && e.Support (cid 3 2) = 2UL
             && e.Support (cid 3 1) = 0UL && e.Support (cid 0 0) = 0UL
    printfn "  Air@(2,1): column above forced Air, below free; Ground@(1,2): column below forced Ground, above free — %b" ok
    ok

let gravityRule d (a: int) (b: int) =
    match d with 0 -> a = 1 || b = 0 | 2 -> a = 0 || b = 1 | _ -> true

let verifyTrailUndo () =
    // Trail MECHANISM, no search monad involved: nested marks must restore words AND supports
    // exactly (snapshot equality, not just re-quiescence), a ⊥ unwind must leave a usable
    // store, and the restored store must still propagate (no dead queued[] flags).
    let e = Wfc.Engine(4, 4, 2, Wfc.buildAllowed 2 gravityRule)
    let snap () = Array.init 16 (fun c -> e.Domain c), Array.init 16 (fun c -> e.Support c)
    let eq (d0: uint64[][], s0: uint64[]) =
        let d1, s1 = snap ()
        Array.forall2 (fun (a: uint64[]) b -> a = b) d0 d1 && s0 = s1
    // base state holds an AUTHORED pin (premise 3) so undo must preserve real support bits
    let mutable ok = e.AssertTile(3, 13, 0)          // Air@(3,1): forces column 1 Air above
    let s0 = snap ()
    let m1 = e.Mark ()
    ok <- ok && e.Collapse(12, 0)                    // guess Air@(3,0): forces column 0 above
    let s1 = snap ()
    let m2 = e.Mark ()
    ok <- ok && e.Collapse(3, 1)                     // guess Ground@(0,3): forces column 3 below
    ok <- ok && not (eq s1)
    e.UndoTo m2
    ok <- ok && eq s1 && e.WorklistClean && not e.Contradiction
    e.UndoTo m1
    ok <- ok && eq s0 && e.WorklistClean
    // ⊥ at a guess, then unwind back to the same fixpoint
    let m3 = e.Mark ()
    ok <- ok && e.Collapse(0, 0) && not (e.Collapse(0, 1))   // {0} ∩ {1} = ∅
    ok <- ok && e.Contradiction
    e.UndoTo m3
    ok <- ok && not e.Contradiction && eq s0 && e.WorklistClean
    ok <- ok && e.Collapse(15, 1) && e.Tiles 15 = [ 1 ]      // still propagates after unwind
    printfn "  nested mark/undo restores words+supports exactly; ⊥ unwind leaves a usable store — %b" ok
    ok

let verifyStaircaseSearch () =
    // Staircase 1xN as a SEARCH closed form: solutions = max(0, T-N+1). T=3, N=3 -> exactly
    // one ([|0;1;2|]) with exactly 2 failed guesses at the first cell (tiles 1 and 2 dead-end
    // mid-wave); N=4 -> UNSAT: 0 solutions, exactly 3 failed guesses. Failure counts are
    // deterministic under the scan picker, so the unwind path is verified, not just exercised.
    let rule d (a: int) b =
        match d with 1 -> b = a + 1 | 3 -> b = a - 1 | _ -> true
    let e3 = Wfc.Engine(3, 1, 3, Wfc.buildAllowed 3 rule)
    let f3 = ref 0
    let sols3 = WfcSearch.allSolutions e3 3 (WfcSearch.scanPicker e3 3) f3
    let e4 = Wfc.Engine(4, 1, 3, Wfc.buildAllowed 3 rule)
    let f4 = ref 0
    let sols4 = WfcSearch.allSolutions e4 4 (WfcSearch.scanPicker e4 4) f4
    let ok = sols3 = [ [| 0; 1; 2 |] ] && f3.Value = 2
             && sols4 = [] && f4.Value = 3
    printfn "  1x3: %d solution(s), %d failed guesses (expect 1, 2); 1x4 unsat: %d, %d (expect 0, 3)"
        sols3.Length f3.Value sols4.Length f4.Value
    ok

let verifyExhaustiveGravity () =
    // Gravity on w x h: columns are independent and each is an Air-prefix over a
    // Ground-suffix -> (h+1)^w solutions; 3x3 -> 64. And because columns are PATHS (trees),
    // arc consistency is complete here: every candidate in a quiesced domain extends, so the
    // failed-guess count must be exactly 0. Run under the HEAP picker: the count is
    // order-independent, so this also checks the heap never loses an undecided cell across
    // the enumeration's many unwinds.
    let e = Wfc.Engine(3, 3, 2, Wfc.buildAllowed 2 gravityRule)
    let failures = ref 0
    let sols = WfcSearch.allSolutions e 9 (WfcSearch.heapPicker e 9) failures
    let ok = sols.Length = 64
             && (List.distinct sols).Length = 64
             && List.forall (WfcSearch.isValidSolution 3 3 gravityRule) sols
             && failures.Value = 0
    printfn "  3x3 gravity: %d solutions (closed form 4^3 = 64), all valid+distinct, failed guesses = %d (AC complete on paths)"
        sols.Length failures.Value
    ok

let verifyCheckerboard () =
    // T=2, a<>b in all directions: any grid has exactly 2 solutions (the two parity
    // colorings). Scan picker; one root guess forces the whole grid either way.
    let rule (_: int) (a: int) (b: int) = a <> b
    let mutable ok = Wfc.checkConsistent 2 rule
    let e = Wfc.Engine(3, 3, 2, Wfc.buildAllowed 2 rule)
    let failures = ref 0
    let sols = WfcSearch.allSolutions e 9 (WfcSearch.scanPicker e 9) failures
    ok <- ok && sols.Length = 2 && (List.distinct sols).Length = 2
             && List.forall (WfcSearch.isValidSolution 3 3 rule) sols
             && failures.Value = 0
    printfn "  3x3 checkerboard: %d solutions (closed form 2), failed guesses = %d" sols.Length failures.Value
    ok

let verifyFirstSolutionLazy () =
    // 12x12 3-coloring via the LAZY driver: min-entropy heap + seeded shuffle, take ONE map.
    // Then force the head AGAIN: the memoized cell must return the SAME array (reference
    // equality) — proof that re-forcing a prefix does not re-run engine effects, which is
    // the property a raw seq lacks.
    let rule (_: int) (a: int) (b: int) = a <> b
    let e = Wfc.Engine(12, 12, 3, Wfc.buildAllowed 3 rule)
    let rng = Random 42
    let failures = ref 0
    let sols = WfcSearch.solutions e 144 (WfcSearch.heapPicker e 144) (WfcSearch.shuffledBy rng) failures
    let first  = Hansei.FSharpx.Collections.LazyList.tryHead sols
    let again  = Hansei.FSharpx.Collections.LazyList.tryHead sols
    let ok = match first, again with
             | Some a, Some b ->
                 WfcSearch.isValidSolution 12 12 rule a && obj.ReferenceEquals(a, b)
             | _ -> false
    printfn "  12x12 3-color first map: valid = %b, memoized re-force = same array = %b, failed guesses = %d"
        (match first with Some a -> WfcSearch.isValidSolution 12 12 rule a | None -> false)
        (match first, again with Some a, Some b -> obj.ReferenceEquals(a, b) | _ -> false)
        failures.Value
    ok

// =====================================================================================
// Timing — first 500x500 measurements (recorded in docs/benchmarks.md). Engines are built
// once; each iteration = Reset + Assert + propagate to quiescence, so the Reset cost is
// inside every row — the reset-only rows are there to subtract it out. The ramp rows are
// the dense-⊤ worst case for the union loop (cost ∝ popcount of the firing cell); real WFC
// firing cells are near-collapsed, so read these as an upper bound, not the driver-era cost.
// =====================================================================================

/// best-of-`trials` mean milliseconds per run; also returns the across-trials mean.
let bench (name: string) (trials: int) (iters: int) (warmup: int) (f: unit -> unit) =
    for _ in 1 .. warmup do f ()
    let mutable best = Double.MaxValue
    let mutable sum  = 0.0
    for _ in 1 .. trials do
        GC.Collect(); GC.WaitForPendingFinalizers()
        let sw = Stopwatch.StartNew()
        for _ in 1 .. iters do f ()
        sw.Stop()
        let perMs = sw.Elapsed.TotalMilliseconds / float iters
        best <- min best perMs
        sum  <- sum + perMs
    printfn "  %-18s best %10.3f ms/run   mean %10.3f ms/run   (%d trials x %d iters)"
        name best (sum / float trials) trials iters
    best

let main () =
    printfn "runtime: %s" (Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn ""
    printfn "== ramp 1x100, single pin (T=100, W=2: domains cross the word seam) =="
    let ok1 = verifyRampSingle ()
    printfn "== ramp 1x100, both ends pinned (forced chain + OR-on-narrow supports) =="
    let ok2 = verifyRampPinned ()
    printfn "== ramp 1x3, conflicting pin (⊥ at the assert site) =="
    let ok3 = verifyRampSiteBot ()
    printfn "== staircase 1x3 (⊥ mid-propagation: early-exit + drain/clear + Reset) =="
    let ok4 = verifyStaircaseMidWaveBot ()
    printfn "== gravity 4x4 (direction-asymmetric rule: N/S wiring) =="
    let ok5 = verifyGravity ()
    printfn "== trail: nested mark/undo, ⊥ unwind, exact word+support restore =="
    let ok6 = verifyTrailUndo ()
    printfn "== search: staircase 1x3 / 1x4 (closed-form solution AND failure counts) =="
    let ok7 = verifyStaircaseSearch ()
    printfn "== search: gravity 3x3 exhaustive = 4^3 under the entropy heap =="
    let ok8 = verifyExhaustiveGravity ()
    printfn "== search: checkerboard 3x3 = 2 parity colorings (scan picker) =="
    let ok9 = verifyCheckerboard ()
    printfn "== search: lazy first-solution, memoized re-force (LazyList) =="
    let ok10 = verifyFirstSolutionLazy ()
    printfn ""
    let ok = ok1 && ok2 && ok3 && ok4 && ok5 && ok6 && ok7 && ok8 && ok9 && ok10
    printfn "correctness: %s"
        (if ok then "PASS (store/propagator: ramp single/pinned/site-⊥, staircase mid-wave ⊥, gravity dirs; \
                     search: trail undo, staircase counts, gravity 64, checkerboard 2, lazy memoized)"
         else sprintf "FAIL (rampSingle=%b rampPinned=%b siteBot=%b staircase=%b gravity=%b trail=%b stairSearch=%b gravity64=%b checker=%b lazy=%b)"
                  ok1 ok2 ok3 ok4 ok5 ok6 ok7 ok8 ok9 ok10)
    printfn ""

    if not ok then
        eprintfn "Refusing to time a wrong engine."
        1
    else
        let envInt name dflt =
            match Environment.GetEnvironmentVariable name with
            | null | "" -> dflt
            | s -> int s
        let trials = envInt "TRIALS" 3
        let side = 500

        // T=512 (W=8): pin the corner -> wave narrows every cell at distance < 511 (~131k
        // cells, each carrying up to 512 live bits while the wave is dense).
        let t512 = 512
        let e512 = Wfc.Engine(side, side, t512, Wfc.buildAllowed t512 rampRule)
        let run512 () = e512.Reset (); e512.AssertTile(0, 0, 0) |> ignore
        run512 ()
        let okB1 = not e512.Contradiction
                   && e512.Count 0 = 1 && e512.Count 10 = 11
                   && e512.Count (250 * side + 250) = 501           // distance 500 -> {0..500}
                   && e512.Count (side * side - 1) = 512            // distance 998: unnarrowed ⊤

        // T=128 (W=2): pin the center -> radius-127 diamond (~32k cells narrowed).
        let t128 = 128
        let e128 = Wfc.Engine(side, side, t128, Wfc.buildAllowed t128 rampRule)
        let center = 250 * side + 250
        let run128 () = e128.Reset (); e128.AssertTile(0, center, 0) |> ignore
        run128 ()
        let okB2 = not e128.Contradiction
                   && e128.Count center = 1 && e128.Count (center + 1) = 2
                   && e128.Count 0 = 128                            // distance 500: unnarrowed ⊤

        // Gravity T=2 (W=1): pin Air at the bottom of one column -> forces 499 cells up one
        // column. The wave is tiny, so this row is dominated by Reset — subtract the
        // reset-only row to read the local-wave cost.
        let eGrav = Wfc.Engine(side, side, 2, Wfc.buildAllowed 2 gravityRule)
        let bottom = 499 * side + 250
        let runGrav () = eGrav.Reset (); eGrav.AssertTile(0, bottom, 0) |> ignore
        runGrav ()
        let okB3 = not eGrav.Contradiction
                   && eGrav.Count 250 = 1 && eGrav.Tiles 250 = [ 0 ]
                   && eGrav.Count 251 = 2

        if not (okB1 && okB2 && okB3) then
            eprintfn "Refusing to time: 500x500 spot checks failed (ramp512=%b ramp128=%b gravity=%b)" okB1 okB2 okB3
            1
        else
            printfn "== timing, 500x500 (reset + assert + propagate to quiescence, per run) =="
            bench "reset-only W=8"   trials 10 3   (fun () -> e512.Reset ())  |> ignore
            bench "ramp512 corner"   trials 1  1   run512                     |> ignore
            bench "ramp128 center"   trials 3  1   run128                     |> ignore
            bench "reset-only W=1"   trials 20 5   (fun () -> eGrav.Reset ()) |> ignore
            bench "gravity column"   trials 20 5   runGrav                    |> ignore

            // --- items 3+4 end-to-end: full-map generation = Reset + heap rebuild + FIRST
            // solution off the lazy driver. Seeded rng per run -> identical work every
            // iteration. Each row runs on a big-stack thread: forcing holds one bracket of
            // frames per guess (stack ∝ guesses, not cells).
            let genOnce (e: Wfc.Engine) (nCells: int) (seed: int) (failures: int ref) =
                e.Reset ()
                let pick = WfcSearch.heapPicker e nCells
                WfcSearch.solutions e nCells pick (WfcSearch.shuffledBy (Random seed)) failures
                |> Hansei.FSharpx.Collections.LazyList.tryHead

            let colorRule (_: int) (a: int) (b: int) = a <> b
            let e3c     = Wfc.Engine(side, side, 3, Wfc.buildAllowed 3 colorRule)
            let eRamp32 = Wfc.Engine(64, 64, 32, Wfc.buildAllowed 32 rampRule)
            let fG, f3, fR = ref 0, ref 0, ref 0
            let sG = WfcSearch.runDeep 1024 (fun () -> genOnce eGrav (side * side) 42 fG)
            let s3 = WfcSearch.runDeep 1024 (fun () -> genOnce e3c (side * side) 42 f3)
            let sR = WfcSearch.runDeep 1024 (fun () -> genOnce eRamp32 (64 * 64) 42 fR)
            let okG = match sG with Some s -> WfcSearch.isValidSolution side side gravityRule s | None -> false
            let ok3 = match s3 with Some s -> WfcSearch.isValidSolution side side colorRule s | None -> false
            let okR = match sR with Some s -> WfcSearch.isValidSolution 64 64 rampRule s | None -> false
            if not (okG && ok3 && okR) then
                eprintfn "Refusing to time: generation spot checks failed (gravity=%b 3color=%b ramp32=%b)" okG ok3 okR
                1
            else
                printfn ""
                printfn "== timing, full-map generation (first solution: min-entropy heap + seeded shuffle) =="
                printfn "   failed guesses on the spot-check runs: gravity=%d, 3color=%d, ramp32=%d" fG.Value f3.Value fR.Value
                WfcSearch.runDeep 1024 (fun () ->
                    bench "gravity gen 500x500" trials 2 1 (fun () -> genOnce eGrav (side * side) 42 (ref 0) |> ignore)) |> ignore
                WfcSearch.runDeep 1024 (fun () ->
                    bench "3color gen 500x500"  trials 1 1 (fun () -> genOnce e3c (side * side) 42 (ref 0) |> ignore)) |> ignore
                WfcSearch.runDeep 1024 (fun () ->
                    bench "ramp32 gen 64x64"    trials 2 1 (fun () -> genOnce eRamp32 (64 * 64) 42 (ref 0) |> ignore)) |> ignore
                0

exit (main ())
