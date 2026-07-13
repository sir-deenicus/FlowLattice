module Propagator.Core

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

/// A stable caller-authored identity for an enduring structural constraint. Unlike a premise,
/// it is descriptive provenance and is never a retraction handle.
type ConstraintId = private ConstraintId of string

/// The two independent sources of derivation evidence carried by General contributions.
type Support =
    { Premises: Set<Premise>
      Constraints: Set<ConstraintId> }

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
    { constraintId: ConstraintId option
      arity: int
      scopes: Cell<'a> array
      allows: 'a list -> bool }

/// A general-only rich dataflow relation (convert / sum / equal over lattices). `cells` are reads,
/// `outputs` are targets, and each optional result is a freshly-derived contribution. `None` emits
/// nothing, preserving an independent target value when a source is uninformative.
type DataflowBox<'a> =
    { constraintId: ConstraintId option
      cells: Cell<'a> list
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

/// One representation-independent cell entry in a bounded General propagation result.
type CellSnapshot<'a> =
    { cell: Cell<'a>
      state: CellState<'a>
      support: Support }

/// The result of running only the permitted number of strict aggregate cell changes.
type PropagationResult<'a> =
    | Fixpoint of snapshot: CellSnapshot<'a> list * narrowingEvents: int
    | Contradiction of snapshot: CellSnapshot<'a> list * support: Support * narrowingEvents: int
    | Truncated of partialSnapshot: CellSnapshot<'a> list * narrowingEvents: int

/// A change event on a cell — the unit of the observation seam. A DU, not a bare before->after,
/// because DDB and the trail WIDEN on backtrack and interactive edit wants restores. The payload is
/// the rep-agnostic domain projection (remaining candidates as `'a list`), not the resolved singleton
/// `'a` and not the backend rep; the exact projection type is the one open payload detail (§12).


// ---- Authoring (companion modules for the vocabulary nouns) -------------

module ConstraintId =

    let create (value: string) : ConstraintId =
        if System.String.IsNullOrWhiteSpace value then
            invalidArg "value" "constraint ids must not be empty"
        ConstraintId value

    let value (ConstraintId value) = value

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
                { constraintId = None
                  arity = arity
                  scopes = scopes |> List.collect id |> List.toArray
                  allows = allows }

    /// Attach the caller's stable structural identity to a relation or dataflow constraint.
    let named (constraintId: ConstraintId) (constraintBox: Constraint<'a>) : Constraint<'a> =
        match constraintBox with
        | Relation box -> Relation { box with constraintId = Some constraintId }
        | Dataflow box -> Dataflow { box with constraintId = Some constraintId }

    /// A portable finite relation over cells (membership predicate).
    let relation (cells: Cell<'a> list) (allows: 'a list -> bool) : Constraint<'a> =
        relationBox [ cells ] allows

    /// One portable finite relation applied to many equal-arity scopes.
    let relations (scopes: seq<Cell<'a> list>) (allows: 'a list -> bool) : Constraint<'a> =
        relationBox (Seq.toList scopes) allows

    /// A general-only rich dataflow relation.
    let dataflow (cells: Cell<'a> list) (narrow: 'a list -> 'a list) : Constraint<'a> =
        Dataflow
            { constraintId = None
              cells = cells
              outputs = cells
              narrow = narrow >> List.map Some }

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
              { constraintId = None
                cells = [ left ]
                outputs = [ right ]
                narrow = function
                    | [ value ] -> [ payload value |> Option.map (forward >> inject) ]
                    | _ -> invalidArg "values" "convert forward expected one source value" }
          Dataflow
              { constraintId = None
                cells = [ right ]
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
            { constraintId = None
              cells = sources
              outputs = [ target ]
              narrow = fun values ->
                  let projected = List.map payload values
                  if List.forall Option.isSome projected then
                      [ projected |> List.map Option.get |> operation |> inject |> Some ]
                  else
                      [ None ] }

    /// The portable all-different relation used by the friendly finite shorthand.
// ---- The canonical rich interval lattice used by the general face ----
// Copied from propagator-friendly.fsx / propagator-number-types.fsx §4: meet = intersection,
// Bot = Empty, outward-rounded arithmetic. Friendly interval and fixed-point domains delegate here.

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

// A quantized expression/presentation facade over Interval. Arithmetic never rounds to the
// user grid; the wrapped Interval remains the sole arithmetic authority.
module private FixedPointBoundary =
    let requireQuantum quantum =
        if quantum <= 0m then invalidArg "quantum" "The fixed-point quantum must be positive."

    let inferQuantum (value: decimal) =
        let scale = (System.Decimal.GetBits(value).[3] >>> 16) &&& 0xFF
        let mutable quantum = 1m
        for _ in 1 .. scale do
            quantum <- quantum / 10m
        quantum

    let private decimalFraction (value: decimal) =
        let bits = System.Decimal.GetBits value
        let lo = bigint (uint32 bits.[0])
        let mid = bigint (uint32 bits.[1]) <<< 32
        let hi = bigint (uint32 bits.[2]) <<< 64
        let coefficient = lo + mid + hi
        let scale = (bits.[3] >>> 16) &&& 0x7F
        let numerator = if bits.[3] < 0 then -coefficient else coefficient
        numerator, BigInteger.Pow(10I, scale)

    let private doubleFraction (value: float) =
        let raw = uint64 (System.BitConverter.DoubleToInt64Bits value)
        let negative = (raw >>> 63) <> 0UL
        let exponentBits = int ((raw >>> 52) &&& 0x7FFUL)
        let fractionBits = raw &&& 0x000FFFFFFFFFFFFFUL
        let significand, exponent =
            if exponentBits = 0 then bigint fractionBits, -1074
            else bigint (fractionBits ||| 0x0010000000000000UL), exponentBits - 1023 - 52
        let signed = if negative then -significand else significand
        if exponent >= 0 then signed <<< exponent, 1I
        else signed, 1I <<< -exponent

    let private compareDoubleToDecimal (binary: float) (exact: decimal) =
        if System.Double.IsNaN binary then invalidArg "binary" "NaN has no ordered decimal comparison."
        elif System.Double.IsNegativeInfinity binary then -1
        elif System.Double.IsPositiveInfinity binary then 1
        else
            let binaryNumerator, binaryDenominator = doubleFraction binary
            let decimalNumerator, decimalDenominator = decimalFraction exact
            compare
                (binaryNumerator * decimalDenominator)
                (decimalNumerator * binaryDenominator)

    let literal value =
        let nearest = float value
        match compareDoubleToDecimal nearest value with
        | 0 -> Iv(nearest, nearest)
        | n when n < 0 -> Iv(nearest, System.Math.BitIncrement nearest)
        | _ -> Iv(System.Math.BitDecrement nearest, nearest)

    let private fitsCell quantum gridValue lo hi =
        let half = quantum / 2m
        let lower = gridValue - half
        let upper = gridValue + half
        compareDoubleToDecimal lo lower >= 0
        && compareDoubleToDecimal hi upper < 0

    let tryPoint quantum interval =
        requireQuantum quantum
        match interval with
        | Empty -> None
        | Iv(lo, hi) when System.Double.IsFinite lo && System.Double.IsFinite hi ->
            try
                let center = decimal (lo / 2.0 + hi / 2.0)
                let nearestTick =
                    System.Decimal.Round(center / quantum, 0, System.MidpointRounding.ToEven)
                [ nearestTick - 1m; nearestTick; nearestTick + 1m ]
                |> List.map (fun tick -> tick * quantum)
                |> List.tryFind (fun gridValue -> fitsCell quantum gridValue lo hi)
            with :? System.OverflowException ->
                None
        | _ -> None

    let format quantum interval =
        let decimalText (value: decimal) =
            value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture)
        match interval, tryPoint quantum interval with
        | Empty, _ -> "BOT (contradiction)"
        | _, Some value -> decimalText value
        | Iv(lo, hi), None -> sprintf "[%.17g, %.17g]" lo hi

[<Sealed>]
type FixedPoint private (interval: Interval, quantum: decimal) =
    do FixedPointBoundary.requireQuantum quantum

    new (value: decimal) =
        FixedPoint(
            FixedPointBoundary.literal value,
            FixedPointBoundary.inferQuantum value)

    new (value: decimal, quantum: decimal) =
        FixedPoint(FixedPointBoundary.literal value, quantum)

    member _.Quantum = quantum
    member _.IsBottom = interval = Empty
    member _.TryPoint = FixedPointBoundary.tryPoint quantum interval
    member _.WithQuantum(newQuantum: decimal) = FixedPoint(interval, newQuantum)
    member internal _.Interval = interval

    override _.ToString() = FixedPointBoundary.format quantum interval

    static member internal FromInterval(interval: Interval, quantum: decimal) =
        FixedPoint(interval, quantum)

    static member private SharedQuantum(left: FixedPoint, right: FixedPoint) =
        if left.Quantum <> right.Quantum then
            invalidArg "right" "Fixed-point arithmetic requires matching quanta; use WithQuantum explicitly."
        left.Quantum

    static member (+) (left: FixedPoint, right: FixedPoint) =
        FixedPoint(
            Interval.add left.Interval right.Interval,
            FixedPoint.SharedQuantum(left, right))

    static member (+) (left: FixedPoint, right: decimal) =
        FixedPoint(Interval.add left.Interval (FixedPointBoundary.literal right), left.Quantum)

    static member (+) (left: decimal, right: FixedPoint) =
        FixedPoint(Interval.add (FixedPointBoundary.literal left) right.Interval, right.Quantum)

    static member (-) (left: FixedPoint, right: FixedPoint) =
        FixedPoint(
            Interval.sub left.Interval right.Interval,
            FixedPoint.SharedQuantum(left, right))

    static member (-) (left: FixedPoint, right: decimal) =
        FixedPoint(Interval.sub left.Interval (FixedPointBoundary.literal right), left.Quantum)

    static member (-) (left: decimal, right: FixedPoint) =
        FixedPoint(Interval.sub (FixedPointBoundary.literal left) right.Interval, right.Quantum)

    static member (*) (left: FixedPoint, right: FixedPoint) =
        FixedPoint(
            Interval.mul left.Interval right.Interval,
            FixedPoint.SharedQuantum(left, right))

    static member (*) (left: FixedPoint, right: decimal) =
        FixedPoint(Interval.mul left.Interval (FixedPointBoundary.literal right), left.Quantum)

    static member (*) (left: decimal, right: FixedPoint) =
        FixedPoint(Interval.mul (FixedPointBoundary.literal left) right.Interval, right.Quantum)

    static member (/) (left: FixedPoint, right: FixedPoint) =
        FixedPoint(
            Interval.div left.Interval right.Interval,
            FixedPoint.SharedQuantum(left, right))

    static member (/) (left: FixedPoint, right: decimal) =
        FixedPoint(Interval.div left.Interval (FixedPointBoundary.literal right), left.Quantum)

    static member (/) (left: decimal, right: FixedPoint) =
        FixedPoint(Interval.div (FixedPointBoundary.literal left) right.Interval, right.Quantum)

module Transform =
    let scale (factor: float) = fun value -> value * factor
    let shift (offset: float) = fun value -> value + offset
    let affine factor offset = scale factor >> shift offset

    type Affine =
        { k: float
          b: float }
        member transform.Apply value = transform.k * value + transform.b
        member transform.Inverse = { k = 1.0 / transform.k; b = -transform.b / transform.k }


// ---- The write-once heart: generalized arc consistency over `allows` ----

module internal Gac =

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

module internal StableRandom =

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
