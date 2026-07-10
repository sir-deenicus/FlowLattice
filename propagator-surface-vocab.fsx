(*  propagator-surface-vocab.fsx

    The two-faces surface: one authored Model, two lowerings, and the differential harness that
    proves they agree. Design: docs/propagator-surface-design.md (§5 vocabulary, §7 decided,
    §10 Codex + Fable review).

    Started life (2026-07-05) as Codex build-order step 1 — "write the concrete F# signatures
    first" — a signatures-only skeleton with every body a `failwith` stub. This file now FILLS
    those stubs for the lowering + solveBoth work (2026-07-06):
      - fork A  RATIFIED (Deen, 2026-07-05): no mechanism port. The general consume verbs
                lower to dependency-directed backtracking on the array core as-is (guess = Assert
                under a fresh premise; backtrack = Retract of it). Both faces expose `solve`;
                only the lowerings differ.
      - fork B  DECIDED (D14): the premise ceiling is a per-backend REPRESENTATION choice. Premise
                handles hide width; no construct below names a word.
      - fork C  RATIFIED (Deen, 2026-07-05): write-once. One authored Model, two lowerings.
                Scoped: PORTABLE constructs are authored once, not all constructs are portable.

    THE WRITE-ONCE HEART (fork C made real). A portable finite `RelationBox` carries one
    membership predicate `allows : 'a list -> bool`. Both lowerings interpret it through the SAME
    generalized-arc-consistency pass (`Gac.narrow`, below) — authored once, plugged into two reps
    (a caller-supplied `FiniteRep` on General, a `uint64` word on Optimized). Identical narrowing => identical fixpoint =>
    identical solution; that agreement is exactly what `Differential.solveBoth` asserts.

    solveBoth is the TEST HARNESS, not the shipped surface (docs §7.8, Deen 2026-07-06). A consumer
    picks a face and never calls it; it lowers to BOTH and diffs, so it belongs with the tests.

    Sharpening 1 (Fable): capability witnesses are ordinary lowering RETURN TYPES
      (Result<_, UnsupportedConstruct list>) — legality is visible before any engine is built. The
      C<->F slice proves it: `Optimized.lower` returns [RichLatticeOnOptimized; DataflowOnOptimized].

    Core/outer boundary (Deen, 2026-07-05): the line is the `#r` — everything here is pure F# (core).
      The two engines are copied in verbatim (the repo pattern: literate files carry their engine, they
      do not `#load` a file that ends in `exit (main())`).

    Still stubbed (the observation/edit seams — NOT this slice's scope): `solutions` / `generate` /
    `onCell` / `onNet` / `assume` / `retract`. They are the deferred observation slice (docs §9, §12).

    Guardrail fix (Codex review, 2026-07-06): lowerings now reject every unsupported construct/domain
    combination as an `UnsupportedConstruct` value instead of silently ignoring it, and the optimized
    proof backend refuses >64-value domains or premise/search-depth requirements that exceed its one-word
    support representation. Negative harness rows lock those failures in.

    Review-gate fix (Codex, 2026-07-10): both DDB drivers reuse failed guess premises in LIFO order, making
    the lower-time `givens + cells` premise bound true at runtime. A sparse-givens differential slice now
    proves that search is required, both faces agree, and the authored puzzle is uniquely solvable by an
    independent exhaustive enumerator.

      dotnet fsi propagator-surface-vocab.fsx  *)


open System.Collections.Generic


// ---- Nouns ---------------------------------------------------------------

/// A phantom-typed, opaque cell handle. 'a tags the value domain but is not stored.
type Cell<'a> = { id: int; name: string }

/// An opaque premise handle. Hides allocation AND width (fork B / D14): no vocabulary
/// construct names the underlying word, so the ceiling stays a per-backend representation
/// choice and never leaks into the contract.
type Premise = { pid: int }

/// Meet-semilattice operations for a rich domain (general-only). `top` is the open value a
/// cell starts at (as-built delta 2026-07-06: the closure engine needs it to initialize cells).
type LatticeOps<'a> =
    { top: 'a
      meet: 'a -> 'a -> 'a
      isBottom: 'a -> bool
      equals: 'a -> 'a -> bool }

/// The operations General needs from a finite-domain representation. The representation state is
/// deliberately separate from the authored value type and is hidden again by `GeneralNet.run`.
type FiniteRep<'value, 'state> =
    { top: 'state
      ofValues: 'value list -> 'state
      singleton: 'value -> 'state
      meet: 'state -> 'state -> 'state
      candidates: 'state -> 'value list
      isBottom: 'state -> bool
      isSingleton: 'state -> bool
      equals: 'state -> 'state -> bool }

/// A value domain. The portability class is visible in the case:
///   Finite  — rep-agnostic, PORTABLE (supplied General rep / optimized bitset).
///   Lattice — rich meet (interval, scalar), FRIENDLY-ONLY by construction.
type Domain<'a> =
    | Finite of 'a list
    | Lattice of LatticeOps<'a>


// ---- Spine (constraint boxes) -------------------------------------------

/// A portable finite relation: a membership predicate over a tuple of cell values.
/// Both faces compile it through the shared `Gac.narrow` (general filters a Set, optimized
/// filters a word) — this is the write-once construct.
type RelationBox<'a> =
    { cells: Cell<'a> list
      allows: 'a list -> bool }

/// A general-only rich dataflow relation (convert / sum / equal over lattices):
/// a directional narrowing that needs the lattice meet machinery. `narrow` returns each cell's
/// freshly-derived value; the engine meets it in.
type DataflowBox<'a> =
    { cells: Cell<'a> list
      narrow: 'a list -> 'a list }

/// A constraint box. Portability class is visible in the case:
///   Relation → portable    Dataflow → general-only
type Constraint<'a> =
    | Relation of RelationBox<'a>
    | Dataflow of DataflowBox<'a>

/// The authored model — written ONCE, lowered to either face (fork C).
/// `givens` are the instance's initial assignments (as-built delta 2026-07-06: a CSP instance =
/// relations + clues; the clues are what drive propagation, so the Model must carry them).
/// Slice-0 is monomorphic in the cell value type; heterogeneous domains are deferred.
type Model<'a> =
    { domain: Domain<'a>
      cells: Cell<'a> list
      constraints: Constraint<'a> list
      givens: (Cell<'a> * 'a) list }


// ---- Capability witnesses (sharpening 1: ordinary values, not type-level proofs) --

/// Why a model cannot lower onto a given face — returned as DATA, naming the offender.
/// Legality is thereby visible from the lowering's result before any engine is built.
type UnsupportedConstruct =
    | RichLatticeOnOptimized
    | DataflowOnOptimized
    | RelationRequiresFiniteDomain
    | DataflowRequiresRichLattice
    | FiniteDomainTooWideForOptimized of needed: int * width: int
    | PremiseWidthExceeded of needed: int * width: int   // fork B overflow rides this same channel


// ---- Results of lowering: the two nets ----------------------------------

/// An assignment of each cell to a value in its domain.
type Solution<'a> = Map<Cell<'a>, 'a>

/// A lowered FRIENDLY network (≈ the closure engine dressed): clear rep, closure props, rich
/// lattices. Search = dependency-directed backtracking (fork A). `run` is the built, ready-to-solve
/// engine captured as a closure; `General.solve` just calls it.
type GeneralNet<'a> = { model: Model<'a>; run: unit -> Solution<'a> option }

/// A lowered OPTIMIZED network (≈ the array core dressed): flat-word rep, array-backed store.
/// Same verb, different machinery (D2). `run` captures the built array-core engine.
type OptimizedNet<'a> = { model: Model<'a>; run: unit -> Solution<'a> option }

/// A change event on a cell — the unit of the observation seam. A DU, not a bare before->after,
/// because DDB and the trail WIDEN on backtrack and interactive edit wants restores. The payload is
/// the rep-agnostic domain projection (remaining candidates as `'a list`), not the resolved singleton
/// `'a` and not the backend rep; the exact projection type is the one open payload detail (§12).
type CellChange<'a> =
    | Narrowed of cell: Cell<'a> * before: 'a list * after: 'a list
    | Restored of cell: Cell<'a> * before: 'a list * after: 'a list
    | Resolved of cell: Cell<'a> * value: 'a
    | Contradiction of cell: Cell<'a>


// ---- Authoring (companion modules for the vocabulary nouns) -------------

module Domain =

    /// A portable finite domain. Duplicate values are invalid because each lowering must assign
    /// one unambiguous representation slot to each authored value.
    let finite (values: 'a list) : Domain<'a> =
        if List.length (List.distinct values) <> List.length values then
            invalidArg "values" "finite domain values must be unique"
        Finite values

    /// A general-only rich domain from its open top, meet, and bottom test.
    let lattice (top: 'a) (meet: 'a -> 'a -> 'a) (isBottom: 'a -> bool) : Domain<'a> =
        Lattice { top = top; meet = meet; isBottom = isBottom; equals = (=) }

module FiniteRep =

    /// Friendly default layered over the generic representation contract. Candidate projection
    /// follows the authored domain order rather than the Set comparison order.
    let set (values: 'value list) : FiniteRep<'value, Set<'value>> when 'value : comparison =
        let order = List.toArray values
        { top = Set.ofList values
          ofValues = Set.ofList
          singleton = Set.singleton
          meet = Set.intersect
          candidates = fun state ->
              [ for value in order do
                    if Set.contains value state then yield value ]
          isBottom = Set.isEmpty
          isSingleton = fun state -> Set.count state = 1
          equals = (=) }

module Cell =

    let mutable private nextId = 0

    /// Allocate a fresh named cell handle.
    let create (name: string) : Cell<'a> =
        let id = nextId
        nextId <- id + 1
        { id = id; name = name }

module Constraint =

    /// A portable finite relation over cells (membership predicate).
    let relation (cells: Cell<'a> list) (allows: 'a list -> bool) : Constraint<'a> =
        Relation { cells = cells; allows = allows }

    /// A general-only rich dataflow relation.
    let dataflow (cells: Cell<'a> list) (narrow: 'a list -> 'a list) : Constraint<'a> =
        Dataflow { cells = cells; narrow = narrow }

// ---- A rich lattice value type used by the general-only slice (verbatim) ----
// Copied from propagator-friendly.fsx / propagator-number-types.fsx §4: meet = intersection,
// Bot = Empty, outward-rounded arithmetic. Only the C<->F slice uses it.

type Interval = Empty | Iv of float * float

module Interval =
    let entire = Iv(System.Double.NegativeInfinity, System.Double.PositiveInfinity)
    let pt (x: float) = Iv(x, x)
    let private down (x: float) = if System.Double.IsFinite x then System.Math.BitDecrement x else x
    let private up   (x: float) = if System.Double.IsFinite x then System.Math.BitIncrement x else x
    let meet a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | Iv(a0, a1), Iv(b0, b1) ->
            let lo, hi = max a0 b0, min a1 b1
            if lo > hi then Empty else Iv(lo, hi)
    let add a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | Iv(a0, a1), Iv(b0, b1) -> Iv(down (a0 + b0), up (a1 + b1))
    let sub a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | Iv(a0, a1), Iv(b0, b1) -> Iv(down (a0 - b1), up (a1 - b0))
    let mul a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | Iv(a0, a1), Iv(b0, b1) ->
            let ps = [ a0 * b0; a0 * b1; a1 * b0; a1 * b1 ]
            Iv(down (List.min ps), up (List.max ps))
    let div a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | _, Iv(b0, b1) when b0 <= 0.0 && 0.0 <= b1 -> entire
        | Iv(a0, a1), Iv(b0, b1) ->
            let qs = [ a0 / b0; a0 / b1; a1 / b0; a1 / b1 ]
            Iv(down (List.min qs), up (List.max qs))


// ---- Backend engines (the two lowering targets, copied and adapted) -----
// Their internal `Cell` differs from the public handle above, so each is nested and private.

/// The FRIENDLY backend: the closure `Engine<'a>` of tutorial-propagation-part1.fsx (its 7a-7e),
/// with one integration delta: lattice-supplied equality replaces the source engine's `'a : equality`
/// constraint and generic comparisons. Clear rep, closure props, premises + Retract.
module private Closure =

    type Premise = int
    type Origin = Ext of Premise | Prop of int
    type Contribution<'a> = { value: 'a; support: Set<Premise> }

    type Cell<'a> =
        { id: int
          contribs: Dictionary<Origin, Contribution<'a>>
          mutable value: 'a
          mutable support: Set<Premise> }

    type Lattice<'a> =
        { top: 'a
          meet: 'a -> 'a -> 'a
          equals: 'a -> 'a -> bool }

    type Propagator<'a> =
        { pid: int
          reads: Cell<'a> list
          fire: unit -> (Cell<'a> * Contribution<'a>) list }

    type Engine<'a>(L: Lattice<'a>) =
        let cells = ResizeArray<Cell<'a>>()
        let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()
        let mutable nCell = 0
        let mutable nProp = 0

        member private _.Recompute (c: Cell<'a>) =
            let mutable v = L.top
            let mutable s = Set.empty
            for kv in c.contribs do
                v <- L.meet v kv.Value.value
                if not (L.equals kv.Value.value L.top) then s <- Set.union s kv.Value.support
            c.value <- v; c.support <- s

        member private this.Quiesce (frontier: seq<Cell<'a>>) =
            let q = Queue<Propagator<'a>>()
            let wake (c: Cell<'a>) = for p in watch.[c.id] do q.Enqueue p
            Seq.iter wake frontier
            while q.Count > 0 do
                let p = q.Dequeue()
                for (target, fact) in p.fire () do
                    let before = target.value
                    target.contribs.[Prop p.pid] <- fact
                    this.Recompute target
                    if not (L.equals target.value before) then wake target

        member _.NewCell () =
            let c = { id = nCell; contribs = Dictionary(); value = L.top; support = Set.empty }
            nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

        member _.AddProp (reads, fire) =
            let p = { pid = nProp; reads = reads; fire = fire }
            nProp <- nProp + 1
            for c in reads do watch.[c.id].Add p
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
                    for k in dead do c.contribs.Remove k |> ignore
                    this.Recompute c
                    touched.Add c
            this.Quiesce touched

/// The OPTIMIZED backend: `module M`'s array-backed engine from propagator-mutable-core.fsx,
/// with two integration deltas: premise ids are range-checked, and `Seal` exposes the existing one-time
/// freeze so zero-given models can finish setup before search reads the arrays. Structure-of-arrays,
/// uint64 premise bitmask, one reused emit callback.
module private ArrayCore =

    type Lattice<'a> =
        { top: 'a
          meet: 'a -> 'a -> 'a
          isBot: 'a -> bool
          equals: 'a -> 'a -> bool }

    type Emit<'a> = int -> 'a -> uint64 -> unit

    type Engine<'a>(L: Lattice<'a>) =
        let watchB = ResizeArray<ResizeArray<int>>()
        let firesB = ResizeArray<Emit<'a> -> unit>()
        let exts   = Dictionary<struct(int * int), 'a>()
        let mutable nCell = 0
        let mutable nProp = 0
        let mutable values   : 'a[]     = [||]
        let mutable supports : uint64[] = [||]
        let mutable watch    : int[][]  = [||]
        let mutable fires    : (Emit<'a> -> unit)[] = [||]
        let mutable queued   : bool[]   = [||]
        let mutable frozen   = false
        let q = Queue<int>()

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

        member this.Seal () = this.Freeze ()

        member _.Value   (c: int) = values.[c]
        member _.Support (c: int) = supports.[c]
        member _.IsBot   (c: int) = L.isBot values.[c]

        member this.Assert (p: int, c: int, v: 'a) =
            if p < 0 || p > 63 then invalidArg "p" "premise bitmask caps at 64"
            this.Freeze ()
            exts.[struct(p, c)] <- v
            emit c v (1UL <<< p)
            quiesce ()

        member this.Retract (p: int) =
            if p < 0 || p > 63 then invalidArg "p" "premise bitmask caps at 64"
            this.Freeze ()
            let bit = 1UL <<< p
            let dead =
                [ for kv in exts do
                    let struct(pr, _) = kv.Key
                    if pr = p then yield kv.Key ]
            for k in dead do exts.Remove k |> ignore
            for c in 0 .. nCell - 1 do
                if supports.[c] &&& bit <> 0UL then
                    values.[c]   <- L.top
                    supports.[c] <- 0UL
            for kv in exts do
                let struct(pr, cc) = kv.Key
                values.[cc]   <- L.meet values.[cc] kv.Value
                supports.[cc] <- supports.[cc] ||| (1UL <<< pr)
            for pid in 0 .. nProp - 1 do
                if not queued.[pid] then queued.[pid] <- true; q.Enqueue pid
            quiesce ()


// ---- The write-once heart: generalized arc consistency over `allows` ----

module private Gac =

    /// Per cell, the values that participate in at least one allowed tuple, given each cell's
    /// current candidate list. Rep-agnostic — the ONE narrowing both faces call, so identical
    /// input candidates yield identical output on either backend.
    let narrow (allows: 'a list -> bool) (candidates: 'a list list) : 'a list list =
        let cand = List.toArray candidates
        let n = cand.Length
        let supported = Array.init n (fun _ -> HashSet<'a>())
        let rec go i acc =
            if i = n then
                let tup = List.rev acc
                if allows tup then tup |> List.iteri (fun j v -> supported.[j].Add v |> ignore)
            else
                for v in cand.[i] do go (i + 1) (v :: acc)
        go 0 []
        [ for j in 0 .. n - 1 -> [ for v in cand.[j] do if supported.[j].Contains v then yield v ] ]


// ---- General face -------------------------------------------------------

let private optimizedDomainWidth = 64
let private optimizedPremiseWidth = 64

let private maxPremisesNeeded (model: Model<'a>) =
    // Givens consume one premise each; DDB can consume at most one live guess per cell.
    List.length model.givens + List.length model.cells

let private optimizedWidthErrors (values: 'a list) (model: Model<'a>) =
    [ if List.length values > optimizedDomainWidth then
          yield FiniteDomainTooWideForOptimized(List.length values, optimizedDomainWidth)
      let needed = maxPremisesNeeded model
      if needed > optimizedPremiseWidth then
          yield PremiseWidthExceeded(needed, optimizedPremiseWidth) ]

module General =

    /// Lower with a caller-supplied finite representation. The representation state remains internal
    /// to the captured engine and does not appear in the returned net or solution.
    let lowerWith
        (makeFiniteRep: 'a list -> FiniteRep<'a, 'state>)
        (model: Model<'a> when 'a : comparison)
        : Result<GeneralNet<'a>, UnsupportedConstruct list> =
        let unsupported =
            [ for c in model.constraints do
                match model.domain, c with
                | Finite _, Dataflow _ -> yield DataflowRequiresRichLattice
                | Lattice _, Relation _ -> yield RelationRequiresFiniteDomain
                | _ -> () ]
        if not (List.isEmpty unsupported) then Error unsupported
        else
            match model.domain with
            | Finite values ->
                let rep = makeFiniteRep values
                let lattice : Closure.Lattice<'state> =
                    { top = rep.top; meet = rep.meet; equals = rep.equals }
                let eng = Closure.Engine<'state>(lattice)
                let emap = Dictionary<int, Closure.Cell<'state>>()
                for pc in model.cells do emap.[pc.id] <- eng.NewCell ()
                let ecell (pc: Cell<'a>) = emap.[pc.id]
                for c in model.constraints do
                    match c with
                    | Relation box ->
                        let ecs = box.cells |> List.map ecell
                        eng.AddProp(ecs, fun () ->
                            let cand = ecs |> List.map (fun c -> rep.candidates c.value)
                            let narrowed = Gac.narrow box.allows cand
                            let sup = ecs |> List.map (fun c -> c.support) |> Set.unionMany
                            List.map2 (fun (c: Closure.Cell<'state>) vs ->
                                c, { Closure.value = rep.ofValues vs; Closure.support = sup }) ecs narrowed) |> ignore
                    | Dataflow _ -> failwith "preflight missed unsupported general finite constraint"
                model.givens |> List.iteri (fun i (pc, v) -> eng.Assert(i, ecell pc, rep.singleton v))
                let run () =
                    let ecells = model.cells |> List.map (fun pc -> pc, ecell pc)
                    let anyBot () = ecells |> List.exists (fun (_, c) -> rep.isBottom c.value)
                    let mutable gp = List.length model.givens
                    let rec go () =
                        if anyBot () then false
                        else
                            match ecells |> List.tryFind (fun (_, c) ->
                                      not (rep.isBottom c.value) && not (rep.isSingleton c.value)) with
                            | None -> true
                            | Some (_, c) ->
                                let rec tryV lst =
                                    match lst with
                                    | [] -> false
                                    | v :: rest ->
                                        let p = gp in gp <- gp + 1
                                        eng.Assert(p, c, rep.singleton v)
                                        if go () then true
                                        else
                                            eng.Retract p
                                            gp <- p
                                            tryV rest
                                tryV (rep.candidates c.value)
                    if go () then
                        Some (ecells |> List.map (fun (pc, c) -> pc, List.head (rep.candidates c.value)) |> Map.ofList)
                    else None
                Ok { model = model; run = run }
            | Lattice ops ->
                let latticeRich : Closure.Lattice<'a> =
                    { top = ops.top; meet = ops.meet; equals = ops.equals }
                let eng = Closure.Engine<'a>(latticeRich)
                let emap = Dictionary<int, Closure.Cell<'a>>()
                for pc in model.cells do emap.[pc.id] <- eng.NewCell ()
                let ecell (pc: Cell<'a>) = emap.[pc.id]
                for c in model.constraints do
                    match c with
                    | Dataflow box ->
                        let ecs = box.cells |> List.map ecell
                        eng.AddProp(ecs, fun () ->
                            let vals = ecs |> List.map (fun c -> c.value)
                            let derived = box.narrow vals
                            let sup = ecs |> List.map (fun c -> c.support) |> Set.unionMany
                            List.map2 (fun (c: Closure.Cell<'a>) v ->
                                c, { Closure.value = v; Closure.support = sup }) ecs derived) |> ignore
                    | Relation _ -> failwith "preflight missed unsupported general lattice constraint"
                model.givens |> List.iteri (fun i (pc, v) -> eng.Assert(i, ecell pc, v))
                let run () =
                    let settled = model.cells |> List.map (fun pc -> pc, (ecell pc).value)
                    if settled |> List.exists (fun (_, v) -> ops.isBottom v) then None
                    else Some (Map.ofList settled)
                Ok { model = model; run = run }

    /// Friendly finite-domain default. All lowering logic remains in `lowerWith`; this wrapper only
    /// selects the Set adapter.
    let lower (model: Model<'a> when 'a : comparison) : Result<GeneralNet<'a>, UnsupportedConstruct list> =
        lowerWith FiniteRep.set model

    /// One solution (DDB on the closure engine: guess = Assert under a fresh premise, backtrack = Retract).
    let solve (net: GeneralNet<'a>) : Solution<'a> option = net.run ()

    // ---- Observation / edit seams — the deferred observation slice, still stubbed (docs §9, §12) ----

    let solutions (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let generate (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let onCell (net: GeneralNet<'a>) (cell: Cell<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (expose OnChange per-cell)"
    let onNet (net: GeneralNet<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (expose OnChange net-wide)"
    let assume (net: GeneralNet<'a>) (cell: Cell<'a>) (value: 'a) : Premise =
        failwith "deferred: edit slice (Assert under fresh premise)"
    let retract (net: GeneralNet<'a>) (premise: Premise) : unit =
        failwith "deferred: edit slice (Retract premise)"


// ---- Optimized face ------------------------------------------------------

module Optimized =

    /// Lower to the optimized face, or NAME every blocking construct as DATA. A rich `Lattice`
    /// domain and any `Dataflow` box surface here — so general-onlyness is visible pre-engine.
    /// A finite model of `Relation`s lowers onto the array core through the same `Gac.narrow`.
    let lower (model: Model<'a> when 'a : comparison) : Result<OptimizedNet<'a>, UnsupportedConstruct list> =
        let constraintBad =
            [ for c in model.constraints do
                match c with
                | Dataflow _ -> yield DataflowOnOptimized
                | Relation _ -> () ]
        match model.domain with
        | Lattice _ -> Error (RichLatticeOnOptimized :: constraintBad)
        | Finite values ->
            let unsupported = constraintBad @ optimizedWidthErrors values model
            if not (List.isEmpty unsupported) then Error unsupported
            else
                let tagged = values |> List.mapi (fun i value -> value, 1UL <<< i) |> List.toArray
                let bitByValue : IReadOnlyDictionary<'a, uint64> =
                    let index = Dictionary<'a, uint64>()
                    for (value, bit) in tagged do index.Add(value, bit)
                    index
                let bitOf (value: 'a) = bitByValue.[value]
                let topWord = tagged |> Array.fold (fun word (_, bit) -> word ||| bit) 0UL
                let decode (word: uint64) =
                    [ for (value, bit) in tagged do
                          if word &&& bit <> 0UL then yield value ]
                let encode (vs: 'a list) = vs |> List.fold (fun a v -> a ||| bitOf v) 0UL
                let latticeW : ArrayCore.Lattice<uint64> =
                    { top = topWord; meet = (&&&); isBot = (fun w -> w = 0UL); equals = (=) }
                let s = ArrayCore.Engine<uint64>(latticeW)
                let imap = Dictionary<int, int>()
                for pc in model.cells do imap.[pc.id] <- s.NewCell ()
                let icell (pc: Cell<'a>) = imap.[pc.id]
                for c in model.constraints do
                    match c with
                    | Relation box ->
                        let ids = box.cells |> List.map icell
                        s.AddProp(ids, fun emit ->
                            let sup = ids |> List.fold (fun a c -> a ||| s.Support c) 0UL
                            let cand = ids |> List.map (fun c -> decode (s.Value c))
                            let narrowed = Gac.narrow box.allows cand
                            List.iter2 (fun c vs -> emit c (encode vs) sup) ids narrowed) |> ignore
                    | Dataflow _ -> failwith "preflight missed unsupported optimized constraint"
                s.Seal ()
                model.givens |> List.iteri (fun i (pc, v) -> s.Assert(i, icell pc, encode [v]))
                let run () =
                    let icells = model.cells |> List.map (fun pc -> pc, icell pc)
                    let single (w: uint64) = w <> 0UL && (w &&& (w - 1UL)) = 0UL
                    let anyBot () = icells |> List.exists (fun (_, c) -> s.IsBot c)
                    let mutable gp = List.length model.givens
                    let rec go () =
                        if anyBot () then false
                        else
                            match icells |> List.tryFind (fun (_, c) ->
                                      not (s.IsBot c) && not (single (s.Value c))) with
                            | None -> true
                            | Some (_, c) ->
                                let rec tryV lst =
                                    match lst with
                                    | [] -> false
                                    | v :: rest ->
                                        let p = gp in gp <- gp + 1
                                        s.Assert(p, c, encode [v])
                                        if go () then true
                                        else
                                            s.Retract p
                                            gp <- p
                                            tryV rest
                                tryV (decode (s.Value c))
                    if go () then
                        Some (icells |> List.map (fun (pc, c) -> pc, List.head (decode (s.Value c))) |> Map.ofList)
                    else None
                Ok { model = model; run = run }

    /// One solution (DDB on the array core — same laws, different machinery from the general face).
    let solve (net: OptimizedNet<'a>) : Solution<'a> option = net.run ()

    // ---- Observation / edit seams — the deferred observation slice, still stubbed ----

    let solutions (net: OptimizedNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let generate (net: OptimizedNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let onCell (net: OptimizedNet<'a>) (cell: Cell<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (trail OnChange per-cell)"
    let onNet (net: OptimizedNet<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (trail OnChange net-wide)"
    let assume (net: OptimizedNet<'a>) (cell: Cell<'a>) (value: 'a) : Premise =
        failwith "deferred: edit slice (Assert under fresh premise)"
    let retract (net: OptimizedNet<'a>) (premise: Premise) : unit =
        failwith "deferred: edit slice (cone-local Retract)"


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
        let cToF iv = Interval.add (Interval.div (Interval.mul iv (Interval.pt 9.0)) (Interval.pt 5.0)) (Interval.pt 32.0)
        let fToC iv = Interval.div (Interval.mul (Interval.sub iv (Interval.pt 32.0)) (Interval.pt 5.0)) (Interval.pt 9.0)
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

    let wideDomainGuard () : Model<int> =
        let c = Cell.create "wide-domain-cell"
        { domain = Domain.finite [1..65]; cells = [ c ]; constraints = []; givens = [] }

    let premiseWidthGuard () : Model<int> =
        let cells = List.init 65 (fun i -> (Cell.create (sprintf "pwidth-%d" i) : Cell<int>))
        { domain = Domain.finite [1; 2]; cells = cells; constraints = []; givens = [] }

    let authoredOrderGuard () : Model<int> =
        let cell = Cell.create "authored-order"
        { domain = Domain.finite [2; 1]; cells = [ cell ]; constraints = []; givens = [] }


// ---- Differential test oracles ------------------------------------------

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
                    let before = box.cells |> List.map (fun cell -> Map.find cell candidates)
                    let after = Gac.narrow box.allows before
                    if after <> before then
                        changed <- true
                        for cell, narrowed in List.zip box.cells after do
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
            let assigned = box.cells |> List.map (fun cell -> Map.tryFind cell assignment)
            if assigned |> List.forall Option.isSome then
                assigned |> List.map Option.get |> box.allows
            else true
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

let private hasFiniteWidth needed width =
    List.exists (function FiniteDomainTooWideForOptimized(n, w) when n = needed && w = width -> true | _ -> false)

let private hasPremiseWidth needed width =
    List.exists (function PremiseWidthExceeded(n, w) when n = needed && w = width -> true | _ -> false)

let expected = [ [1;2;3;4]; [3;4;1;2]; [2;1;4;3]; [4;3;2;1] ]

let main () =
    printfn "runtime: %s" (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn ""

    printfn "== slice 2: 4x4 Sudoku through BOTH lowerings (Differential.solveBoth) =="
    let sud = Slices.sudoku4 ()
    let sudokuOk =
        match Differential.solveBoth sud with
        | Ok (Some fSol, Some oSol) ->
            let fg, og = gridOf sud fSol, gridOf sud oSol
            showGrid "general  (closure engine, FiniteRep.set):" fg
            showGrid "optimized (array core, uint64 word):"  og
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

    printfn "== generic General representation, guardrails, and one-word widths =="
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

    let wideDomain =
        match Optimized.lower (Slices.wideDomainGuard ()) with
        | Error es -> printfn "  Optimized.lower 65-value domain rejects: %A" es; hasFiniteWidth 65 64 es
        | Ok _ -> printfn "  FAIL: optimized accepted a 65-value one-word domain"; false
    let premiseWidth =
        match Optimized.lower (Slices.premiseWidthGuard ()) with
        | Error es -> printfn "  Optimized.lower 65-cell DDB bound rejects: %A" es; hasPremiseWidth 65 64 es
        | Ok _ -> printfn "  FAIL: optimized accepted a >64 premise/search-depth bound"; false
    let guardOk =
        duplicateDomain && genericGeneral && authoredOrderPreserved && dataflowFinite && relationLattice && wideDomain && premiseWidth
    printfn ""

    let ok = sudokuOk && sparseDifferentialOk && optRejects && generalRuns && guardOk
    printfn "RESULT: %s" (if ok then "PASS (two lowerings agree; DDB gate and capability witnesses correct)" else "FAIL")
    if ok then 0 else 1

exit (main ())
