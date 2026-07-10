// propagator-friendly.fsx — a single-face *friendly* surface over the propagator engine.
//
// The engine of tutorial-propagation-part1.fsx / propagator-number-types.fsx is powerful but
// raw: wiring a relation means writing a closure that guards Top/Empty by hand, builds a
// `{ value; support }` contribution literal, threads support out of the cells it read, and ends
// in `AddProp(...) |> ignore`. This file keeps that engine UNCHANGED and puts a friendly face
// over it — domain-shaped *functions and methods* on a `Network`, plus a `network { }` monad as a
// second entry point — so those four frictions disappear behind `Convert` / `Combine` /
// `AllDifferent` and `Assert` / `Retract` / `Value` / `Solve`.
//
// The proof is re-authoring the engine's three worked examples on this surface and reproducing
// their outputs: Celsius<->Fahrenheit (scalar and interval), the barometer (interval fan-in +
// premise retraction), and the 4x4 Sudoku (finite CSP + a dependency-directed Solve driver).
//
// Conversion helpers (scale/shift/affine, the invertible `Affine`) are SUPPLIED from outside the
// surface — see `module Transform` — because the core `Convert` takes only general forward/backward
// functions; it never bakes in a conversion vocabulary. The file carries no `#r`: it is pure F#.
//
// Run end-to-end with:  dotnet fsi propagator-friendly.fsx

open System.Collections.Generic

// =====================================================================================
// The engine — reproduced VERBATIM from tutorial-propagation-part1.fsx (its 7a–7e).
// It knows nothing of temperatures, intervals, or Sudoku; the friendly surface below is a
// thin wrapper, not a change. A cell caches the meet of its contributions (each stamped with
// the premises it rests on); Quiesce fires propagators to fixpoint; Assert records an outside
// fact under a premise; Retract drops every fact resting on a premise.
// =====================================================================================

type Premise = int
type Origin = Ext of Premise | Prop of int
type Contribution<'a> = { value: 'a; support: Set<Premise> }

type Cell<'a> =
    { id: int
      contribs: Dictionary<Origin, Contribution<'a>>
      mutable value: 'a
      mutable support: Set<Premise> }

type Lattice<'a> = { top: 'a; meet: 'a -> 'a -> 'a }

type Propagator<'a> =
    { pid: int
      reads: Cell<'a> list
      fire: unit -> (Cell<'a> * Contribution<'a>) list }

type Engine<'a when 'a : equality>(L: Lattice<'a>) =
    let cells = ResizeArray<Cell<'a>>()
    let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()
    let mutable nCell = 0
    let mutable nProp = 0

    member private _.Recompute (c: Cell<'a>) =
        let mutable v = L.top
        let mutable s = Set.empty
        for kv in c.contribs do
            v <- L.meet v kv.Value.value
            if kv.Value.value <> L.top then s <- Set.union s kv.Value.support
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
                if target.value <> before then wake target

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

// =====================================================================================
// The two lattices the ports use — reproduced from propagator-number-types.fsx.
// The scalar lattice (a point with Top/Bot) for exact numbers; the interval lattice
// (a range, meet = intersection, Bot = empty) for measured/irrational data.
// =====================================================================================

type Scalar<'n> = Top | Val of 'n | Bot

let scalarMeet a b =
    match a, b with
    | Top, x | x, Top         -> x
    | Val x, Val y when x = y -> Val x
    | _                       -> Bot

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
        | Iv(_, a1) when a1 < 0.0 -> Empty
        | Iv(a0, a1) ->
            let lo = if a0 <= 0.0 then 0.0 else down (System.Math.Sqrt a0)
            Iv(lo, up (System.Math.Sqrt a1))

let intervalL = { top = Interval.entire; meet = Interval.meet }

// =====================================================================================
// THE FRIENDLY SURFACE
//
// A `Domain<'a,'p>` describes a value type to the surface: its lattice, how to read a cell's
// value as a *payload* worth propagating (`payload`, None when the cell is uninformative — Top
// or Bot), how to inject a computed payload back (`inject`), a bottom test, and — for finite
// CSP domains — the set operations `AllDifferent`/`Solve` need (`FiniteOps`). The payload is the
// bare number/range the user actually computes with: for a scalar cell it is `'n`, so a user
// writes `fun x -> x*9.0/5.0 + 32.0` on plain floats and never sees `Val`.
// =====================================================================================

type FiniteOps<'a> =
    { universe: 'a
      empty: 'a
      values: int list
      isSingleton: 'a -> bool
      singletonOf: int -> 'a
      contains: int -> 'a -> bool
      remove: 'a -> 'a -> 'a
      union: 'a -> 'a -> 'a }

type Domain<'a, 'p when 'a : equality> =
    { lattice: Lattice<'a>
      payload: 'a -> 'p option
      inject: 'p -> 'a
      isBottom: 'a -> bool
      finite: FiniteOps<'a> option }

/// The friendly network: domain-shaped methods over one `Engine<'a>`. Premises are NAMES here;
/// the surface maps each name to the engine's integer premise and remembers it for retraction
/// and for printing a fact's provenance.
type Network<'a, 'p when 'a : equality>(dom: Domain<'a, 'p>) =
    let e = Engine<'a>(dom.lattice)
    let created = ResizeArray<Cell<'a>>()
    let names = Dictionary<int, string>()          // cell id  -> display name
    let premiseIds = Dictionary<string, int>()     // premise name -> engine premise
    let premiseNames = Dictionary<int, string>()   // engine premise -> name (for provenance display)
    let mutable nextPremise = 0

    let fresh () = let p = nextPremise in nextPremise <- p + 1; p
    let premiseId (name: string) =
        match premiseIds.TryGetValue name with
        | true, p -> p
        | _ -> let p = fresh () in premiseIds.[name] <- p; premiseNames.[p] <- name; p

    /// Introduce a named cell, open at the domain's Top.
    member _.Cell (name: string) =
        let c = e.NewCell ()
        names.[c.id] <- name
        created.Add c
        c

    /// A two-way relation between two cells: `fwd` runs a -> b, `bwd` runs b -> a. Both directions
    /// are installed; each fires only when its source is informative, and each stamps its output
    /// with the source's support. (Hides the Top/Empty guard, the contribution literal, the
    /// support threading, and the `|> ignore`.)
    member _.Convert (a: Cell<'a>, b: Cell<'a>, fwd: 'p -> 'p, bwd: 'p -> 'p) =
        e.AddProp([a], fun () ->
            match dom.payload a.value with
            | Some x -> [ b, { value = dom.inject (fwd x); support = a.support } ]
            | None -> []) |> ignore
        e.AddProp([b], fun () ->
            match dom.payload b.value with
            | Some y -> [ a, { value = dom.inject (bwd y); support = b.support } ]
            | None -> []) |> ignore

    /// A fan-in: several `sources` combine through `f` into one `target`. Fires only when every
    /// source is informative; the target's support is the union of the sources' supports, so a
    /// derived fact remembers every measurement it leaned on (and retracting one removes it).
    member _.Combine (sources: Cell<'a> list, target: Cell<'a>, f: 'p list -> 'p) =
        e.AddProp(sources, fun () ->
            let ps = sources |> List.map (fun c -> dom.payload c.value)
            if List.forall Option.isSome ps then
                let xs = ps |> List.map Option.get
                let sup = sources |> List.map (fun c -> c.support) |> Set.unionMany
                [ target, { value = dom.inject (f xs); support = sup } ]
            else []) |> ignore

    /// All cells in `group` take distinct values (a finite-domain constraint). Installs the two
    /// classic propagators — naked single (a value taken by a solved peer is unavailable here)
    /// and hidden single (a value with a unique home in the group lands there).
    member _.AllDifferent (group: Cell<'a> list) =
        match dom.finite with
        | None -> failwith "AllDifferent requires a finite domain (build the network with Domain.finite)"
        | Some fo ->
            let sup () = group |> List.map (fun p -> p.support) |> Set.unionMany
            e.AddProp(group, fun () ->
                [ for t in group ->
                    let gone =
                        group |> List.fold (fun acc p ->
                            if p.id <> t.id && fo.isSingleton p.value then fo.union acc p.value else acc) fo.empty
                    t, { value = fo.remove fo.universe gone; support = sup () } ]) |> ignore
            e.AddProp(group, fun () ->
                [ for v in fo.values do
                    match group |> List.filter (fun p -> fo.contains v p.value) with
                    | [t] when not (fo.isSingleton t.value) -> yield t, { value = fo.singletonOf v; support = sup () }
                    | _ -> () ]) |> ignore

    /// Assert a fact under an anonymous, permanent premise (never retracted).
    member _.Assert (c: Cell<'a>, x: 'p) = e.Assert(fresh (), c, dom.inject x)
    /// Assert a fact under a NAMED premise — the handle you later `Retract`.
    member _.Assert (premise: string, c: Cell<'a>, x: 'p) = e.Assert(premiseId premise, c, dom.inject x)
    /// Withdraw a named premise; every fact resting on it falls away and the engine re-derives.
    member _.Retract (premise: string) =
        match premiseIds.TryGetValue premise with
        | true, p -> e.Retract p
        | _ -> ()
    /// The current lattice value of a cell.
    member _.Value (c: Cell<'a>) = e.Value c

    /// Complete a finite CSP by dependency-directed backtracking on Assert/Retract: propagation
    /// runs first; if a cell is still open, guess a value under a fresh premise and recurse; on a
    /// contradiction, retract the guess and try the next. (No trail, no search stack of its own —
    /// the guess/backtrack rides the engine's premise machinery. For puzzles that propagation
    /// already finishes, it returns at once having guessed nothing.)
    member _.Solve () : bool =
        match dom.finite with
        | None -> failwith "Solve requires a finite domain (build the network with Domain.finite)"
        | Some fo ->
            let anyBottom () = created |> Seq.exists (fun c -> dom.isBottom c.value)
            let mutable guesses = 0
            let rec go () =
                if anyBottom () then false
                else
                    match created |> Seq.tryFind (fun c -> not (dom.isBottom c.value) && not (fo.isSingleton c.value)) with
                    | None -> true
                    | Some c ->
                        let candidates = fo.values |> List.filter (fun v -> fo.contains v c.value)
                        let rec tryVals = function
                            | [] -> false
                            | v :: rest ->
                                guesses <- guesses + 1
                                let g = premiseId (sprintf "$guess-%d" guesses)
                                e.Assert(g, c, fo.singletonOf v)
                                if go () then true
                                else e.Retract g; tryVals rest
                        tryVals candidates
            go ()

    /// Render a support set with premise names, for provenance printouts.
    member _.ShowSupport (s: Set<Premise>) =
        if Set.isEmpty s then "{}"
        else
            s
            |> Seq.map (fun p -> match premiseNames.TryGetValue p with true, n -> n | _ -> sprintf "p%d" p)
            |> String.concat ", "
            |> sprintf "{%s}"

/// Pre-made domains. Constructors match the surface's three lattices; each fixes `payload` /
/// `inject` so the user computes with bare numbers/ranges, never with the lattice wrapper.
module Domain =
    let scalar<'n when 'n : equality> () : Network<Scalar<'n>, 'n> =
        Network<Scalar<'n>, 'n>
            { lattice = { top = Top; meet = scalarMeet }
              payload = (function Val x -> Some x | _ -> None)
              inject = Val
              isBottom = (function Bot -> true | _ -> false)
              finite = None }

    let interval () : Network<Interval, Interval> =
        Network<Interval, Interval>
            { lattice = intervalL
              payload = (function Empty -> None | iv -> Some iv)
              inject = id
              isBottom = (function Empty -> true | _ -> false)
              finite = None }

    let finite (values: int list) : Network<Set<int>, Set<int>> =
        let universe = Set.ofList values
        if Set.count universe <> List.length values then
            invalidArg "values" "finite domain values must be unique"
        Network<Set<int>, Set<int>>
            { lattice = { top = universe; meet = Set.intersect }
              payload = (fun s -> if Set.isEmpty s then None else Some s)
              inject = id
              isBottom = Set.isEmpty
              finite =
                Some { universe = universe
                       empty = Set.empty
                       values = values
                       isSingleton = (fun s -> Set.count s = 1)
                       singletonOf = Set.singleton
                       contains = Set.contains
                       remove = Set.difference
                       union = Set.union } }

// =====================================================================================
// Second entry point: the `network { }` monad. Same core, same operations — cells are bound
// with `let!` and steps run as `do!` over one network threaded behind the scenes. It is a state
// monad over `Network<'a,'p>`, NOT a custom-operation builder, so it composes with ordinary F#
// (`let`, `for`) freely.
// =====================================================================================

type NetworkBuilder<'a, 'p when 'a : equality>(net: Network<'a, 'p>) =
    member _.Bind (m: Network<'a, 'p> -> 'r, f: 'r -> (Network<'a, 'p> -> 's)) : Network<'a, 'p> -> 's =
        fun n -> f (m n) n
    member _.Return (x: 'r) : Network<'a, 'p> -> 'r = fun _ -> x
    member _.ReturnFrom (m: Network<'a, 'p> -> 'r) : Network<'a, 'p> -> 'r = m
    member _.Zero () : Network<'a, 'p> -> unit = fun _ -> ()
    member _.Combine (m1: Network<'a, 'p> -> unit, m2: Network<'a, 'p> -> 'r) : Network<'a, 'p> -> 'r =
        fun n -> m1 n; m2 n
    member _.Delay (f: unit -> (Network<'a, 'p> -> 'r)) : Network<'a, 'p> -> 'r = fun n -> (f ()) n
    member _.For (xs: seq<'x>, f: 'x -> (Network<'a, 'p> -> unit)) : Network<'a, 'p> -> unit =
        fun n -> for x in xs do f x n
    member _.Run (m: Network<'a, 'p> -> 'r) : 'r = m net

let network (net: Network<'a, 'p>) = NetworkBuilder<'a, 'p>(net)

/// The monad's operations — each is just a call against the threaded network.
module Ops =
    let cell (name: string) : Network<'a, 'p> -> Cell<'a> = fun n -> n.Cell name
    let convert (a: Cell<'a>) (b: Cell<'a>) (fwd: 'p -> 'p) (bwd: 'p -> 'p) : Network<'a, 'p> -> unit =
        fun n -> n.Convert(a, b, fwd, bwd)
    let combine (sources: Cell<'a> list) (target: Cell<'a>) (f: 'p list -> 'p) : Network<'a, 'p> -> unit =
        fun n -> n.Combine(sources, target, f)
    let allDifferent (group: Cell<'a> list) : Network<'a, 'p> -> unit = fun n -> n.AllDifferent group
    let assume (premise: string) (c: Cell<'a>) (x: 'p) : Network<'a, 'p> -> unit = fun n -> n.Assert(premise, c, x)
    let given (c: Cell<'a>) (x: 'p) : Network<'a, 'p> -> unit = fun n -> n.Assert(c, x)
    let retract (premise: string) : Network<'a, 'p> -> unit = fun n -> n.Retract premise
    let read (c: Cell<'a>) : Network<'a, 'p> -> 'a = fun n -> n.Value c
    let solve : Network<'a, 'p> -> bool = fun n -> n.Solve ()

// =====================================================================================
// SUPPLIED conversion helpers — external to the surface (an example/consumer module).
// The core `Convert` takes general fwd/bwd functions; it does not define a conversion vocabulary.
// `Affine` is the invertible seam: author the relation once as { k; b } and get both directions —
// `.Apply` forward, `.Inverse.Apply` backward — from one transform value.
// =====================================================================================

module Transform =
    let scale (k: float) = fun (x: float) -> x * k
    let shift (b: float) = fun (x: float) -> x + b
    let affine (k: float) (b: float) = scale k >> shift b

    type Affine =
        { k: float; b: float }
        member a.Apply (x: float) = a.k * x + a.b
        member a.Inverse = { k = 1.0 / a.k; b = -a.b / a.k }

// =====================================================================================
// PORT 1 — Celsius <-> Fahrenheit at scalar float, methods form.
// (a) The raw conversions reproduce propagator-number-types.fsx §1 exactly: asserting 1 °C
//     fabricates a spurious Bot on float, because the split (y-32)*5/9 does not round back to 1.
// =====================================================================================

let demoCelsiusScalar () =
    let net = Domain.scalar<float> ()
    let cC = net.Cell "C"
    let fF = net.Cell "F"
    net.Convert(cC, fF, (fun x -> x*9.0/5.0 + 32.0), (fun y -> (y-32.0)*5.0/9.0))
    net.Assert(cC, 1.0)
    net, cC, fF

// (b) The SUPPLIED Affine helper: author the relation once as { k; b } and take its inverse for
//     the backward direction. The fused k*x+b and its algebraic inverse round DIFFERENTLY from
//     the split conversions above — and here that difference makes the round trip land back on
//     1.0 exactly, so the surface reports C = 1 rather than §1's spurious Bot. Same network, same
//     lattice; only the arithmetic the two supplied functions perform has changed.
let demoCelsiusScalarAffine () =
    let net = Domain.scalar<float> ()
    let cC = net.Cell "C"
    let fF = net.Cell "F"
    let cToF : Transform.Affine = { k = 9.0 / 5.0; b = 32.0 } // authored once; inverse is derived
    net.Convert(cC, fF, cToF.Apply, cToF.Inverse.Apply)
    net.Assert(cC, 1.0)
    net, cC, fF

// =====================================================================================
// PORT 2 — Celsius <-> Fahrenheit at interval, the network { } monad.
// Reproduces §4(a): asserting C=[1,1] settles instead of fabricating a Bot.
// =====================================================================================

let demoCelsiusInterval () =
    network (Domain.interval ()) {
        let! cC = Ops.cell "C"
        let! fF = Ops.cell "F"
        let cToF iv = Interval.add (Interval.div (Interval.mul iv (Interval.pt 9.0)) (Interval.pt 5.0)) (Interval.pt 32.0)
        let fToC iv = Interval.div (Interval.mul (Interval.sub iv (Interval.pt 32.0)) (Interval.pt 5.0)) (Interval.pt 9.0)
        do! Ops.convert cC fF cToF fToC
        do! Ops.given cC (Interval.pt 1.0)
        return! (fun (n: Network<Interval, Interval>) -> n.Value cC, n.Value fF)
    }

// =====================================================================================
// PORT 3 — the barometer at interval, methods form: fan-in of redundant methods + retraction.
// Reproduces §5. One Convert (the two-way fall relation) + one Combine (the one-way shadow
// method) replace the four hand-written propagators, and the support union is automatic.
// =====================================================================================

let g    = Interval.pt 9.8
let half = Interval.pt 0.5
let two  = Interval.pt 2.0
let heightFromFall t = Interval.mul half (Interval.mul g (Interval.mul t t))
let fallFromHeight h = Interval.sqrt (Interval.div (Interval.mul two h) g)
let heightFromShadow bh bShadow bldShadow = Interval.div (Interval.mul bh bldShadow) bShadow

let showRange = function
    | Empty -> "BOT (no consistent height)"
    | Iv(lo, hi) -> sprintf "[%.6g, %.6g]" lo hi

let demoBarometer () =
    let net = Domain.interval ()
    let t         = net.Cell "t"
    let h         = net.Cell "h"
    let bh        = net.Cell "bh"
    let bShadow   = net.Cell "bShadow"
    let bldShadow = net.Cell "bldShadow"
    net.Convert(t, h, heightFromFall, fallFromHeight)                    // t <-> h, both ways
    net.Combine([bh; bShadow; bldShadow], h, fun xs ->                   // (bh, bShadow, bldShadow) -> h
        match xs with [a; b; c] -> heightFromShadow a b c | _ -> Interval.entire)
    let report (label: string) =
        printfn "  %s" label
        printfn "      height    = %-28s support %s" (showRange h.value) (net.ShowSupport h.support)
        printfn "      fall-time = %-28s support %s" (showRange t.value) (net.ShowSupport t.support)
    net.Assert("stopwatch", t, Iv(3.0, 3.2))
    report "after stopwatch  t = [3.0, 3.2] s:"
    net.Assert("shadows", bh,        Iv(0.30, 0.30))
    net.Assert("shadows", bShadow,   Iv(0.30, 0.32))
    net.Assert("shadows", bldShadow, Iv(45.0, 48.0))
    report "after shadows added:"
    net.Assert("super", h, Iv(49.0, 49.0))
    report "after superintendent says h = 49 m:"
    net.Retract "shadows"
    report "after retracting the shadow measurement:"

// =====================================================================================
// PORT 4 — the 4x4 Sudoku at finite [1..4], methods form. The whole reason for methods over a
// custom-operation CE: the twelve AllDifferent groups are installed with a plain `for` loop,
// which a custom-op builder would forbid.
// =====================================================================================

let givens = [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]

let unitCoords : (int*int) list list =
    [ for r in 0..3 -> [ for c in 0..3 -> r, c ] ]
  @ [ for c in 0..3 -> [ for r in 0..3 -> r, c ] ]
  @ [ for br in 0..1 do for bc in 0..1 -> [ for dr in 0..1 do for dc in 0..1 -> 2*br+dr, 2*bc+dc ] ]

let demoSudoku () =
    let net = Domain.finite [1..4]
    let grid = Array2D.init 4 4 (fun r c -> net.Cell (sprintf "r%dc%d" r c))
    let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]
    for u in unitCoords do
        net.AllDifferent (at u)
    givens |> List.iter (fun ((r, c), v) -> net.Assert(grid.[r, c], Set.singleton v))
    let ok = net.Solve ()
    let digit (d: Set<int>) = if Set.count d = 1 then Set.minElement d else 0
    let solved = [| for r in 0..3 -> [| for c in 0..3 -> digit (net.Value grid.[r, c]) |] |]
    ok, solved

// =====================================================================================
// Run — print each port's result to eyeball against the originals.
// =====================================================================================

let showScalar = function
    | Top -> "Top"
    | Bot -> "BOT (contradiction)"
    | Val (v: float) -> sprintf "%.17g" v

let showIv = function
    | Empty -> "BOT (contradiction)"
    | Iv(lo, hi) -> sprintf "[%.17g, %.17g]" lo hi

let main () =
    printfn "runtime: %s" (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
    printfn ""
    printfn "== 1. C<->F, scalar float (methods) =="
    let n1, cC1, fF1 = demoCelsiusScalar ()
    printfn "  raw  x*9/5+32 / (y-32)*5/9:  assert C=1   C = %s,  F = %s" (showScalar (n1.Value cC1)) (showScalar (n1.Value fF1))
    let n1b, cC1b, fF1b = demoCelsiusScalarAffine ()
    printfn "  supplied Affine {k;b}+Inverse: assert C=1   C = %s,  F = %s" (showScalar (n1b.Value cC1b)) (showScalar (n1b.Value fF1b))
    printfn ""
    printfn "== 2. C<->F, interval (network { } monad) =="
    let cV, fV = demoCelsiusInterval ()
    printfn "  assert C=[1,1]    C = %s,  F = %s" (showIv cV) (showIv fV)
    printfn ""
    printfn "== 3. barometer, interval (methods; fan-in + retraction) =="
    demoBarometer ()
    printfn ""
    printfn "== 4. 4x4 Sudoku, finite (methods; for-loop AllDifferent + Solve) =="
    let ok, grid = demoSudoku ()
    printfn "  Solve() = %b" ok
    for r in grid do printfn "   %A" (List.ofArray r)

main ()
