(*  propagator-surface-vocab.fsx

    The propagator core: one authored Model and two lowering faces. The differential harness,
    friendly regressions, and proof slices live in propagator-friendly.tests.fsx. Design:
    docs/propagator-surface-design.md (§5 vocabulary, §7 decided, §10 Codex + Fable review).

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

    solveBoth is TEST HARNESS code, not the shipped surface (docs §7.8, Deen 2026-07-06), and is
    therefore defined only in the separate test script.

    Sharpening 1 (Fable): capability witnesses are ordinary lowering RETURN TYPES
      (Result<_, UnsupportedConstruct list>) — legality is visible before any engine is built. The
      C<->F slice proves it: `Optimized.lower` returns [RichLatticeOnOptimized; DataflowOnOptimized].

    Core/outer boundary (Deen, 2026-07-05): the line is the `#r` — everything here is pure F# (core).
      This file is deliberately load-safe: friendly syntax and tests consume this single engine and
      vocabulary definition through `#load`.

    Still stubbed (the streaming seams): `solutions` / `generate` / `onCell` / `onNet`. General
    cell reads, support inspection, assumptions, and retraction retain one live closure engine;
    optimized observation/edit remains part of the deferred slice (docs §9, §12).

    Guardrail fix (Codex review, 2026-07-06): lowerings now reject every unsupported construct/domain
    combination as an `UnsupportedConstruct` value instead of silently ignoring it, and the optimized
    proof backend refuses >64-value domains or premise/search-depth requirements that exceed its one-word
    support representation. Negative harness rows lock those failures in.

    Review-gate fix (Codex, 2026-07-10): both DDB drivers reuse failed guess premises in LIFO order, making
    the lower-time `givens + cells` premise bound true at runtime. A sparse-givens differential slice now
    proves that search is required, both faces agree, and the authored puzzle is uniquely solvable by an
    independent exhaustive enumerator.

      dotnet fsi propagator-friendly.tests.fsx  *)


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
/// deliberately separate from the authored value type and stays hidden behind `GeneralNet`.
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
///   Lattice — rich meet (interval, scalar), GENERAL-ONLY by construction.
type Domain<'a> =
    | Finite of 'a list
    | Lattice of LatticeOps<'a>

/// A simple equality lattice: Top is unknown, Val is known, and conflicting values meet at Bot.
type Scalar<'value> =
    | Top
    | Val of 'value
    | Bot

let scalarMeet left right =
    match left, right with
    | Top, value | value, Top -> value
    | Bot, _ | _, Bot -> Bot
    | Val a, Val b when a = b -> Val a
    | Val _, Val _ -> Bot


// ---- Spine (constraint boxes) -------------------------------------------

/// A portable finite relation: a membership predicate over a tuple of cell values.
/// Both faces compile it through the shared `Gac.narrow` (general filters a Set, optimized
/// filters a word) — this is the write-once construct.
type RelationBox<'a> =
    { cells: Cell<'a> list
      allows: 'a list -> bool }

/// A general-only rich dataflow relation (convert / sum / equal over lattices). `cells` are reads,
/// `outputs` are targets, and each optional result is a freshly-derived contribution. `None` emits
/// nothing, preserving an independent target value when a source is uninformative.
type DataflowBox<'a> =
    { cells: Cell<'a> list
      outputs: Cell<'a> list
      narrow: 'a list -> 'a option list }

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

/// A representation-independent view of a live general cell. Finite candidates retain authored
/// order; rich lattices expose their current value directly. The backend representation stays hidden.
type CellState<'a> =
    | FiniteCandidates of 'a list
    | LatticeValue of 'a

/// A lowered GENERAL network. Its private closures retain one live closure engine so reads, edits,
/// retraction, and search all operate on the same propagated state without rebuilding a Model.
type GeneralNet<'a> =
    private
        { domain: Domain<'a>
          addCell: string -> Cell<'a>
          addConstraint: Constraint<'a> -> unit
          freshPremise: unit -> Premise
          assertState: Premise -> Cell<'a> -> CellState<'a> -> unit
          retractPremise: Premise -> unit
          readState: Cell<'a> -> CellState<'a>
          readSupport: Cell<'a> -> Set<Premise>
          run: unit -> Solution<'a> option }

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
        Dataflow { cells = cells; outputs = cells; narrow = narrow >> List.map Some }

    /// A pair of directional rich-lattice propagators. An uninformative source emits no
    /// contribution, preserving the target's independent state and provenance.
    let convert
        (left: Cell<'a>)
        (right: Cell<'a>)
        (payload: 'a -> 'payload option)
        (inject: 'payload -> 'a)
        (forward: 'payload -> 'payload)
        (backward: 'payload -> 'payload)
        : Constraint<'a> list =
        [ Dataflow
              { cells = [ left ]
                outputs = [ right ]
                narrow = function
                    | [ value ] -> [ payload value |> Option.map (forward >> inject) ]
                    | _ -> invalidArg "values" "convert forward expected one source value" }
          Dataflow
              { cells = [ right ]
                outputs = [ left ]
                narrow = function
                    | [ value ] -> [ payload value |> Option.map (backward >> inject) ]
                    | _ -> invalidArg "values" "convert backward expected one source value" } ]

    /// A directional fan-in. It emits only when every source has an informative payload.
    let combine
        (sources: Cell<'a> list)
        (target: Cell<'a>)
        (payload: 'a -> 'payload option)
        (inject: 'payload -> 'a)
        (operation: 'payload list -> 'payload)
        : Constraint<'a> =
        Dataflow
            { cells = sources
              outputs = [ target ]
              narrow = fun values ->
                  let projected = List.map payload values
                  if List.forall Option.isSome projected then
                      [ projected |> List.map Option.get |> operation |> inject |> Some ]
                  else
                      [ None ] }

    /// The portable all-different relation used by the friendly finite shorthand.
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
    let sqrt a =
        match a with
        | Empty -> Empty
        | Iv(_, hi) when hi < 0.0 -> Empty
        | Iv(lo, hi) ->
            let lower = if lo <= 0.0 then 0.0 else down (System.Math.Sqrt lo)
            Iv(lower, up (System.Math.Sqrt hi))

type Interval with
    static member (+) (left: Interval, right: Interval) = Interval.add left right
    static member (-) (left: Interval, right: Interval) = Interval.sub left right
    static member (*) (left: Interval, right: Interval) = Interval.mul left right
    static member (/) (left: Interval, right: Interval) = Interval.div left right
    static member (+) (left: Interval, right: float) = Interval.add left (Interval.pt right)
    static member (+) (left: float, right: Interval) = Interval.add (Interval.pt left) right
    static member (-) (left: Interval, right: float) = Interval.sub left (Interval.pt right)
    static member (-) (left: float, right: Interval) = Interval.sub (Interval.pt left) right
    static member (*) (left: Interval, right: float) = Interval.mul left (Interval.pt right)
    static member (*) (left: float, right: Interval) = Interval.mul (Interval.pt left) right
    static member (/) (left: Interval, right: float) = Interval.div left (Interval.pt right)
    static member (/) (left: float, right: Interval) = Interval.div (Interval.pt left) right
    static member Sqrt (value: Interval) = Interval.sqrt value

module Transform =
    let scale (factor: float) = fun value -> value * factor
    let shift (offset: float) = fun value -> value + offset
    let affine factor offset = scale factor >> shift offset

    type Affine =
        { k: float
          b: float }
        member transform.Apply value = transform.k * value + transform.b
        member transform.Inverse = { k = 1.0 / transform.k; b = -transform.b / transform.k }


// ---- Backend engines (the two lowering targets, copied and adapted) -----
// Their internal `Cell` differs from the public handle above, so each is nested and private.

/// The GENERAL backend: the closure `Engine<'a>` of tutorial-propagation-part1.fsx (its 7a-7e),
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

    let private unsupported (model: Model<'a>) =
        [ for constraintBox in model.constraints do
              match model.domain, constraintBox with
              | Finite _, Dataflow _ -> yield DataflowRequiresRichLattice
              | Lattice _, Relation _ -> yield RelationRequiresFiniteDomain
              | _ -> () ]

    let private publicSupport (support: Set<int>) : Set<Premise> =
        support |> Seq.map (fun pid -> { pid = pid }) |> Set.ofSeq

    let private finiteNet
        (rep: FiniteRep<'a, 'state>)
        (model: Model<'a> when 'a : comparison)
        : GeneralNet<'a> =
        let lattice : Closure.Lattice<'state> =
            { top = rep.top; meet = rep.meet; equals = rep.equals }
        let eng = Closure.Engine<'state>(lattice)
        let emap = Dictionary<int, Closure.Cell<'state>>()
        let cells = ResizeArray<Cell<'a>>()
        let authoredValues = match model.domain with Finite values -> values | _ -> []
        let allowed = HashSet<'a>(authoredValues)
        let mutable nextPremise = 0

        let addExisting (cell: Cell<'a>) =
            if emap.ContainsKey cell.id then invalidArg "cell" "cell is already part of this network"
            emap.[cell.id] <- eng.NewCell ()
            cells.Add cell

        let ecell (cell: Cell<'a>) =
            match emap.TryGetValue cell.id with
            | true, found -> found
            | _ -> invalidArg "cell" "cell does not belong to this network"

        let addCell name =
            let cell = Cell.create name
            addExisting cell
            cell

        let addConstraint constraintBox =
            match constraintBox with
            | Relation box ->
                let engineCells = box.cells |> List.map ecell
                eng.AddProp(engineCells, fun () ->
                    let candidates = engineCells |> List.map (fun cell -> rep.candidates cell.value)
                    let narrowed = Gac.narrow box.allows candidates
                    let support = engineCells |> List.map (fun cell -> cell.support) |> Set.unionMany
                    List.map2 (fun (cell: Closure.Cell<'state>) values ->
                        cell, { Closure.value = rep.ofValues values; Closure.support = support }) engineCells narrowed)
                |> ignore
            | Dataflow _ -> invalidOp "Dataflow constraints require a rich lattice domain"

        let freshPremise () =
            let premise = { pid = nextPremise }
            nextPremise <- nextPremise + 1
            premise

        let assertState premise cell state =
            if premise.pid < 0 then invalidArg "premise" "premise ids must be non-negative"
            nextPremise <- max nextPremise (premise.pid + 1)
            match state with
            | FiniteCandidates values ->
                if values |> List.exists (allowed.Contains >> not) then
                    invalidArg "state" "candidate is not in the authored finite domain"
                eng.Assert(premise.pid, ecell cell, rep.ofValues values)
            | LatticeValue _ -> invalidArg "state" "finite networks require FiniteCandidates"

        let run () =
            let engineCells = cells |> Seq.map (fun cell -> cell, ecell cell) |> Seq.toList
            let anyBottom () = engineCells |> List.exists (fun (_, cell) -> rep.isBottom cell.value)
            let rec search () =
                if anyBottom () then false
                else
                    match engineCells |> List.tryFind (fun (_, cell) ->
                              not (rep.isBottom cell.value) && not (rep.isSingleton cell.value)) with
                    | None -> true
                    | Some (_, cell) ->
                        let rec tryValues = function
                            | [] -> false
                            | value :: rest ->
                                let premise = nextPremise
                                nextPremise <- premise + 1
                                eng.Assert(premise, cell, rep.singleton value)
                                if search () then true
                                else
                                    eng.Retract premise
                                    nextPremise <- premise
                                    tryValues rest
                        tryValues (rep.candidates cell.value)
            if search () then
                engineCells
                |> List.map (fun (publicCell, engineCell) ->
                    publicCell, List.head (rep.candidates engineCell.value))
                |> Map.ofList
                |> Some
            else
                None

        model.cells |> List.iter addExisting
        model.constraints |> List.iter addConstraint
        model.givens
        |> List.iter (fun (cell, value) ->
            let premise = freshPremise ()
            assertState premise cell (FiniteCandidates [ value ]))

        { domain = model.domain
          addCell = addCell
          addConstraint = addConstraint
          freshPremise = freshPremise
          assertState = assertState
          retractPremise = fun premise -> eng.Retract premise.pid
          readState = fun cell -> FiniteCandidates (rep.candidates (ecell cell).value)
          readSupport = fun cell -> publicSupport (ecell cell).support
          run = run }

    let private latticeNet (ops: LatticeOps<'a>) (model: Model<'a>) : GeneralNet<'a> =
        let lattice : Closure.Lattice<'a> =
            { top = ops.top; meet = ops.meet; equals = ops.equals }
        let eng = Closure.Engine<'a>(lattice)
        let emap = Dictionary<int, Closure.Cell<'a>>()
        let cells = ResizeArray<Cell<'a>>()
        let mutable nextPremise = 0

        let addExisting (cell: Cell<'a>) =
            if emap.ContainsKey cell.id then invalidArg "cell" "cell is already part of this network"
            emap.[cell.id] <- eng.NewCell ()
            cells.Add cell

        let ecell (cell: Cell<'a>) =
            match emap.TryGetValue cell.id with
            | true, found -> found
            | _ -> invalidArg "cell" "cell does not belong to this network"

        let addCell name =
            let cell = Cell.create name
            addExisting cell
            cell

        let addConstraint constraintBox =
            match constraintBox with
            | Dataflow box ->
                let reads = box.cells |> List.map ecell
                let outputs = box.outputs |> List.map ecell
                eng.AddProp(reads, fun () ->
                    let values = reads |> List.map (fun cell -> cell.value)
                    let derived = box.narrow values
                    if List.length derived <> List.length outputs then
                        invalidOp "dataflow output count does not match its target count"
                    let support = reads |> List.map (fun cell -> cell.support) |> Set.unionMany
                    List.zip outputs derived
                    |> List.choose (fun (cell, value) ->
                        value
                        |> Option.map (fun narrowed ->
                            cell, { Closure.value = narrowed; Closure.support = support })))
                |> ignore
            | Relation _ -> invalidOp "Relation constraints require a finite domain"

        let freshPremise () =
            let premise = { pid = nextPremise }
            nextPremise <- nextPremise + 1
            premise

        let assertState premise cell state =
            if premise.pid < 0 then invalidArg "premise" "premise ids must be non-negative"
            nextPremise <- max nextPremise (premise.pid + 1)
            match state with
            | LatticeValue value -> eng.Assert(premise.pid, ecell cell, value)
            | FiniteCandidates _ -> invalidArg "state" "rich lattice networks require LatticeValue"

        let run () =
            let settled = cells |> Seq.map (fun cell -> cell, (ecell cell).value) |> Seq.toList
            if settled |> List.exists (fun (_, value) -> ops.isBottom value) then None
            else Some (Map.ofList settled)

        model.cells |> List.iter addExisting
        model.constraints |> List.iter addConstraint
        model.givens
        |> List.iter (fun (cell, value) ->
            let premise = freshPremise ()
            assertState premise cell (LatticeValue value))

        { domain = model.domain
          addCell = addCell
          addConstraint = addConstraint
          freshPremise = freshPremise
          assertState = assertState
          retractPremise = fun premise -> eng.Retract premise.pid
          readState = fun cell -> LatticeValue (eng.Value (ecell cell))
          readSupport = fun cell -> publicSupport (ecell cell).support
          run = run }

    /// Lower with a caller-supplied finite representation. The representation state remains internal
    /// to the captured engine and does not appear in the returned net, cell state, or solution.
    let lowerWith
        (makeFiniteRep: 'a list -> FiniteRep<'a, 'state>)
        (model: Model<'a> when 'a : comparison)
        : Result<GeneralNet<'a>, UnsupportedConstruct list> =
        let errors = unsupported model
        if not (List.isEmpty errors) then Error errors
        else
            match model.domain with
            | Finite values -> Ok (finiteNet (makeFiniteRep values) model)
            | Lattice ops -> Ok (latticeNet ops model)

    /// Friendly finite-domain default. All lowering logic remains in `lowerWith`; this wrapper only
    /// selects the Set adapter.
    let lower (model: Model<'a> when 'a : comparison) : Result<GeneralNet<'a>, UnsupportedConstruct list> =
        lowerWith FiniteRep.set model

    /// Start an empty live general network. Added cells and constraints are installed directly in
    /// its retained closure engine.
    let create (domain: Domain<'a> when 'a : comparison) : GeneralNet<'a> =
        let model = { domain = domain; cells = []; constraints = []; givens = [] }
        match domain with
        | Finite values -> finiteNet (FiniteRep.set values) model
        | Lattice ops -> latticeNet ops model

    /// Start an empty live rich-lattice network without imposing a comparison constraint on its values.
    let createLattice (domain: Domain<'a>) : GeneralNet<'a> =
        match domain with
        | Lattice ops ->
            latticeNet ops { domain = domain; cells = []; constraints = []; givens = [] }
        | Finite _ -> invalidArg "domain" "createLattice requires a rich lattice domain"

    /// Start an empty live finite network using the friendly Set representation default.
    let createFinite (values: 'a list when 'a : comparison) : GeneralNet<'a> =
        let domain = Domain.finite values
        finiteNet (FiniteRep.set values) { domain = domain; cells = []; constraints = []; givens = [] }

    let cell (net: GeneralNet<'a>) (name: string) : Cell<'a> = net.addCell name

    let constrain (net: GeneralNet<'a>) (constraintBox: Constraint<'a>) : unit =
        net.addConstraint constraintBox

    let convert net left right payload inject forward backward =
        Constraint.convert left right payload inject forward backward
        |> List.iter net.addConstraint

    let combine net sources target payload inject operation =
        Constraint.combine sources target payload inject operation
        |> net.addConstraint

    let freshPremise (net: GeneralNet<'a>) : Premise = net.freshPremise ()

    let assertStateUnder
        (net: GeneralNet<'a>)
        (premise: Premise)
        (cell: Cell<'a>)
        (state: CellState<'a>)
        : unit =
        net.assertState premise cell state

    let assertUnder (net: GeneralNet<'a>) (premise: Premise) (cell: Cell<'a>) (value: 'a) : unit =
        match net.domain with
        | Finite _ -> net.assertState premise cell (FiniteCandidates [ value ])
        | Lattice _ -> net.assertState premise cell (LatticeValue value)

    /// One solution (DDB on the closure engine: guess = Assert under a fresh premise, backtrack = Retract).
    let solve (net: GeneralNet<'a>) : Solution<'a> option = net.run ()

    let solveFinite (net: GeneralNet<'a>) : bool =
        match net.domain with
        | Finite _ -> net.run () |> Option.isSome
        | Lattice _ -> invalidOp "Solve requires a finite domain (build the network with Domain.finite)"

    let value (net: GeneralNet<'a>) (cell: Cell<'a>) : CellState<'a> = net.readState cell

    let support (net: GeneralNet<'a>) (cell: Cell<'a>) : Set<Premise> = net.readSupport cell

    // ---- Observation / edit seams — the deferred observation slice, still stubbed (docs §9, §12) ----

    let solutions (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let generate (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: observation slice"
    let onCell (net: GeneralNet<'a>) (cell: Cell<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (expose OnChange per-cell)"
    let onNet (net: GeneralNet<'a>) (handler: CellChange<'a> -> unit) : System.IDisposable =
        failwith "deferred: observation slice (expose OnChange net-wide)"
    let assume (net: GeneralNet<'a>) (cell: Cell<'a>) (value: 'a) : Premise =
        let premise = net.freshPremise ()
        assertUnder net premise cell value
        premise
    let retract (net: GeneralNet<'a>) (premise: Premise) : unit =
        net.retractPremise premise


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
