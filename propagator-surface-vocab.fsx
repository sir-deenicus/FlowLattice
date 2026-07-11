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
    membership predicate over one or many equal-arity scopes. General expands scopes through
    `Gac.narrow`; Optimized compiles grouped binary predicates once into directional support rows
    and retains `Gac.narrow` for arbitrary n-ary scopes. Both preserve the same authored semantics.

    solveBoth is TEST HARNESS code, not the shipped surface (docs §7.8, Deen 2026-07-06), and is
    therefore defined only in the separate test script.

    Sharpening 1 (Fable): capability witnesses are ordinary lowering RETURN TYPES
      (Result<_, UnsupportedConstruct list>) — legality is visible before any engine is built. The
      C<->F slice proves it: `Optimized.lower` returns [RichLatticeOnOptimized; DataflowOnOptimized].

    Core/outer boundary (Deen, 2026-07-05): the line is the `#r` — everything here is pure F# (core).
      This file is deliberately load-safe: friendly syntax and tests consume this single engine and
      vocabulary definition through `#load`.

    `generate`, snapshot observation, live finite edits, and retraction are implemented on both faces.
    `solutions` remains deliberately deferred. Optimized finite domains use one runtime-width, cell-major
    bitset store; the independent 64-premise support ceiling is checked at solve/edit boundaries.

    Review-gate fix (Codex, 2026-07-10): both DDB drivers reuse failed guess premises in LIFO order, making
    the lower-time `givens + cells` premise bound true at runtime. A sparse-givens differential slice now
    proves that search is required, both faces agree, and the authored puzzle is uniquely solvable by an
    independent exhaustive enumerator.

      dotnet fsi propagator-friendly.tests.fsx  *)


open System.Collections.Generic
open System.Numerics


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

/// A portable finite relation: one membership predicate over one or more equal-arity scopes.
type RelationBox<'a> =
    { arity: int
      scopes: Cell<'a> array
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
    | PremiseWidthExceeded of needed: int * width: int   // fork B overflow rides this same channel


// ---- Results of lowering: the two nets ----------------------------------

/// An assignment of each cell to a value in its domain.
type Solution<'a> = Map<Cell<'a>, 'a>

/// A representation-independent view of a live general cell. Finite candidates retain authored
/// order; rich lattices expose their current value directly. The backend representation stays hidden.
type CellState<'a> =
    | FiniteCandidates of 'a list
    | LatticeValue of 'a

type GenerationFailure =
    | RestartLimitExceeded of attempts: int

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
          run: unit -> Solution<'a> option
          generate: uint64 -> int -> Result<Solution<'a>, GenerationFailure>
          observeCell: Cell<'a> -> (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable
          observeNet: (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable }

/// A lowered optimized finite network. Its private closures retain one live runtime-width store.
type OptimizedNet<'a> =
    private
        { addCell: string -> Cell<'a>
          addConstraint: Constraint<'a> -> unit
          freshPremise: unit -> Premise
          assertState: Premise -> Cell<'a> -> CellState<'a> -> unit
          retractPremise: Premise -> unit
          readState: Cell<'a> -> CellState<'a>
          readSupport: Cell<'a> -> Set<Premise>
          run: unit -> Solution<'a> option
          generate: uint64 -> int -> Result<Solution<'a>, GenerationFailure>
          observeCell: Cell<'a> -> (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable
          observeNet: (Cell<'a> * CellState<'a> -> unit) -> System.IDisposable }

/// A change event on a cell — the unit of the observation seam. A DU, not a bare before->after,
/// because DDB and the trail WIDEN on backtrack and interactive edit wants restores. The payload is
/// the rep-agnostic domain projection (remaining candidates as `'a list`), not the resolved singleton
/// `'a` and not the backend rep; the exact projection type is the one open payload detail (§12).


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

    let private relationBox (scopes: Cell<'a> list list) (allows: 'a list -> bool) =
        match scopes with
        | [] -> invalidArg "scopes" "a relation requires at least one scope"
        | first :: _ when List.isEmpty first -> invalidArg "scopes" "relation scopes cannot be empty"
        | first :: rest ->
            let arity = List.length first
            if rest |> List.exists (fun scope -> List.length scope <> arity) then
                invalidArg "scopes" "all relation scopes must have the same arity"
            Relation
                { arity = arity
                  scopes = scopes |> List.collect id |> List.toArray
                  allows = allows }

    /// A portable finite relation over cells (membership predicate).
    let relation (cells: Cell<'a> list) (allows: 'a list -> bool) : Constraint<'a> =
        relationBox [ cells ] allows

    /// One portable finite relation applied to many equal-arity scopes.
    let relations (scopes: seq<Cell<'a> list>) (allows: 'a list -> bool) : Constraint<'a> =
        relationBox (Seq.toList scopes) allows

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
    type Origin = Ext of Premise | Prop of int | Generated of int
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
          mutable initialized: bool
          fire: unit -> (Cell<'a> * Contribution<'a>) list }

    type Engine<'a>(L: Lattice<'a>) =
        let cells = ResizeArray<Cell<'a>>()
        let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()
        let mutable nCell = 0
        let mutable nProp = 0
        let handlers = Dictionary<int, int * 'a -> unit>()
        let mutable nHandler = 0

        member private _.Recompute (c: Cell<'a>) =
            let before = c.value
            let mutable v = L.top
            let mutable s = Set.empty
            for kv in c.contribs do
                v <- L.meet v kv.Value.value
                if not (L.equals kv.Value.value L.top) then s <- Set.union s kv.Value.support
            c.value <- v; c.support <- s
            if not (L.equals before v) then
                for handler in handlers.Values do handler (c.id, v)

        member private this.Quiesce (frontier: seq<Cell<'a>>) =
            let q = Queue<Propagator<'a>>()
            let wake (c: Cell<'a>) = for p in watch.[c.id] do q.Enqueue p
            Seq.iter wake frontier
            while q.Count > 0 do
                let p = q.Dequeue()
                p.initialized <- true
                for (target, fact) in p.fire () do
                    let before = target.value
                    target.contribs.[Prop p.pid] <- fact
                    this.Recompute target
                    if not (L.equals target.value before) then wake target

        member _.NewCell () =
            let c = { id = nCell; contribs = Dictionary(); value = L.top; support = Set.empty }
            nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

        member _.AddProp (reads, fire) =
            let p = { pid = nProp; reads = reads; initialized = false; fire = fire }
            nProp <- nProp + 1
            for c in reads do watch.[c.id].Add p
            p

        member this.Stabilize () =
            let q = Queue<Propagator<'a>>()
            for propagators in watch.Values do
                for propagator in propagators do
                    if not propagator.initialized then
                        propagator.initialized <- true
                        q.Enqueue propagator
            while q.Count > 0 do
                let propagator = q.Dequeue()
                for target, fact in propagator.fire () do
                    let before = target.value
                    target.contribs.[Prop propagator.pid] <- fact
                    this.Recompute target
                    if not (L.equals target.value before) then
                        for next in watch.[target.id] do q.Enqueue next

        member this.Collapse (c: Cell<'a>, value: 'a) =
            c.contribs.[Generated c.id] <- { value = value; support = Set.empty }
            this.Recompute c
            this.Quiesce [ c ]

        member this.ResetGenerated () =
            for c in cells do
                let dead =
                    [ for kv in c.contribs do
                          match kv.Key with
                          | Prop _ | Generated _ -> yield kv.Key
                          | Ext _ -> () ]
                for key in dead do c.contribs.Remove key |> ignore
                this.Recompute c
            this.Quiesce cells

        member _.Subscribe (handler: int * 'a -> unit) =
            let id = nHandler
            nHandler <- nHandler + 1
            handlers.[id] <- handler
            { new System.IDisposable with
                member _.Dispose () = handlers.Remove id |> ignore }

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

module private StableRandom =

    let next state =
        let state' = state + 0x9E3779B97F4A7C15UL
        let mutable value = state'
        value <- (value ^^^ (value >>> 30)) * 0xBF58476D1CE4E5B9UL
        value <- (value ^^^ (value >>> 27)) * 0x94D049BB133111EBUL
        state', value ^^^ (value >>> 31)

    let rec bounded bound state =
        if bound = 0UL then invalidArg "bound" "random bound must be positive"
        let state', value = next state
        let threshold = (0UL - bound) % bound
        if value >= threshold then state', int (value % bound)
        else bounded bound state'

module private FiniteCore =

    type Table =
        { forward: uint64[]
          reverse: uint64[] }

    [<Struct>]
    type Arc =
        { source: int
          target: int
          table: int
          reverse: bool }

    type Nary<'a> =
        { cells: int[]
          allows: 'a list -> bool }

    type Engine<'a when 'a : equality>
        (domain: 'a[], publicCells: Cell<'a>[], constraints: Constraint<'a> list) =

        let domainCount = domain.Length
        let wordCount = max 1 ((domainCount + 63) / 64)
        let finalMask =
            let used = domainCount % 64
            if used = 0 then System.UInt64.MaxValue else (1UL <<< used) - 1UL
        let top = Array.init wordCount (fun word -> if word = wordCount - 1 then finalMask else System.UInt64.MaxValue)
        let valueIndex = Dictionary<'a, int>()
        let cellIndex = Dictionary<int, int>()
        do
            domain |> Array.iteri (fun index value -> valueIndex.Add(value, index))
            publicCells |> Array.iteri (fun index cell -> cellIndex.Add(cell.id, index))

        let encode (items: 'a list) =
            let words = Array.zeroCreate<uint64> wordCount
            for item in items do
                match valueIndex.TryGetValue item with
                | true, index -> words.[index / 64] <- words.[index / 64] ||| (1UL <<< (index % 64))
                | _ -> invalidArg "items" "candidate is not in the authored finite domain"
            words

        let tables, arcs, naries, watches =
            let tables = ResizeArray<Table>()
            let arcs = ResizeArray<Arc>()
            let naries = ResizeArray<Nary<'a>>()
            let watches = Array.init publicCells.Length (fun _ -> ResizeArray<int>())
            let addArc source target table reverse =
                let id = arcs.Count
                arcs.Add { source = source; target = target; table = table; reverse = reverse }
                watches.[source].Add id
            for constraintBox in constraints do
                match constraintBox with
                | Dataflow _ -> invalidOp "optimized finite networks do not support dataflow constraints"
                | Relation box when box.arity = 2 ->
                    let forward = Array.zeroCreate<uint64> (domainCount * wordCount)
                    let reverse = Array.zeroCreate<uint64> (domainCount * wordCount)
                    for left in 0 .. domainCount - 1 do
                        for right in 0 .. domainCount - 1 do
                            if box.allows [ domain.[left]; domain.[right] ] then
                                forward.[left * wordCount + right / 64] <-
                                    forward.[left * wordCount + right / 64] ||| (1UL <<< (right % 64))
                                reverse.[right * wordCount + left / 64] <-
                                    reverse.[right * wordCount + left / 64] ||| (1UL <<< (left % 64))
                    let tableId = tables.Count
                    tables.Add { forward = forward; reverse = reverse }
                    for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                        let left = cellIndex.[box.scopes.[offset].id]
                        let right = cellIndex.[box.scopes.[offset + 1].id]
                        addArc left right tableId false
                        addArc right left tableId true
                | Relation box ->
                    for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                        let ids =
                            [| for index in offset .. offset + box.arity - 1 -> cellIndex.[box.scopes.[index].id] |]
                        let id = naries.Count
                        naries.Add { cells = ids; allows = box.allows }
                        let work = -(id + 1)
                        for cell in ids do watches.[cell].Add work
            tables.ToArray(), arcs.ToArray(), naries.ToArray(), watches |> Array.map _.ToArray()

        let state = Array.zeroCreate<uint64> (publicCells.Length * wordCount)
        let supports = Array.zeroCreate<uint64> publicCells.Length
        let assertions = Dictionary<struct(int * int), uint64[]>()
        let generated = Dictionary<int, uint64[]>()
        let handlers = Dictionary<int, int -> unit>()
        let mutable nextHandler = 0
        let queue = Queue<int>()
        let queuedArcs = Array.zeroCreate<bool> arcs.Length
        let queuedNaries = Array.zeroCreate<bool> naries.Length

        let wordOffset cell = cell * wordCount
        let hasValue cell value =
            state.[wordOffset cell + value / 64] &&& (1UL <<< (value % 64)) <> 0UL
        let notify cell = for handler in handlers.Values do handler cell
        let enqueue work =
            if work >= 0 then
                if not queuedArcs.[work] then queuedArcs.[work] <- true; queue.Enqueue work
            else
                let id = -work - 1
                if not queuedNaries.[id] then queuedNaries.[id] <- true; queue.Enqueue work
        let enqueueCell cell = for work in watches.[cell] do enqueue work

        let meetCell cell (mask: uint64[]) support =
            let offset = wordOffset cell
            let mutable changed = false
            for word in 0 .. wordCount - 1 do
                let narrowed = state.[offset + word] &&& mask.[word]
                if narrowed <> state.[offset + word] then
                    state.[offset + word] <- narrowed
                    changed <- true
            if changed then
                supports.[cell] <- supports.[cell] ||| support
                notify cell
                enqueueCell cell
            changed

        let candidates cell =
            [ for index in 0 .. domainCount - 1 do if hasValue cell index then yield domain.[index] ]

        let fireArc id =
            let arc = arcs.[id]
            let table = tables.[arc.table]
            let rows = if arc.reverse then table.reverse else table.forward
            let allowed = Array.zeroCreate<uint64> wordCount
            for value in 0 .. domainCount - 1 do
                if hasValue arc.source value then
                    let row = value * wordCount
                    for word in 0 .. wordCount - 1 do
                        allowed.[word] <- allowed.[word] ||| rows.[row + word]
            meetCell arc.target allowed supports.[arc.source] |> ignore

        let fireNary id =
            let relation = naries.[id]
            let before = relation.cells |> Array.map candidates |> Array.toList
            let after = Gac.narrow relation.allows before
            let support = relation.cells |> Array.fold (fun value cell -> value ||| supports.[cell]) 0UL
            Array.iter2 (fun cell narrowed -> meetCell cell (encode narrowed) support |> ignore)
                relation.cells (List.toArray after)

        let quiesce () =
            while queue.Count > 0 do
                let work = queue.Dequeue()
                if work >= 0 then
                    queuedArcs.[work] <- false
                    fireArc work
                else
                    let id = -work - 1
                    queuedNaries.[id] <- false
                    fireNary id

        let enqueueAll () =
            for id in 0 .. arcs.Length - 1 do enqueue id
            for id in 0 .. naries.Length - 1 do enqueue (-id - 1)

        let resetQueue () =
            queue.Clear()
            System.Array.Clear(queuedArcs, 0, queuedArcs.Length)
            System.Array.Clear(queuedNaries, 0, queuedNaries.Length)

        let setTop cell =
            let offset = wordOffset cell
            let mutable changed = false
            for word in 0 .. wordCount - 1 do
                if state.[offset + word] <> top.[word] then changed <- true
                state.[offset + word] <- top.[word]
            supports.[cell] <- 0UL
            if changed then notify cell

        let rebuild includeGenerated =
            resetQueue ()
            for cell in 0 .. publicCells.Length - 1 do setTop cell
            assertions
            |> Seq.sortBy (fun pair -> let struct(premise, cell) = pair.Key in premise, cell)
            |> Seq.iter (fun pair ->
                let struct(premise, cell) = pair.Key
                meetCell cell pair.Value (1UL <<< premise) |> ignore)
            if includeGenerated then
                generated |> Seq.sortBy _.Key |> Seq.iter (fun pair -> meetCell pair.Key pair.Value 0UL |> ignore)
            enqueueAll ()
            quiesce ()

        do
            for cell in 0 .. publicCells.Length - 1 do
                let offset = wordOffset cell
                System.Array.Copy(top, 0, state, offset, wordCount)
            enqueueAll ()
            quiesce ()

        member _.CellIndex (cell: Cell<'a>) =
            match cellIndex.TryGetValue cell.id with
            | true, index -> index
            | _ -> invalidArg "cell" "cell does not belong to this network"

        member _.Candidates cell = candidates cell
        member _.CandidateCount cell =
            let offset = wordOffset cell
            let mutable count = 0
            for word in 0 .. wordCount - 1 do count <- count + int (BitOperations.PopCount state.[offset + word])
            count
        member this.IsBottom cell = this.CandidateCount cell = 0
        member this.IsSingleton cell = this.CandidateCount cell = 1
        member _.Support cell = supports.[cell]

        member _.Assert (premise: int, cell: int, items: 'a list) =
            if premise < 0 || premise >= 64 then invalidArg "premise" "premise bitmask caps at 64"
            let key = struct(premise, cell)
            let replacing = assertions.ContainsKey key
            assertions.[key] <- encode items
            if replacing then rebuild true
            else
                meetCell cell assertions.[key] (1UL <<< premise) |> ignore
                quiesce ()

        member _.Retract premise =
            if premise < 0 || premise >= 64 then invalidArg "premise" "premise bitmask caps at 64"
            let dead =
                [ for pair in assertions do
                      let struct(found, _) = pair.Key
                      if found = premise then yield pair.Key ]
            for key in dead do assertions.Remove key |> ignore
            rebuild true

        member _.Collapse (cell, value: 'a) =
            let mask = encode [ value ]
            generated.[cell] <- mask
            meetCell cell mask 0UL |> ignore
            quiesce ()

        member _.ResetGenerated () =
            if generated.Count > 0 then
                generated.Clear()
                rebuild false

        member _.Subscribe handler =
            let id = nextHandler
            nextHandler <- nextHandler + 1
            handlers.[id] <- handler
            { new System.IDisposable with
                member _.Dispose () = handlers.Remove id |> ignore }


// ---- General face -------------------------------------------------------

let private optimizedPremiseWidth = 64

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
                for offset in 0 .. box.arity .. box.scopes.Length - 1 do
                    let engineCells =
                        [ for index in offset .. offset + box.arity - 1 -> ecell box.scopes.[index] ]
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

        let engineCells () = cells |> Seq.map (fun cell -> cell, ecell cell) |> Seq.toList
        let solution () =
            engineCells ()
            |> List.map (fun (publicCell, engineCell) ->
                publicCell, List.head (rep.candidates engineCell.value))
            |> Map.ofList

        let run () =
            eng.Stabilize ()
            let engineCells = engineCells ()
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
                Some (solution ())
            else
                None

        let generate seed attempts =
            if attempts <= 0 then invalidArg "attempts" "generation attempts must be positive"
            let mutable randomState = seed
            let rec attempt number =
                eng.ResetGenerated ()
                let rec collapse () =
                    let openCell =
                        engineCells ()
                        |> List.choose (fun (_, cell) ->
                            let candidates = rep.candidates cell.value
                            if List.length candidates > 1 then Some (List.length candidates, cell, candidates)
                            else None)
                        |> List.sortBy (fun (count, cell, _) -> count, cell.id)
                        |> List.tryHead
                    if engineCells () |> List.exists (fun (_, cell) -> rep.isBottom cell.value) then false
                    else
                        match openCell with
                        | None -> true
                        | Some (count, cell, candidates) ->
                            let nextState, choice = StableRandom.bounded (uint64 count) randomState
                            randomState <- nextState
                            eng.Collapse(cell, rep.singleton candidates.[choice])
                            collapse ()
                if collapse () then Ok (solution ())
                elif number < attempts then attempt (number + 1)
                else
                    eng.ResetGenerated ()
                    Error (RestartLimitExceeded attempts)
            attempt 1

        let observeCell cell handler =
            eng.Stabilize ()
            let engineCell = ecell cell
            handler (cell, FiniteCandidates (rep.candidates engineCell.value))
            eng.Subscribe(fun (changed, state) ->
                if changed = engineCell.id then handler (cell, FiniteCandidates (rep.candidates state)))

        let observeNet handler =
            eng.Stabilize ()
            for cell, engineCell in engineCells () do
                handler (cell, FiniteCandidates (rep.candidates engineCell.value))
            let byId = engineCells () |> Seq.map (fun (cell, engineCell) -> engineCell.id, cell) |> dict
            eng.Subscribe(fun (changed, state) ->
                handler (byId.[changed], FiniteCandidates (rep.candidates state)))

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
          readState = fun cell -> eng.Stabilize (); FiniteCandidates (rep.candidates (ecell cell).value)
          readSupport = fun cell -> eng.Stabilize (); publicSupport (ecell cell).support
          run = run
          generate = generate
          observeCell = observeCell
          observeNet = observeNet }

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
            eng.Stabilize ()
            let settled = cells |> Seq.map (fun cell -> cell, (ecell cell).value) |> Seq.toList
            if settled |> List.exists (fun (_, value) -> ops.isBottom value) then None
            else Some (Map.ofList settled)

        let observeCell cell handler =
            eng.Stabilize ()
            let engineCell = ecell cell
            handler (cell, LatticeValue engineCell.value)
            eng.Subscribe(fun (changed, state) ->
                if changed = engineCell.id then handler (cell, LatticeValue state))

        let observeNet handler =
            eng.Stabilize ()
            let engineCells = cells |> Seq.map (fun cell -> cell, ecell cell) |> Seq.toArray
            for cell, engineCell in engineCells do handler (cell, LatticeValue engineCell.value)
            let byId = engineCells |> Seq.map (fun (cell, engineCell) -> engineCell.id, cell) |> dict
            eng.Subscribe(fun (changed, state) -> handler (byId.[changed], LatticeValue state))

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
          readState = fun cell -> eng.Stabilize (); LatticeValue (eng.Value (ecell cell))
          readSupport = fun cell -> eng.Stabilize (); publicSupport (ecell cell).support
          run = run
          generate = fun _ _ -> invalidOp "Generate requires a finite domain"
          observeCell = observeCell
          observeNet = observeNet }

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

    let convert (net: GeneralNet<'a>) left right payload inject forward backward =
        Constraint.convert left right payload inject forward backward
        |> List.iter net.addConstraint

    let combine (net: GeneralNet<'a>) sources target payload inject operation =
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

    let solutions (net: GeneralNet<'a>) : Solution<'a> seq = failwith "deferred: solutions"
    let generateWith attempts seed (net: GeneralNet<'a>) = net.generate seed attempts
    let generate seed (net: GeneralNet<'a>) = net.generate seed 32
    let onCell (net: GeneralNet<'a>) (cell: Cell<'a>) handler = net.observeCell cell handler
    let onNet (net: GeneralNet<'a>) handler = net.observeNet handler
    let assume (net: GeneralNet<'a>) (cell: Cell<'a>) (value: 'a) : Premise =
        let premise = net.freshPremise ()
        assertUnder net premise cell value
        premise
    let retract (net: GeneralNet<'a>) (premise: Premise) : unit =
        net.retractPremise premise


// ---- Optimized face ------------------------------------------------------

module Optimized =

    let private finiteNet (values: 'a list) (model: Model<'a> when 'a : comparison) =
        let authored = List.toArray values
        let cells = ResizeArray<Cell<'a>>(model.cells)
        let cellIds = HashSet<int>(model.cells |> Seq.map _.id)
        let constraints = ResizeArray<Constraint<'a>>(model.constraints)
        let pending = Dictionary<struct(int * int), 'a list>()
        let mutable nextPremise = 0
        let mutable engine: FiniteCore.Engine<'a> option = None
        let mutable searchPremises: int list = []

        let cellPosition cell =
            cells
            |> Seq.tryFindIndex (fun candidate -> candidate.id = cell.id)
            |> Option.defaultWith (fun () -> invalidArg "cell" "cell does not belong to this network")

        let requireAuthoring () =
            if engine.IsSome then invalidOp "structural authoring is closed after the network is first observed or run"

        let ensureEngine () =
            match engine with
            | Some found -> found
            | None ->
                let built = FiniteCore.Engine<'a>(authored, cells.ToArray(), List.ofSeq constraints)
                pending
                |> Seq.sortBy (fun pair -> let struct(premise, cell) = pair.Key in premise, cell)
                |> Seq.iter (fun pair ->
                    let struct(premise, cell) = pair.Key
                    built.Assert(premise, cell, pair.Value))
                pending.Clear()
                engine <- Some built
                built

        let addCell name =
            requireAuthoring ()
            let cell = Cell.create name
            cells.Add cell
            cellIds.Add cell.id |> ignore
            cell

        let addConstraint constraintBox =
            requireAuthoring ()
            match constraintBox with
            | Relation box when box.scopes |> Array.exists (fun cell -> not (cellIds.Contains cell.id)) ->
                invalidArg "constraintBox" "relation contains a cell outside this network"
            | Relation _ -> constraints.Add constraintBox
            | Dataflow _ -> invalidOp "optimized finite networks do not support dataflow constraints"

        let freshPremise () =
            if nextPremise >= optimizedPremiseWidth then
                invalidOp (sprintf "PremiseWidthExceeded(%d, %d)" (nextPremise + 1) optimizedPremiseWidth)
            let premise = { pid = nextPremise }
            nextPremise <- nextPremise + 1
            premise

        let assertState premise cell state =
            if premise.pid < 0 || premise.pid >= optimizedPremiseWidth then
                invalidArg "premise" "optimized premise ids must be between 0 and 63"
            nextPremise <- max nextPremise (premise.pid + 1)
            let candidates =
                match state with
                | FiniteCandidates candidates -> candidates
                | LatticeValue _ -> invalidArg "state" "finite networks require FiniteCandidates"
            let index = cellPosition cell
            match engine with
            | Some built -> built.Assert(premise.pid, index, candidates)
            | None -> pending.[struct(premise.pid, index)] <- candidates

        let retractPremise premise =
            match engine with
            | Some built -> built.Retract premise.pid
            | None ->
                let dead =
                    [ for pair in pending do
                          let struct(found, _) = pair.Key
                          if found = premise.pid then yield pair.Key ]
                for key in dead do pending.Remove key |> ignore

        let clearSearch () =
            match engine with
            | Some built ->
                for premise in searchPremises |> List.sortDescending do built.Retract premise
            | None -> ()
            searchPremises <- []

        let solution (built: FiniteCore.Engine<'a>) =
            cells
            |> Seq.mapi (fun index cell -> cell, List.head (built.Candidates index))
            |> Map.ofSeq

        let anyBottom (built: FiniteCore.Engine<'a>) =
            cells |> Seq.mapi (fun index _ -> built.IsBottom index) |> Seq.exists id

        let run () =
            clearSearch ()
            let needed = nextPremise + cells.Count
            if needed > optimizedPremiseWidth then
                invalidOp (sprintf "PremiseWidthExceeded(%d, %d)" needed optimizedPremiseWidth)
            let built = ensureEngine ()
            built.ResetGenerated ()
            let mutable guess = nextPremise
            let rec search () =
                if anyBottom built then false
                else
                    match cells |> Seq.mapi (fun index _ -> index) |> Seq.tryFind (built.IsSingleton >> not) with
                    | None -> true
                    | Some cell ->
                        let rec tryValues = function
                            | [] -> false
                            | value :: rest ->
                                let premise = guess
                                guess <- guess + 1
                                built.Assert(premise, cell, [ value ])
                                if search () then
                                    searchPremises <- premise :: searchPremises
                                    true
                                else
                                    built.Retract premise
                                    guess <- premise
                                    tryValues rest
                        tryValues (built.Candidates cell)
            if search () then Some (solution built) else None

        let generate seed attempts =
            if attempts <= 0 then invalidArg "attempts" "generation attempts must be positive"
            clearSearch ()
            let built = ensureEngine ()
            let mutable randomState = seed
            let rec attempt number =
                built.ResetGenerated ()
                let heap = PriorityQueue<int, struct(int * int)>()
                let counts = Array.init cells.Count built.CandidateCount
                let mutable unresolved = counts |> Array.sumBy (fun count -> if count > 1 then 1 else 0)
                let mutable contradictions = counts |> Array.sumBy (fun count -> if count = 0 then 1 else 0)
                let changed cell =
                    let before = counts.[cell]
                    let after = built.CandidateCount cell
                    counts.[cell] <- after
                    if before > 1 && after <= 1 then unresolved <- unresolved - 1
                    elif before <= 1 && after > 1 then unresolved <- unresolved + 1
                    if before = 0 && after > 0 then contradictions <- contradictions - 1
                    elif before > 0 && after = 0 then contradictions <- contradictions + 1
                    if after > 1 then heap.Enqueue(cell, struct(after, cell))
                for cell in 0 .. cells.Count - 1 do
                    if counts.[cell] > 1 then heap.Enqueue(cell, struct(counts.[cell], cell))
                use subscription = built.Subscribe changed
                let rec collapse () =
                    if contradictions > 0 then false
                    elif unresolved = 0 then true
                    else
                        let cell = heap.Dequeue()
                        let count = counts.[cell]
                        if count <= 1 then collapse ()
                        else
                            let candidates = built.Candidates cell
                            let nextState, choice = StableRandom.bounded (uint64 count) randomState
                            randomState <- nextState
                            built.Collapse(cell, candidates.[choice])
                            collapse ()
                if collapse () then Ok (solution built)
                elif number < attempts then attempt (number + 1)
                else
                    built.ResetGenerated ()
                    Error (RestartLimitExceeded attempts)
            attempt 1

        let readState cell =
            let built = ensureEngine ()
            FiniteCandidates (built.Candidates (cellPosition cell))

        let readSupport cell =
            let support = (ensureEngine ()).Support(cellPosition cell)
            [ for premise in 0 .. 63 do
                  if support &&& (1UL <<< premise) <> 0UL then yield { pid = premise } ]
            |> Set.ofList

        let observeCell cell handler =
            let built = ensureEngine ()
            let index = cellPosition cell
            handler (cell, FiniteCandidates (built.Candidates index))
            built.Subscribe(fun changed ->
                if changed = index then handler (cell, FiniteCandidates (built.Candidates index)))

        let observeNet handler =
            let built = ensureEngine ()
            for index in 0 .. cells.Count - 1 do
                handler (cells.[index], FiniteCandidates (built.Candidates index))
            built.Subscribe(fun changed ->
                handler (cells.[changed], FiniteCandidates (built.Candidates changed)))

        model.givens
        |> List.iter (fun (cell, value) ->
            let premise = freshPremise ()
            assertState premise cell (FiniteCandidates [ value ]))

        { addCell = addCell
          addConstraint = addConstraint
          freshPremise = freshPremise
          assertState = assertState
          retractPremise = retractPremise
          readState = readState
          readSupport = readSupport
          run = run
          generate = generate
          observeCell = observeCell
          observeNet = observeNet }

    let lower (model: Model<'a> when 'a : comparison) : Result<OptimizedNet<'a>, UnsupportedConstruct list> =
        let constraintBad =
            [ for constraintBox in model.constraints do
                  match constraintBox with
                  | Dataflow _ -> yield DataflowOnOptimized
                  | Relation _ -> () ]
        match model.domain with
        | Lattice _ -> Error (RichLatticeOnOptimized :: constraintBad)
        | Finite values ->
            let premiseBad =
                if List.length model.givens > optimizedPremiseWidth then
                    [ PremiseWidthExceeded(List.length model.givens, optimizedPremiseWidth) ]
                else []
            let errors = constraintBad @ premiseBad
            if List.isEmpty errors then Ok (finiteNet values model) else Error errors

    let createFinite (values: 'a list when 'a : comparison) =
        let domain = Domain.finite values
        finiteNet values { domain = domain; cells = []; constraints = []; givens = [] }

    let cell (net: OptimizedNet<'a>) name = net.addCell name
    let constrain (net: OptimizedNet<'a>) constraintBox = net.addConstraint constraintBox
    let freshPremise (net: OptimizedNet<'a>) = net.freshPremise ()
    let assertStateUnder (net: OptimizedNet<'a>) premise cell state = net.assertState premise cell state
    let solve (net: OptimizedNet<'a>) = net.run ()
    let value (net: OptimizedNet<'a>) cell = net.readState cell
    let support (net: OptimizedNet<'a>) cell = net.readSupport cell
    let solutions (net: OptimizedNet<'a>) : Solution<'a> seq = failwith "deferred: solutions"
    let generateWith attempts seed (net: OptimizedNet<'a>) = net.generate seed attempts
    let generate seed (net: OptimizedNet<'a>) = net.generate seed 32
    let onCell (net: OptimizedNet<'a>) cell handler = net.observeCell cell handler
    let onNet (net: OptimizedNet<'a>) handler = net.observeNet handler
    let assume (net: OptimizedNet<'a>) cell value =
        let premise = net.freshPremise ()
        net.assertState premise cell (FiniteCandidates [ value ])
        premise
    let retract (net: OptimizedNet<'a>) premise = net.retractPremise premise
