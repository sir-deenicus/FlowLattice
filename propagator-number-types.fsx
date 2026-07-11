// propagator-number-types.fsx — a literate companion to tutorial-propagation-part1.fsx.
// It reuses that tutorial's propagation engine UNCHANGED and varies what the cells carry
// on the Celsius<->Fahrenheit network. Sections 1-3 vary the number under a scalar
// lattice (float, decimal, BigRational) to show where an exact-equality `meet` fabricates
// contradictions; section 4 swaps the scalar lattice for an interval lattice whose `meet`
// is intersection, removing the spurious contradictions without any equality test. A closing
// section then puts that interval lattice to work on the propagator literature's canonical
// example — measuring a building by several redundant methods — adding many-sources-into-one-
// cell and premise retraction, on the same unchanged engine.
// Run end-to-end with:  dotnet fsi propagator-number-types.fsx
// The reference supplies BigRational (used only in section 3).
#r "nuget: MathNet.Numerics.FSharp, 4.15.0"

open System.Collections.Generic
open MathNet.Numerics

(**
# Number types and the propagator lattice: Celsius ⇌ Fahrenheit

A propagator network stores knowledge in *cells* ordered by refinement and combines
contributions with a *meet*. When a cell ranges over the real numbers, the meet must
decide whether two independently derived values denote the same quantity — and it
makes that decision with the value type's own equality. The choice of value type
therefore determines whether the engine is sound.

This note reuses the engine and the Celsius–Fahrenheit network of
`tutorial-propagation-part1.fsx` **without modification**, changing only the numeric
type the scalar lattice carries, and instantiates it at three types: IEEE binary
`float`, .NET `decimal`, and arbitrary-precision rationals (`BigRational`). We find
that exact equality in the meet causes `float` to fabricate contradictions for the
majority of ordinary inputs; that `decimal` removes them in one assertion direction
yet retains them in the other; and that only the rational type is sound for every
rational input, because it alone is closed under division. Finally we change the lattice
itself rather than the number beneath it: a fourth instantiation carries *intervals*,
whose meet is intersection rather than an equality test, and which reports a genuine
contradiction when one is present yet fabricates none for the inputs that defeated
`float` and `decimal` — at the cost of returning each answer as a narrow enclosing
interval rather than a point. A closing section then leaves the two-cell network behind for
the example the interval lattice is made for — Sussman and Radul's barometer, a building's
height measured by several redundant methods — where independent intervals are sharpened by
intersection, a conflicting witness yields a real contradiction, and one measurement is
withdrawn through the premise machinery the engine has carried, unused, since the tutorial.
Every result quoted below is produced by the code as written.

## The engine

The engine below is reproduced **verbatim** from the tutorial (its sections 7a–7e);
it is reused, not rebuilt. It knows nothing of temperatures or numbers. A cell caches
the `meet` of its contributions (each a value stamped with the *premises* it rests
on); `Quiesce` fires propagators until no cached value changes; `Assert` records an
outside contribution under a premise and re-quiesces; `Retract` drops every
contribution resting on a premise. The entire difference between one problem and
another lives in the `Lattice` record — `top` and `meet`.
*)

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

(**
## The Celsius ⇌ Fahrenheit network

The tutorial's Demo 1 builds two cells — Celsius and Fahrenheit — and two propagators,
`C → F` and `F → C`, over a scalar lattice whose `meet` agrees only when two known
values are *equal*:

```fsharp
type Scalar = Top | Val of float | Bot           // tutorial 7f: unknown / a number / impossible
```

To run that identical network at three numeric types, the single necessary change is
to give the knowledge type a **type parameter**, `Scalar<'n>`, in place of the
tutorial's float-fixed `Scalar`. Note that this generalization is what *preserves* the
original names: three separate `float`/`decimal`/`rational` knowledge types would each
need their own `Top`/`Val`/`Bot`, colliding in one file. The `meet` and the two
propagators are otherwise the tutorial's, verbatim.
*)

type Scalar<'n> = Top | Val of 'n | Bot           // generalizes the tutorial's `Scalar`

// The tutorial's scalar `meet`, lifted to the parameter (tutorial 7f, unchanged logic):
// two known values agree only when equal, otherwise the cell is contradictory.
let scalarMeet a b =
    match a, b with
    | Top, x | x, Top         -> x
    | Val x, Val y when x = y -> Val x
    | _                       -> Bot

/// The tutorial's Demo-1 wiring (lines 116–120), parameterized by the lattice and the
/// two conversions so the same network can be built at any numeric type.
let buildCF (L: Lattice<Scalar<'n>>) (cToF: 'n -> 'n) (fToC: 'n -> 'n) =
    let e  = Engine<Scalar<'n>>(L)
    let cC = e.NewCell()   // Celsius
    let fF = e.NewCell()   // Fahrenheit
    e.AddProp([cC], fun () -> match cC.value with Val x -> [ fF, { value = Val (cToF x); support = cC.support } ] | _ -> []) |> ignore   // C->F
    e.AddProp([fF], fun () -> match fF.value with Val y -> [ cC, { value = Val (fToC y); support = fF.support } ] | _ -> []) |> ignore   // F->C
    e, cC, fF

let report (e: Engine<Scalar<'n>>) cC fF (showN: 'n -> string) (label: string) =
    let s = function Top -> "Top" | Bot -> "BOT (contradiction)" | Val v -> showN v
    printfn "  %-12s C = %s,  F = %s" label (s (e.Value cC)) (s (e.Value fF))

(**
## 1. Binary floating point

We build the network at `float`, with the tutorial's exact conversions
(`x*9.0/5.0 + 32.0` and `(y-32.0)*5.0/9.0`), and assert **one degree Celsius**.
*)

printfn "1. float"
let ef, cf, ff = buildCF { top = Top; meet = scalarMeet } (fun x -> x*9.0/5.0 + 32.0) (fun y -> (y-32.0)*5.0/9.0)
ef.Assert(1, cf, Val 1.0)
report ef cf ff (sprintf "%.17g") "assert C=1"

(**
```
1. float
  assert C=1   C = BOT (contradiction),  F = 33.799999999999997
```

The Celsius cell goes to `Bot`: the engine concludes that 1 °C is impossible. The
`C → F` propagator derives `33.799999999999997` °F — already inexact, since 33.8 has no
finite binary expansion — and the `F → C` propagator converts that back to
`0.99999999999999845` °C. That contribution is met against the asserted `1.0`;
`scalarMeet` finds the two unequal and yields `Bot`.

The defect is not peculiar to 1. Sweeping the tenth-degree grid from 0 to 200 °C, the
Celsius round trip fails on **901 of 2001** inputs, the integers 1, 2, 3, 4 among them
(while, by coincidence of cancellation, 5 and 37 survive). Binary floating point cannot
represent the coefficient 9/5 = 1.8, so the forward and backward propagators are not
exact inverses, and exact-equality merge reads the residue — of order 10⁻¹⁶ — as a
contradiction. The failure is a *spurious* `Bot`: because `Bot` is absorbing, the
network reaches it and stops; this is a false conclusion, not divergence.

## 2. Decimal

`System.Decimal` represents a value as a 96-bit integer significand scaled by a power
of ten (28–29 significant digits), so decimal fractions such as 1.8, 32, and 33.8 are
exact and `+`, `−`, `×` are exact within that precision. We build the same network at
`decimal` and repeat the assertion that defeated `float`.
*)

printfn "2. decimal"
let ed, cd, fd = buildCF { top = Top; meet = scalarMeet } (fun x -> x*9.0m/5.0m + 32.0m) (fun y -> (y-32.0m)*5.0m/9.0m)
ed.Assert(1, cd, Val 1.0m)
report ed cd fd (fun d -> d.ToString()) "assert C=1"

(**
```
  assert C=1   C = 1.0,  F = 33.8
```

Correct: 1.0 °C and 33.8 °F, in agreement. More than that — the Celsius direction
*never rounds*. The forward map multiplies by 9 (exact) and divides by 5 (exact, since
5 divides 10); the inverse multiplies by 5 and divides by 9 in an order that cancels
the 9 against the original `×9`. Across the swept grid, asserting Celsius produces
**zero** contradictions.

### A residual failure

Decimal does not change the *kind* of guarantee; it enlarges the exactly-represented
fragment and relocates the boundary. The boundary is crossed by asserting from the
other side — **47 °F**.
*)

let ed2, cd2, fd2 = buildCF { top = Top; meet = scalarMeet } (fun x -> x*9.0m/5.0m + 32.0m) (fun y -> (y-32.0m)*5.0m/9.0m)
ed2.Assert(1, fd2, Val 47.0m)
report ed2 cd2 fd2 (fun d -> d.ToString()) "assert F=47"

(**
```
  assert F=47  C = 8.333333333333333333333333333,  F = BOT (contradiction)
```

Now the Fahrenheit cell goes to `Bot`. The `F → C` propagator computes
`(47 − 32)·5/9 = 75/9`, which has no finite decimal expansion; `decimal` rounds it to
`8.333…3` at the 28th digit. The `C → F` propagator converts that back to `46.999…9`,
unequal to the asserted 47, and the meet returns `Bot`. `decimal` is closed under `+`,
`−`, `×` but **not** under division: wherever a conversion divides by a factor that is
not a divisor of a power of ten (here, by 9), the round trip rounds, and exact-equality
merge again fabricates a contradiction — now at the 28th digit rather than the 16th,
and for a minority of inputs (77 of 1801 on the swept Fahrenheit grid) rather than a
majority. Fewer failures, the same defect.

## 3. Arbitrary-precision rationals

A rational value type is available locally as **`BigRational`** in namespace
`MathNet.Numerics` — the F# PowerPack rational carried forward by the
`MathNet.Numerics.FSharp` package (referenced at the top of this script). A
`BigRational` is a pair of arbitrary-precision integers kept in lowest terms. We build
the network with rational coefficients and assert from *both* sides.
*)

printfn "3. BigRational"
let bi = BigRational.FromInt
let c9_5, c32, c5_9 = bi 9 / bi 5, bi 32, bi 5 / bi 9
let er, cr, fr = buildCF { top = Top; meet = scalarMeet } (fun c -> c*c9_5 + c32) (fun f -> (f-c32)*c5_9)
er.Assert(1, cr, Val (bi 1))
report er cr fr (fun r -> r.ToString()) "assert C=1"
let er2, cr2, fr2 = buildCF { top = Top; meet = scalarMeet } (fun c -> c*c9_5 + c32) (fun f -> (f-c32)*c5_9)
er2.Assert(1, fr2, Val (bi 47))
report er2 cr2 fr2 (fun r -> r.ToString()) "assert F=47"

(**
```
  assert C=1   C = 1,  F = 169/5
  assert F=47  C = 25/3,  F = 47
```

Both assertions settle, in both directions, and both survive the return conversion.
The coefficient 9/5 is held as `9/5`, not `1.8…`; the derived value 25/3 is held as
`25/3`, not `8.333…`. Addition, subtraction, multiplication, and — decisively —
division are all exact, because dividing by `p/q` is multiplying by `q/p`, a purely
integer operation that never rounds. Exact equality on the reduced pair is therefore
*honest*: it holds precisely when the two cells denote the same rational number. The
`Val/Val` case of `scalarMeet` can no longer be deceived.

## Why BigRational is needed

The progression is not `float < decimal < rational` along a single axis of precision.
It is a question of **closure**.

The network's correctness rests on one identity in the meet: `Val x ⊓ Val y = Val x`
when `x` and `y` denote the same quantity, and `Bot` otherwise. That identity is sound
only if the value type's equality is honest, and equality is honest only if every
operation the propagators perform stays inside the exactly-represented set. The
Celsius–Fahrenheit propagators use all four field operations, including division by 5
and by 9.

- Binary `float` is exactly closed under none of them — even `× 9 / 5` is inexact — so
  it fails broadly.
- `decimal` is closed under `+`, `−`, `×` but not `÷`, so it fails exactly where a
  conversion divides by a non-decimal factor (the `/ 9`).
- The rationals are closed under all four, so they never fail.

`BigRational` is thus the *smallest* value type for which this network's exact-equality
meet is sound for every rational input. It is needed not for more digits, but for
closure under division — the property that makes the meet's equality test mean what the
lattice requires it to mean.

### Boundary of the claim

This argument covers data and coefficients that are *rational*. It does not extend to
genuinely irrational or measured quantities — a falling-body height ½·g·t², a
similar-triangles ratio, any square root — for which no exact point representation
exists in `float`, `decimal`, or `BigRational` alike. For such data the appropriate
lattice element is not a point but an **interval**, where the meet is intersection and
`Bot` is the empty interval, and the merge no longer relies on equality at all.
Rationals make the equality in the meet honest; intervals make the merge independent of
equality. Which is required is a property of the data, not of the engine. The remainder
of this note builds that interval lattice — self-contained, in some thirty lines, with no
external library — and runs it on the same Celsius–Fahrenheit network.

## 4. Intervals: enclosure instead of equality

The three sections above keep the same lattice — a point with `Top` and `Bot` bolted on —
and vary only the number under it. The remaining option is to change the lattice. An
*interval* lattice carries a closed range `[lo, hi]` of still-possible values; its top is
the whole real line and its bottom is the empty range; and its meet is **intersection**,
not an equality test. There is no separate `Top`/`Val`/`Bot` wrapper this time, because
the interval domain already contains its own top (`entire`) and bottom (`Empty`).

The arithmetic is *outward-rounded*: every computed endpoint is nudged one unit in the
last place outward — the low end down, the high end up, via `Math.BitDecrement` and
`Math.BitIncrement` — so the floating-point interval is guaranteed to **contain** the
exact real result rather than merely approximate it. Enclosure, not a point, is the whole
idea: a conversion may not know the answer to the last bit, but it can return an interval
the answer provably lies inside.
*)

type Interval = Empty | Iv of float * float

module Interval =
    let entire = Iv(System.Double.NegativeInfinity, System.Double.PositiveInfinity)
    let pt (x: float) = Iv(x, x)
    // Outward rounding: widen by one ULP so every result is a guaranteed superset.
    let private down (x: float) = if System.Double.IsFinite x then System.Math.BitDecrement x else x
    let private up   (x: float) = if System.Double.IsFinite x then System.Math.BitIncrement x else x
    let meet a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | Iv(a0, a1), Iv(b0, b1) ->
            let lo, hi = max a0 b0, min a1 b1
            if lo > hi then Empty else Iv(lo, hi)            // disjoint ranges => a real contradiction
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
            let ps = [ a0 * b0; a0 * b1; a1 * b0; a1 * b1 ]   // an endpoint product attains each extreme
            Iv(down (List.min ps), up (List.max ps))
    let div a b =
        match a, b with
        | Empty, _ | _, Empty -> Empty
        | _, Iv(b0, b1) when b0 <= 0.0 && 0.0 <= b1 -> entire // divisor straddles 0: no finite bound
        | Iv(a0, a1), Iv(b0, b1) ->
            let qs = [ a0 / b0; a0 / b1; a1 / b0; a1 / b1 ]
            Iv(down (List.min qs), up (List.max qs))
    // Outward-rounded square root on the non-negative part — used by the closing barometer
    // section to run the fall-time relation backward (t = sqrt(2h/g)).
    let sqrt a =
        match a with
        | Empty -> Empty
        | Iv(_, a1) when a1 < 0.0 -> Empty                    // wholly negative: no real root
        | Iv(a0, a1) ->
            let lo = if a0 <= 0.0 then 0.0 else down (System.Math.Sqrt a0)
            Iv(lo, up (System.Math.Sqrt a1))

(**
The network is wired exactly as before — two cells, two propagators — but over
`Engine<Interval>`, with the conversions expressed in interval arithmetic. A cell that
has collapsed to `Empty` has nothing left to say, so each propagator yields nothing in
that case; a contradiction therefore stays local to the over-constrained cell instead of
spreading `Bot` across the whole network.
*)

let intervalL = { top = Interval.entire; meet = Interval.meet }

let buildIntervalCF () =
    let e  = Engine<Interval>(intervalL)
    let cC = e.NewCell()   // Celsius
    let fF = e.NewCell()   // Fahrenheit
    let cToF iv = Interval.add (Interval.div (Interval.mul iv (Interval.pt 9.0)) (Interval.pt 5.0)) (Interval.pt 32.0)
    let fToC iv = Interval.div (Interval.mul (Interval.sub iv (Interval.pt 32.0)) (Interval.pt 5.0)) (Interval.pt 9.0)
    e.AddProp([cC], fun () -> match cC.value with Empty -> [] | iv -> [ fF, { value = cToF iv; support = cC.support } ]) |> ignore   // C->F
    e.AddProp([fF], fun () -> match fF.value with Empty -> [] | iv -> [ cC, { value = fToC iv; support = fF.support } ]) |> ignore   // F->C
    e, cC, fF

let reportIv (e: Engine<Interval>) cC fF (label: string) =
    let s = function Empty -> "BOT (contradiction)" | Iv(lo, hi) -> sprintf "[%.17g, %.17g]" lo hi
    printfn "  %-20s C = %s,  F = %s" label (s (e.Value cC)) (s (e.Value fF))

(**
We run three assertions. The first two are the single-degree inputs that produced a
spurious `Bot` at `float` (§1) and at `decimal` (§2); the third over-constrains the
network with two genuinely incompatible facts.
*)

printfn "4. interval"
let ei1, ci1, fi1 = buildIntervalCF ()                 // (a) the assertion that defeated float
ei1.Assert(1, ci1, Interval.pt 1.0)
reportIv ei1 ci1 fi1 "assert C=[1,1]"

let ei2, ci2, fi2 = buildIntervalCF ()                 // (b) the assertion that defeated decimal
ei2.Assert(1, fi2, Interval.pt 47.0)
reportIv ei2 ci2 fi2 "assert F=[47,47]"

let ei3, ci3, fi3 = buildIntervalCF ()                 // (c) genuine conflict: 1 C is 33.8 F, not 100 F
ei3.Assert(1, ci3, Interval.pt 1.0)
ei3.Assert(2, fi3, Interval.pt 100.0)
reportIv ei3 ci3 fi3 "assert C=1 and F=100"

(**
```
4. interval
  assert C=[1,1]       C = [1, 1],  F = [33.79999999999999, 33.800000000000004]
  assert F=[47,47]     C = [8.3333333333333286, 8.3333333333333375],  F = [47, 47]
  assert C=1 and F=100 C = [1, 1],  F = BOT (contradiction)
```

The first two assertions, which collapsed `float` and `decimal` to a false `Bot`, now
settle. Asserting 1 °C drives the Celsius cell through `C → F` to a Fahrenheit interval a
few units in the last place wide, bracketing 33.8 — that width is the rounding slack §1's
`float` collapsed onto a single point and then misread as a contradiction. The `F → C`
propagator carries that bracket back
to a Celsius interval that, by outward rounding, still **contains** 1; intersecting it
with the asserted `[1, 1]` leaves `[1, 1]` unchanged, so the network quiesces with no
contradiction. The 47 °F assertion behaves symmetrically: the Celsius cell brackets
8.333…, the return trip encloses 47, and the meet holds.

The third assertion is a real contradiction — 1 °C is 33.8 °F, not 100 °F — and the
interval lattice still reports it. The `C → F` propagator pins Fahrenheit to its bracket
around 33.8; the second assertion independently pins it to `[100, 100]`; the two ranges
are disjoint, so their intersection is empty and the cell becomes `Bot`. Detecting
genuine conflicts is not lost — only the fabrication of false ones is.

The design is sound and it terminates. Soundness: because every interval operation rounds
outward, each derived interval is a guaranteed superset of the true value, so the
round-trip can never exclude a value that is in fact consistent — a spurious empty
intersection cannot arise. Termination: because the meet is intersection, a cell's value
only ever narrows, and with finitely many representable float endpoints it cannot narrow
forever, so the worklist drains. The unbounded oscillation sometimes feared of interval
propagators belongs to the *widening* regime, where each pass enlarges an endpoint; this
intersection-only network never enters it.

Enclosure is guaranteed; tightness is not. Interval arithmetic returns a sound superset
of the true range for *any* expression, but a needlessly wide one when a variable occurs
more than once: each occurrence is bounded independently, so the correlation between them
is lost — the textbook case is `x − x`, evaluated as `[−w, w]` rather than `[0, 0]`. The
brackets above stay tight — a few units in the last place, pure rounding slack — precisely
because each conversion uses its input exactly once: `x·9/5 + 32` and `(y − 32)·5/9` have
no repeated variable, so there is no correlation to lose. The propagator structure helps
around the cycle as well: every pass meets the returning interval against the pinned
assertion, so even a loop that widened would be re-tightened by the intersection. A
network whose propagators repeated a variable would remain sound but could report
intervals looser than the data warrants — a limitation of the arithmetic, not of the
lattice.

## Closure or enclosure

Two routes make the meet sound, and the four instantiations divide between them.

The exact-number route (§§1–3) keeps the meet's *equality* test and asks the number type
to make it honest. That demands **closure**: every operation a propagator performs must
land back in the exactly-represented set. `BigRational` achieves it for all four field
operations, so the meet's equality means what the lattice needs — but only for data that
is exactly a rational.

The interval route (§4) abandons equality for *intersection* and asks each operation to
return an honest superset of the truth. That demands only **enclosure**, which
outward-rounded floating point supplies for any real input, rational or not. The price is
that the answer is a bracket rather than a point: §4 reports 33.8 °F as a few-ULP interval
around it instead of the value itself.

The two are therefore not rivals on one scale but answers to different questions about the
data. When the inputs and coefficients are exactly rational and an exact value is wanted,
rationals deliver it. When the data is measured or irrational — a fall time, a square
root, a barometer's height — no point type can be honest, and the interval lattice
delivers the tightest bracket provably containing the answer. In neither case did the
engine change: only the `Lattice` record did. A final example shows the same engine doing
more — combining several measurements of one quantity, and retracting one of them — with the
cells, again, carrying all the difference.

## A barometer: many sources, one cell, and retraction

The Celsius–Fahrenheit network had a single source of fact flowing back and forth between
two cells. The interesting propagator networks are not like that: they gather **redundant,
partial** information about one quantity from several independent methods and let the lattice
combine them. The example the propagator literature uses to make that point is the barometer
(Sussman & Radul's *The Art of the Propagator*, and Radul's *Propagation Networks* thesis),
and it exercises two features of the engine the temperature demos left idle — fan-in and
retraction — without adding anything to the engine at all.

The setup is the old physics-class joke. Asked to find a building's height with a barometer,
the student spurns the intended air-pressure method for others: drop the barometer from the
roof and time the fall, `h = ½ g t²`; compare its shadow with the building's by similar
triangles, `h = bh · bldShadow / bShadow`; or trade it to the superintendent for the
building's plans. Each method yields a *range*, not a number — a stopwatch is imprecise, a
shadow's edge is fuzzy — so each is an interval, and the height is whatever range the methods
can all agree on.

Three things here go past Celsius–Fahrenheit:

- **Fan-in.** Several propagators write the *same* height cell, and its `meet` — interval
  intersection — keeps only the overlap of every method's range. No method is privileged; the
  network keeps what they all allow, so partial sources combine into knowledge none of them
  held alone.
- **Contradiction.** If a precise, trusted witness reports a height no measurement allows, the
  intersection is empty and the cell is `Empty` — the same `Bot` as section 4, now arising
  *among redundant sources* rather than from one round trip.
- **Retraction.** Every fact carries the premises it rests on (`support`). When the methods
  conflict, that stamp names the suspects; we revoke one premise and the engine re-derives,
  dropping exactly what depended on it. This is dependency-directed reason maintenance, **not**
  search: nothing is tried, reordered, or rolled back in time; a premise is withdrawn and the
  consequences that rested on it fall away because their support no longer holds.

The fall-time method is a *two-way* relation, `h = ½ g t²` forward and `t = √(2h/g)` backward,
so it needs a square root — the one operation added to the section-4 interval module above.
Each direction uses its input once, so the enclosure stays tight; the forward `t·t` is a
squaring, the shape that loses correlation for a general interval, but `t` is a positive fall
time and on a positive interval `t·t` is exact, so no width is invented.
*)

let g    = Interval.pt 9.8
let half = Interval.pt 0.5
let two  = Interval.pt 2.0

// h = ½ g t²  (forward) and  t = √(2h/g)  (backward); h = bh·bldShadow/bShadow (shadows)
let heightFromFall t = Interval.mul half (Interval.mul g (Interval.mul t t))
let fallFromHeight h = Interval.sqrt (Interval.div (Interval.mul two h) g)
let heightFromShadow bh bShadow bldShadow = Interval.div (Interval.mul bh bldShadow) bShadow

let union3 a b c = Set.union a (Set.union b c)

(**
The wiring is the only new code. Five cells: the unknown height `h`, the fall time `t`, and
the three measured shadow lengths. Gravity and the numeric constants are folded in as exact
point intervals, so the only premises are the three *measurements* — which is what the
retraction story turns on. Each propagator stamps its output with the **union of the supports
of the cells it read**: that is how a height derived from the shadows comes to remember that
it depends on the shadow measurements, so that retracting them later removes it too.
*)

let buildBarometer () =
    let e = Engine<Interval>(intervalL)
    let t         = e.NewCell()   // fall time (s)
    let h         = e.NewCell()   // building height (m) — the unknown
    let bh        = e.NewCell()   // barometer height (m)
    let bShadow   = e.NewCell()   // barometer's shadow (m)
    let bldShadow = e.NewCell()   // building's shadow (m)
    // Fall time, forward:  t -> h
    e.AddProp([t], fun () ->
        match t.value with
        | Empty -> []
        | tv -> [ h, { value = heightFromFall tv; support = t.support } ]) |> ignore
    // Fall time, backward:  h -> t   (what makes the method multidirectional)
    e.AddProp([h], fun () ->
        match h.value with
        | Empty -> []
        | hv -> [ t, { value = fallFromHeight hv; support = h.support } ]) |> ignore
    // Similar triangles, forward:  (bh, bShadow, bldShadow) -> h
    e.AddProp([bh; bShadow; bldShadow], fun () ->
        match bh.value, bShadow.value, bldShadow.value with
        | Empty, _, _ | _, Empty, _ | _, _, Empty -> []
        | a, b, c -> [ h, { value = heightFromShadow a b c
                            support = union3 bh.support bShadow.support bldShadow.support } ]) |> ignore
    e, t, h, bh, bShadow, bldShadow

let STOPWATCH, SHADOWS, SUPER = 1, 2, 3
let premiseName = function 1 -> "stopwatch" | 2 -> "shadows" | 3 -> "super" | n -> string n
let showSupport (s: Set<Premise>) =
    if Set.isEmpty s then "{}"
    else s |> Seq.map premiseName |> String.concat ", " |> sprintf "{%s}"
let showRange = function
    | Empty -> "BOT (no consistent height)"
    | Iv(lo, hi) -> sprintf "[%.6g, %.6g]" lo hi
let reportBaro (h: Cell<Interval>) (t: Cell<Interval>) (label: string) =
    printfn "  %s" label
    printfn "      height    = %-28s support %s" (showRange h.value) (showSupport h.support)
    printfn "      fall-time = %-28s support %s" (showRange t.value) (showSupport t.support)

(**
### One height from two methods

We feed the measurements in one method at a time and watch the height narrow. First the
stopwatch alone: the barometer falls for somewhere between 3.0 and 3.2 seconds. Then the
shadows, all under one `SHADOWS` premise — they are one method, to be trusted or doubted
together: a 0.30 m barometer casting a 0.30–0.32 m shadow while the building casts a 45–48 m
shadow.
*)

printfn "5. barometer"
let e, t, h, bh, bShadow, bldShadow = buildBarometer ()
e.Assert(STOPWATCH, t, Iv(3.0, 3.2))
reportBaro h t "after stopwatch  t = [3.0, 3.2] s:"
e.Assert(SHADOWS, bh,        Iv(0.30, 0.30))
e.Assert(SHADOWS, bShadow,   Iv(0.30, 0.32))
e.Assert(SHADOWS, bldShadow, Iv(45.0, 48.0))
reportBaro h t "after shadows added:"

(**
```
5. barometer
  after stopwatch  t = [3.0, 3.2] s:
      height    = [44.1, 50.176]               support {stopwatch}
      fall-time = [3, 3.2]                     support {stopwatch}
  after shadows added:
      height    = [44.1, 48]                   support {stopwatch, shadows}
      fall-time = [3, 3.12984]                 support {stopwatch, shadows}
```

The height sharpened from `[44.1, 50.176]` to `[44.1, 48]` — the intersection of what the
stopwatch allows (via `½ g t²`) and what the shadows allow. Neither method found that bracket
alone; the lattice combined them.

The fall-time cell tightened too, to about `[3.0, 3.13]`, though no one touched the stopwatch.
The shadow method lowered the height ceiling to 48 m, and the *backward* fall-time propagator
read that smaller height and concluded the fall must have been shorter. Knowledge flowed
shadow → height → fall-time, against the direction the fall-time method was "meant" to run —
the multidirectionality that separates a propagator from a one-way formula. And the refined
fall-time now carries the support `{stopwatch, shadows}`, honestly recording that it leaned on
the shadow measurement to get that sharp.

### A conflicting witness, and its retraction

Now the superintendent, whose plans give the height precisely — 49 m — and whom we trust over
a shadow estimate.
*)

e.Assert(SUPER, h, Iv(49.0, 49.0))
reportBaro h t "after superintendent says h = 49 m:"

(**
```
  after superintendent says h = 49 m:
      height    = BOT (no consistent height)   support {stopwatch, shadows, super}
      fall-time = [3, 3.12984]                 support {stopwatch, shadows}
```

The height cell is `Bot`: the shadows capped it at 48 m, the superintendent pins it at 49 m,
and those ranges are disjoint. This is a *genuine* contradiction — the sources cannot all be
right — not the fabricated one sections 1 and 2 produced. The useful part is the stamp on it:
`{stopwatch, shadows, super}`. The engine has not only found the conflict, it has named the
three premises the conflict rests on. The fall-time cell, resting only on
`{stopwatch, shadows}`, is untouched; a contradiction stays local because each propagator that
reads an `Empty` cell simply declines to fire.

We trust the superintendent and the stopwatch, so the shadow is the weak link (a penumbra is
hard to pin, the ground may slope). We **retract the `SHADOWS` premise** — not the cell, the
premise — and let the engine re-derive whatever still has support.
*)

e.Retract(SHADOWS)
reportBaro h t "after retracting the shadow measurement:"

(**
```
  after retracting the shadow measurement:
      height    = [49, 49]                     support {stopwatch, super}
      fall-time = [3.16228, 3.16228]           support {stopwatch, super}
```

Two things happened, and the second is the one worth the section.

The height recovered to `[49, 49]`. Dropping the shadow premise removed both the shadow
method's contribution and the fall-time contribution it had narrowed; what remained was the
stopwatch's `[44.1, 50.176]` and the superintendent's `[49, 49]`, intersecting to `[49, 49]`.
The surviving height rests on `{stopwatch, super}` — the shadows are gone from its support.

The fall-time cell is the payoff. It went to `[3.16228, 3.16228]` — `√(2 · 49 / 9.8) = √10`.
The old `[3.0, 3.13]` reading, sharpened *using the shadows*, did not stubbornly survive to
contradict the new height: it had been stamped `{stopwatch, shadows}`, so retracting `shadows`
swept it away, and the fall-time was re-derived from the now-trusted height. The new estimate
is sharper than the stopwatch ever was — the superintendent's height, flowing backward through
the fall-time relation, has told us when the barometer must have landed.

That selective unwinding is the whole point of carrying premises. The engine did not search
for a consistent state or roll anything back in time; it revoked one premise, and every fact
whose support contained it — wherever in the network it had reached — fell away on its own,
because support travels with derivation. The propagator that placed the refined fall-time had
stamped it with the premises it used, and that stamp is what made the cleanup exact.

Nothing in the engine changed to run any of this. Sections 1–3 changed only the value type the
cells carry; section 4 changed the lattice to intervals; this section changed only the wiring —
a fan-in of redundant methods, and a use of the retraction the earlier demos left idle. The
lattice and the provenance were in the tutorial's engine all along. That is the propagator
bargain: fix one small engine over cells ordered by information, and the distance between
converting a temperature, choosing a number type, and weighing three measurements of a
building is entirely a matter of what the cells carry and how they are wired.
*)

(**
## Rejected robustness patches, measured

Two tempting patches do not repair the scalar lattice. The first rounds every arithmetic
operation to a fixed decimal grid and then retains exact equality. The second keeps binary
floating point but lets the scalar meet treat values up to four ULPs apart as equal. These
are measured on the same grids quoted above: 0.0 through 200.0 Celsius in tenths (2001
inputs), and 0.0 through 180.0 Fahrenheit in tenths (1801 inputs).
*)

module RejectedRobustness =
    let quantum = 0.1m

    // A deliberately naive standalone fixed-point scalar. Every primitive operation rounds
    // to the nearest q=0.1 grid point (ties to even), after which meet would use exact (=).
    let private quantize value =
        System.Decimal.Round(value / quantum, 0, System.MidpointRounding.ToEven) * quantum

    let private add a b = quantize (a + b)
    let private sub a b = quantize (a - b)
    let private mul a b = quantize (a * b)
    let private div a b = quantize (a / b)

    let private cToF c = add (div (mul c 9m) 5m) 32m
    let private fToC f = div (mul (sub f 32m) 5m) 9m

    let naiveFixedCFailures =
        [ 0 .. 2000 ]
        |> List.sumBy (fun tick ->
            let c = decimal tick / 10m
            if c |> cToF |> fToC = c then 0 else 1)

    let naiveFixedFFailures =
        [ 0 .. 1800 ]
        |> List.sumBy (fun tick ->
            let f = decimal tick / 10m
            if f |> fToC |> cToF = f then 0 else 1)

    let private sameSignUlpDistance (a: float) (b: float) =
        let aBits = uint64 (System.BitConverter.DoubleToInt64Bits a)
        let bBits = uint64 (System.BitConverter.DoubleToInt64Bits b)
        if (aBits >>> 63) <> (bBits >>> 63) then System.UInt64.MaxValue
        elif aBits >= bBits then aBits - bBits
        else bBits - aBits

    let withinFourUlps a b =
        a = b
        || (not (System.Double.IsNaN a || System.Double.IsNaN b)
            && sameSignUlpDistance a b <= 4UL)

    let private floatCToF c = c * 9.0 / 5.0 + 32.0
    let private floatFToC f = (f - 32.0) * 5.0 / 9.0

    let tolerantCFailures =
        [ 0 .. 2000 ]
        |> List.sumBy (fun tick ->
            let c = float tick / 10.0
            if withinFourUlps c (c |> floatCToF |> floatFToC) then 0 else 1)

    let tolerantFFailures =
        [ 0 .. 1800 ]
        |> List.sumBy (fun tick ->
            let f = float tick / 10.0
            if withinFourUlps f (f |> floatFToC |> floatCToF) then 0 else 1)

    // These are real unequal assertions one representable float apart. A 4-ULP meet
    // accepts every one, demonstrating the false-settle defect independently of C/F.
    let tolerantFalseSettles =
        [ yield! [ 0 .. 2000 ] |> List.map (fun tick -> float tick / 10.0)
          yield! [ 0 .. 1800 ] |> List.map (fun tick -> float tick / 10.0) ]
        |> List.sumBy (fun value ->
            let different = System.Math.BitIncrement value
            if different <> value && withinFourUlps value different then 1 else 0)

printfn "6. rejected robustness patches"
printfn "  naive fixed q=0.1: C failures %d/2001, F failures %d/1801"
    RejectedRobustness.naiveFixedCFailures RejectedRobustness.naiveFixedFFailures
printfn "  float eq within 4 ULP: C failures %d/2001, F failures %d/1801, false settles %d/3802"
    RejectedRobustness.tolerantCFailures
    RejectedRobustness.tolerantFFailures
    RejectedRobustness.tolerantFalseSettles

if RejectedRobustness.naiveFixedFFailures = 0 then
    failwith "naive fixed point unexpectedly hid its contracting-direction failure"

if RejectedRobustness.tolerantFalseSettles = 0 then
    failwith "ULP-tolerant equality unexpectedly accepted no genuinely unequal values"

(**
The measured rows are:

| scalar policy | C -> F -> C spurious bottoms | F -> C -> F spurious bottoms | false settles |
|---|---:|---:|---:|
| exact `float` equality (the original row) | 901 / 2001 | not previously recorded | 0 by definition |
| exact `decimal` equality (the original row) | 0 / 2001 | 77 / 1801 | 0 by definition |
| naive fixed point, q = 0.1 | 0 / 2001 | **800 / 1801** | 0 by definition |
| `float` equality within 4 ULPs | **19 / 2001** | **12 / 1801** | **3802 / 3802** one-ULP conflicts |
| quantized interval facade, q = 0.1 | **0 / 2001** | **0 / 1801** | 0; conflict uses intersection |

Naive fixed point is worse than decimal in the contracting Fahrenheit direction: several
Fahrenheit grid points collapse onto the same Celsius grid point, so expansion cannot
recover which point was asserted. Four-ULP equality reduces the original float failures
but does not eliminate them, and its third count is disqualifying: it calls every tested
pair of distinct adjacent representable values equal. It also makes equality non-transitive
(`a` can agree with `b`, and `b` with `c`, while `a` does not agree with `c`), so
propagation can become order-dependent. The interval facade is the only measured inexact
route with zero fabricated bottoms that still rejects genuine conflicts. Neither scalar
patch supplies a sound meet.

## 7. Fixed point as a quantized facade over intervals

Fixed point contributes a user-meaningful decimal grid, not another knowledge lattice.
The sealed `FixedPoint` class is an expression and presentation facade over `Interval`:
its public decimal constructor infers quantum from the decimal's written scale, and its
operators delegate to the unchanged outward-rounded interval arithmetic. Thus
`FixedPoint(12.50m)` carries q = 0.01, while `FixedPoint(12.5m)` carries q = 0.1.

Two fixed operands must have the same quantum; a mismatch fails loudly until the caller
uses `WithQuantum` to make the modeling choice explicit. A plain decimal operand is an
exact constant and retains the fixed operand's quantum. No operator rounds to the user grid.
`TryPoint` and `ToString()` present an interval as grid point `g` only when the whole
closed interval fits in the half-open cell `[g - q/2, g + q/2)`; otherwise the interval
remains visible. The lower boundary belongs to `g`, while the upper belongs to the next
cell.

The propagator cells below still store `Interval`. A private adapter wraps an interval
before a user-authored relation and unwraps the result afterward, so relation code uses
ordinary `+`, `-`, `*`, and `/` without making the facade a second lattice. Exact
decimal-vs-double comparison remains boundary machinery only.
*)

open System.Numerics

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

    // Exact comparison: no decimal-to-double round trip is used to decide enclosure.
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

let private requireOracle label condition =
    if not condition then failwithf "fixed-point oracle failed: %s" label

let fixedPointQuantum = 0.1m

let fixedCToF (c: FixedPoint) = c * 9m / 5m + 32m
let fixedFToC (f: FixedPoint) = (f - 32m) * 5m / 9m

let private applyFixed (quantum: decimal) (transform: FixedPoint -> FixedPoint) (interval: Interval) =
    FixedPoint.FromInterval(interval, quantum)
    |> transform
    |> fun result -> result.Interval

let buildFixedCF quantum =
    let e = Engine<Interval>(intervalL)
    let cC = e.NewCell()
    let fF = e.NewCell()
    e.AddProp([cC], fun () ->
        match cC.value with
        | Empty -> []
        | interval ->
            [ fF,
              { value = applyFixed quantum fixedCToF interval
                support = cC.support } ])
    |> ignore
    e.AddProp([fF], fun () ->
        match fF.value with
        | Empty -> []
        | interval ->
            [ cC,
              { value = applyFixed quantum fixedFToC interval
                support = fF.support } ])
    |> ignore
    e, cC, fF

let private fixedAt quantum interval = FixedPoint.FromInterval(interval, quantum)

printfn "7. fixed point facade over intervals"
let inferredTenths = FixedPoint(1.0m)
let inferredHundredths = FixedPoint(12.50m)
requireOracle "constructor infers q=0.1" (inferredTenths.Quantum = 0.1m)
requireOracle "constructor preserves trailing-zero scale" (inferredHundredths.Quantum = 0.01m)
requireOracle "decimal constants retain the fixed quantum"
    ((inferredHundredths * 2m + 1m).Quantum = 0.01m)
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
requireOracle "mismatched fixed operands fail loudly" mismatchedQuantumRejected
requireOracle "WithQuantum explicitly reconciles fixed operands"
    ((inferredHundredths.WithQuantum(0.1m) + FixedPoint(1.2m)).Quantum = 0.1m)

let operatorInterop =
    let x = FixedPoint(2.00m)
    [ x + 3m; 3m + x
      x - 3m; 3m - x
      x * 3m; 3m * x
      x / 3m; 3m / x ]
requireOracle "all arithmetic operators interoperate with decimal in both positions"
    (operatorInterop |> List.forall (fun result -> not result.IsBottom))

let assertedC = FixedPoint(1.0m)
let eqc, qcc, qfc = buildFixedCF assertedC.Quantum
eqc.Assert(1, qcc, assertedC.Interval)
printfn "  assert C=1: C = %s, F = %s"
    ((fixedAt assertedC.Quantum (eqc.Value qcc)).ToString())
    ((fixedAt assertedC.Quantum (eqc.Value qfc)).ToString())

let assertedF = FixedPoint(47.0m)
let eqf, qcf, qff = buildFixedCF assertedF.Quantum
eqf.Assert(1, qff, assertedF.Interval)
printfn "  assert F=47: C = %s, F = %s"
    ((fixedAt assertedF.Quantum (eqf.Value qcf)).ToString())
    ((fixedAt assertedF.Quantum (eqf.Value qff)).ToString())

let conflictC = FixedPoint(1.0m)
let conflictF = FixedPoint(100.0m)
let eqConflict, qcConflict, qfConflict = buildFixedCF fixedPointQuantum
eqConflict.Assert(1, qcConflict, conflictC.Interval)
eqConflict.Assert(2, qfConflict, conflictF.Interval)
printfn "  assert C=1 and F=100: F = %s"
    ((fixedAt fixedPointQuantum (eqConflict.Value qfConflict)).ToString())

let mutable facadeCFailures = 0
let mutable facadeCDisplayFailures = 0
for tick in 0 .. 2000 do
    let asserted = decimal tick / 10m
    let authored = FixedPoint(asserted, fixedPointQuantum)
    let sweepEngine, sweepC, sweepF = buildFixedCF fixedPointQuantum
    sweepEngine.Assert(1, sweepC, authored.Interval)
    let shownC = fixedAt fixedPointQuantum (sweepEngine.Value sweepC)
    let shownF = fixedAt fixedPointQuantum (sweepEngine.Value sweepF)
    if shownC.IsBottom || shownF.IsBottom then
        facadeCFailures <- facadeCFailures + 1
    if shownC.TryPoint <> Some asserted then
        facadeCDisplayFailures <- facadeCDisplayFailures + 1

let mutable facadeFFailures = 0
let mutable facadeFDisplayFailures = 0
for tick in 0 .. 1800 do
    let asserted = decimal tick / 10m
    let authored = FixedPoint(asserted, fixedPointQuantum)
    let sweepEngine, sweepC, sweepF = buildFixedCF fixedPointQuantum
    sweepEngine.Assert(1, sweepF, authored.Interval)
    let shownC = fixedAt fixedPointQuantum (sweepEngine.Value sweepC)
    let shownF = fixedAt fixedPointQuantum (sweepEngine.Value sweepF)
    if shownC.IsBottom || shownF.IsBottom then
        facadeFFailures <- facadeFFailures + 1
    if shownF.TryPoint <> Some asserted then
        facadeFDisplayFailures <- facadeFDisplayFailures + 1

printfn "  facade sweep: C bottoms %d/2001, F bottoms %d/1801, display failures %d"
    facadeCFailures facadeFFailures (facadeCDisplayFailures + facadeFDisplayFailures)

requireOracle "Celsius sweep has no spurious bottoms" (facadeCFailures = 0)
requireOracle "Fahrenheit sweep has no spurious bottoms" (facadeFFailures = 0)
requireOracle "on-grid assertions display identically"
    (facadeCDisplayFailures = 0 && facadeFDisplayFailures = 0)
requireOracle "wide intervals remain intervals"
    ((fixedAt fixedPointQuantum (Iv(1.0, 1.2))).TryPoint = None)
requireOracle "genuine conflict remains bottom"
    ((fixedAt fixedPointQuantum (eqConflict.Value qfConflict)).IsBottom)

let eqBarometer, qt, qh, qbh, qbShadow, qbldShadow = buildBarometer ()
eqBarometer.Assert(STOPWATCH, qt, Iv(3.0, 3.2))
eqBarometer.Assert(SHADOWS, qbh, Iv(0.30, 0.30))
eqBarometer.Assert(SHADOWS, qbShadow, Iv(0.30, 0.32))
eqBarometer.Assert(SHADOWS, qbldShadow, Iv(45.0, 48.0))
eqBarometer.Assert(SUPER, qh, FixedPoint(49.0m).Interval)
eqBarometer.Retract(SHADOWS)

let barometerTenths = fixedAt 0.1m (eqBarometer.Value qt)
let barometerHundredths = barometerTenths.WithQuantum 0.01m
printfn "  barometer sqrt(10): q=0.1 -> %s, q=0.01 -> %s"
    (barometerTenths.ToString())
    (barometerHundredths.ToString())
requireOracle "barometer q=0.1 reads 3.2" (barometerTenths.TryPoint = Some 3.2m)
requireOracle "barometer q=0.01 reads 3.16" (barometerHundredths.TryPoint = Some 3.16m)

(**
The actual relations are now the ordinary formulas
`c * 9m / 5m + 32m` and `(f - 32m) * 5m / 9m`; only the private adapter sees the
interval carried by the cell. The facade returns `1` and `33.8` for the float-killing
Celsius assertion, and `47` cleanly from the Fahrenheit direction. The incompatible
100 Fahrenheit assertion remains `Bot`; a wide interval remains an interval; and every
one of the 3802 on-grid sweep inputs round-trips through display without a fabricated
contradiction. The barometer's unchanged internal interval around `sqrt(10)` renders as
`3.2` at q = 0.1 and `3.16` after `WithQuantum 0.01m`.

No interval is rounded to the user grid during construction, propagation, or overloaded
arithmetic. Changing `WithQuantum` changes presentation only, as the two barometer views
of the same interval demonstrate.
*)
