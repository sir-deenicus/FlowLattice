# Two roads to a constraint cell

*Post-mortem of two failed prototypes in `IncrementalControl`: how `constraintprop.fsx`
and `constraintprop2.fsx` arrive at two different answers to one question, how to tell
which failure belongs to which paradigm — and why, despite surface differences, both
prototypes failed to produce a single engine that can run both the C↔F demo and
narrowing-style problems like Sudoku.*

---

## The question both files are answering

> *How do you build a **bidirectional** constraint cell on top of a **unidirectional**
> incremental-computation library?*

The library is [FSharp.Data.Adaptive](https://github.com/fsprojects/FSharp.Data.Adaptive).
Both files open with the same framing comment
([`constraintprop.fsx:38`](../constraintprop.fsx), [`constraintprop2.fsx:38`](../constraintprop2.fsx)),
and the crux is this:

> F# Adaptive … operates on a Directed Acyclic Graph (DAG) of dependencies … but it
> cannot handle circular dependencies due to the uni-directional dependency tracking
> inherent to DAGs. **However we can build such a datastructure atop F# Adaptive.**

F# Adaptive gives you `cval` (changeable input cells) and `aval` (derived values) and
recomputes derived values efficiently — but strictly *downhill*, along a DAG. A
constraint like "Fahrenheit and Celsius track each other" is a **cycle** (F depends on C
*and* C depends on F), which the DAG can't express.

The shared workaround in every engine is the same sleight of hand: **don't** make the
cycle a derived `aval`. Instead attach an imperative *callback* to each dependency, and
when it fires, write the new value back into a `cval` inside a `transact`. The callback
is the escape hatch that lets information flow "uphill" against the DAG. Everything else
— the differences between the cell engines — is a different opinion about *what that
callback should do*.

---

## Shared vocabulary

**`AdaptiveExpression<'a>`** ([`constraintprop.fsx:68`](../constraintprop.fsx),
[`constraintprop2.fsx:68`](../constraintprop2.fsx)) is identical in both files: a wrapper
around `aval<'a>` that overloads `+ - * /` so you can write reactive arithmetic that
reads like a spreadsheet formula. This is pure DAG-direction dataflow — the *substrate*,
not the constraint system.

**`minimize` (the "meet").** Every engine has a function that takes two candidate values
and either reconciles them into one or declares a conflict. The recurring example is set
intersection ([`constraintprop2.fsx:715`](../constraintprop2.fsx),
[`constraintprop2.fsx:849`](../constraintprop2.fsx)):

```fsharp
let simpleIntersection left right =
    let intersection = Set.intersect left right
    if Set.isEmpty intersection then Error "Domain Collapse..." else Ok intersection
```

In the Sussman & Radul sense (*The Art of the Propagator*) this is a `merge`: a
commutative, associative, idempotent **lattice meet**, where the empty set plays the role
of *the-contradiction*. This concept is the genetic material common to both approaches.
The difference is the *machinery* built around it.

---

## The two engines

There are three cell types across the two files, but they fall into **two paradigms**:

| Paradigm (by architecture) | Engine(s) | Designed for | Wrong shape for |
|---|---|---|---|
| **Propagators** | `Cell` in `constraintprop.fsx`; `Cell2` in `constraintprop2.fsx` | C↔F, linear systems | — |
| **Constraint propagation** | `Cell` in `constraintprop2.fsx` | Sudoku | C↔F |

That table groups by *architecture* — but architecture is not destiny. Both
prototypes ultimately fail the project's real goal. The fold engine fails conceptually
(narrowing cannot convert), the callback engine fails in practice (`CellError` crashes the
Adaptive graph), and even the original `string`-error callback engine is only a partial
success: it runs the C↔F demo, but it cannot do Sudoku-style narrowing. Of the three
engines, **none are viable.**

| Engine | Error channel | C↔F at runtime |
|---|---|---|
| `Cell` — `constraintprop.fsx` | `string` | ⚠️ converges, but **prototype only** — cannot narrow |
| `Cell2` — `constraintprop2.fsx:418` | `CellError<'T>` | 💥 `NullReferenceException` — *right* paradigm, broken by error-type × `shallowEquals` |
| `Cell` — `constraintprop2.fsx:255` | `CellError<'T>` | ❌ conflicts (wrong paradigm), then 💥 same NRE on the error path |

Both failures are dissected below — the conceptual one in
[The decisive distinction](#the-decisive-distinction-which-engine-can-do-what-and-why), the
crash in [The other failure](#the-other-failure-cellerror-vs-the-adaptive-graph).

### Approach A — the propagator engine (callback per edge)

`Cell` in [`constraintprop.fsx:194`](../constraintprop.fsx), later hardened into `Cell2`
at [`constraintprop2.fsx:418`](../constraintprop2.fsx).

**Mental model:** a cell is connected to other cells by **independent, directional
agents**. Each agent watches one input and writes one cell. The state is a `ResizeArray`
of dependency avals, and `AddDependency` installs **one callback per dependency**
([`constraintprop.fsx:278`](../constraintprop.fsx)):

```fsharp
member this.AddDependency (adaptivevalue: aval<'T option>) =
    dependentCells.Add adaptivevalue
    let disposable = adaptivevalue.AddCallback(fun _ ->
        if checkCircuitBreaker() then
            let candidates = [ for dcell in dependentCells do
                                 match AVal.force dcell with Some v -> yield v | None -> () ]
            match candidates with
            | h::t ->
                match runMinimize h t with
                | Result.Ok v    -> transact (fun () -> cell.Value <- Ok v)
                | Result.Error e -> transact (fun () -> cell.Value <- Error e)
            | [] -> ...
        else ... )
```

This is the Sussman–Radul recipe directly: you get a **bidirectional** relationship by
installing a **separate directional propagator for each direction**. The Fahrenheit↔Celsius
demo ([`constraintprop.fsx:316`](../constraintprop.fsx)) wires two of them:

```fsharp
cellFahrenheit.AddDependency (cellCelsius.Cell, fun c -> (c * 9./5.) + 32.)   // c → f
cellCelsius.AddDependency (cellFahrenheit.Cell, fun f -> (f - 32.) * 5./9.)   // f → c
```

Two features betray that this is a *cycle being forced to converge*:

- **A circuit breaker** ([`constraintprop.fsx:221`](../constraintprop.fsx)) trips when
  updates exceed a threshold within a one-second window — because a bidirectional
  constraint can oscillate forever.
- **`coarsen` / `isEqual`** ([`constraintprop.fsx:200`](../constraintprop.fsx)) round
  values for comparison, so a floating-point cycle has a fixpoint it can actually reach.

`Cell2` is recognizably the *same* engine — same `ResizeArray`, same per-dependency
callback, same `runMinimize` — grown up with structured `CellError<'T>` errors
([`constraintprop2.fsx:188`](../constraintprop2.fsx)), conflict enrichment
([`constraintprop2.fsx:467`](../constraintprop2.fsx)), and an assignment-checking path
that validates a *proposed* value against the dependencies-as-constraints before
committing ([`constraintprop2.fsx:484`](../constraintprop2.fsx),
[`constraintprop2.fsx:531`](../constraintprop2.fsx)).

There is a sting in the tail, though: *that very upgrade breaks it at runtime.* The same
architecture that converges on C↔F in `constraintprop.fsx` throws a `NullReferenceException`
the moment you drive the C↔F cycle through `Cell2` — not because the propagation is wrong,
but because the new `CellError<'T>` error type is incompatible with how F# Adaptive checks
whether a value changed. That gets its own section
([The other failure](#the-other-failure-cellerror-vs-the-adaptive-graph)); for now, hold the
thought: `Cell2` is C↔F-capable *by design*, but not *in practice*.

### Approach B — the constraint-propagation engine (global fold)

The *other* `Cell`, at [`constraintprop2.fsx:255`](../constraintprop2.fsx). This is not an
evolution of Approach A — it is a rewrite from a different worldview.

**Mental model:** a cell's value is the **lattice-meet of every contribution flowing into
it**, expressed once as a declarative fold that F# Adaptive keeps current. The first move
is splitting *intent* from *result* ([`constraintprop2.fsx:264`](../constraintprop2.fsx)):

```fsharp
let baseValue = cval initial            // user's intent only
let cell      = cval (Ok initial)       // computed result only
```

The defining move: inputs are an **adaptive list**, and the value is a **fold seeded with
`initial`** ([`constraintprop2.fsx:333`](../constraintprop2.fsx)):

```fsharp
let allInputs = clist [ (baseValue :> aval<_>) |> AVal.map Ok ]   // own intent is input #0

let reduceValues acc next =
    AVal.map2 (fun accRes nextRes ->
        match accRes, nextRes with
        | Error e, _ | _, Error e -> Error e          // first error wins
        | Ok l, Ok r -> minimizeCE l r) acc next      // otherwise, meet

let finalResult =
    allInputs
    |> AList.fold reduceValues (AVal.constant (Ok initial))        // seeded with `initial`
    |> AVal.bind id
```

There is no per-dependency callback doing reconciliation. `AddDependency` just appends a
contributor ([`constraintprop2.fsx:406`](../constraintprop2.fsx)); the single callback in
the whole engine ([`constraintprop2.fsx:350`](../constraintprop2.fsx)) just mirrors
`finalResult` into the public `cell`. The value = **meet of all constraints**, recomputed
globally — classical arc-consistency / AC-3 domain narrowing. This is the engine the
**Sudoku** solvers ride on ([`constraintprop2.fsx:686`](../constraintprop2.fsx) onward):
a peer's solved state is transformed into a contribution ("everything except what my peer
took") and merged in ([`constraintprop2.fsx:765`](../constraintprop2.fsx),
[`constraintprop2.fsx:949`](../constraintprop2.fsx), full 4×4 grid at
[`constraintprop2.fsx:1051`](../constraintprop2.fsx)).

---

## The decisive distinction: which engine can do what, and why

The cleanest way to tell the two paradigms apart is also the reason **the Sudoku engine
cannot do C↔F**.

### Trace: setting Celsius to 100 on the fold engine

The temperature cells at [`constraintprop2.fsx:634`](../constraintprop2.fsx) are built on
the **fold engine** — and built with `logErrors = true`, a fossil of this exact failure.
The default `minimize` is **equality** ([`constraintprop2.fsx:295`](../constraintprop2.fsx)):

```fsharp
defaultArg minimize (fun a b -> if isEqual a b then Ok a else Error "Default minimize: values are not equal")
```

Now set `cellCelsius.Value <- Some 100` (its seed was `initial = 0`):

- `baseValue` becomes `100`.
- `finalResult` folds:  `(Ok 0)  ⊕  (Ok 100, baseValue)  ⊕  (Ok 0, back-converted from F)`.
- The first step is `minimizeCE 0 100` → equality fails → **`Error`**.

`cellCelsius` flips to an error the instant it is set to anything other than its seed. The
conversion never happens. The fold engine can only ever **narrow**; it cannot transform.

**Receipts (from the REPL).** Running the temperature demo confirms this exactly. The
cleanest evidence is the *self-conflict* — it needs no second cell at all:

```text
> cellFahrenheit.SetValue 98;;
> cellFahrenheit.Cell;;
val it = cval(Error (InconsistencyConflict ("", "Default minimize: values are not equal")))
```

That error is `Ok 32 (seed) ⊓ Ok 98 (base)` under equality-minimize: a fold-`Cell` conflicts
against *its own `initial`* the moment its value leaves the seed. And no conversion fired —
the peer cell stayed at its seed rather than tracking:

```text
> cellCelsius.Value;;
val it: float option = Some 0.0          // not 36.7
```

There is also a sharper, uglier symptom. Setting the value the *other* way doesn't even
return a clean `Error` — it throws:

```text
> cellCelsius.Value <- Some 100;;
System.NullReferenceException
   at shallowEquals(CellError`1, CellError`1)
   at shallowEquals(FSharpResult`2, FSharpResult`2)
   at FSharp.Data.Adaptive.AValModule.Map2Val`3.Compute(...)
```

Hold that thought — the `NullReferenceException` is *not* the narrowing limitation. It is a
second, independent failure that also sinks `Cell2`, and it gets its own section next.

**Why Sudoku is fine on the same engine:** there `minimize` is set intersection and
`initial = universe = {1,2,3,4}` ([`constraintprop2.fsx:932`](../constraintprop2.fsx)). The
universe is the **top of the lattice**, so it is the identity for meet — folding it in is
harmless. The engine only works when `minimize` is a true meet *and* `initial` is its top.
Equality-on-floats is not a meet and `0` is not a top, so C↔F collapses.

### The two discriminators

1. **Architecture.** Many small independent **directional** machines (one callback per
   edge) = **propagators**. A single **global fold** of the meet = **constraint
   propagation**.

2. **Does the cell fold its *own* value/seed into the meet?**
   - **Fold engine: yes** — `baseValue` and `initial` are inputs
     ([`constraintprop2.fsx:333`](../constraintprop2.fsx),
     [`constraintprop2.fsx:346`](../constraintprop2.fsx)). Your input must be *consistent
     with* everything, including the back-conversion. Pure narrowing → **can't** do C↔F.
   - **Callback engine: no** — `dependentCells` holds only the *external* transforms; the
     cell's own value is not in the meet
     ([`constraintprop.fsx:278`](../constraintprop.fsx)). With one external dep per
     direction, `runMinimize [transform]` simply *is* the transform, so setting C
     overwrites F. Directional push → C↔F **works** (circuit breaker + `coarsen` damp the
     cycle).

So: **C↔F is closer to propagators** (paired directional agents, functional push), and
**Sudoku is constraint propagation** (global meet, monotone domain narrowing).

---

## The other failure: `CellError` vs. the adaptive graph

The paradigm story above explains why the *fold* engine can't convert. It does **not**
explain why `Cell2` — which has the *right* architecture — also fails the C↔F demo. That is
a separate bug on a different axis (type/library, not paradigm), and it is a trap worth
isolating for anyone building cells on F# Adaptive.

Drive the cycle through `Cell2` and you get, not a conflict, but a crash — from two
different call sites depending on how you poke it:

```text
> cellCelsius.Value <- Some 100;;
System.NullReferenceException
   at shallowEquals(CellError`1, CellError`1)
   at shallowEquals(FSharpResult`2, FSharpResult`2)
   at FSharp.Data.Adaptive.AValModule.MapVal`2.Compute(...)
   at ...Cell2`1.set_Value(...)   constraintprop2.fsx:545   // inside transact → runFinalizers → a callback

> cellFahrenheit.SetValue 98;;
System.NullReferenceException
   at shallowEquals(CellError`1, CellError`1)
   at ...Cell2`1.checkValue(...)  constraintprop2.fsx:491   // AVal.force currentConstraint
```

**What `shallowEquals` is.** After any `MapVal` / `Map2Val` recomputes, F# Adaptive calls
its internal `shallowEquals` to compare the new output against the cached previous one —
that is how it decides whether to wake dependents. It runs on essentially every node
evaluation, which is why the two traces enter from different doors (a `transact` finalizer
firing a callback; an `AVal.force` inside `checkValue`) and land in the same place. The
fold engine's version of this crash goes through `Map2Val` rather than `MapVal` (its meet is
built from `AVal.map2`), but it is the identical root cause.

**Why the DU and not the string.** The stack shows `shallowEquals` recursing *one level*:
to compare a `Result`, it compares the active case's payload via `shallowEquals` of that
payload's type. The two files differ in exactly that payload, and that is the whole story:

- `constraintprop.fsx`'s `Cell` carries `Result<'T, string>`. The payloads it must compare
  are `float` (value type) and `string` — both have safe comparer paths. **No crash.**
- Both `constraintprop2.fsx` engines carry `Result<'T, CellError<'T>>`. The Error payload is
  the `CellError` DU; `shallowEquals` descends into `shallowEquals<CellError>` and
  dereferences a **null DU operand it doesn't guard** → `NullReferenceException`.

The value actually being compared is `InconsistencyConflict("", "…")` — only `string` fields
— so this is *not* a "can't compare a `'T` / `'T list` field" problem; it is a null reference
being walked. The most likely null is the map node's previous-value cache, which for a
reference-typed output (`Result<_, CellError>`) starts life as `Unchecked.defaultof = null`.
The original engine never tripped it because `float` / `string` / `float option` all take the
comparer's safe path.

> *Which operand is null is inference from the stack; the channel-type split — `string`
> survives, the DU crashes — is certain. It can be pinned down by decompiling
> `ShallowEqualityComparer` in the FSharp.Data.Adaptive 1.2.16 package.*

**The irony.** This is the *upgrade* biting back. `constraintprop.fsx` converges on C↔F
precisely *because* its errors are dumb strings. Replacing them with a structured,
information-rich `CellError<'T>` DU — the change that made conflict reporting genuinely
better — is what makes both Gen-2 cells incompatible with the adaptive graph's equality
fast-path. The fold engine hits *both* walls (wrong paradigm **and** this NRE); `Cell2` has
only this one standing between it and a working C↔F.

**This is a library/type bug, not a propagation bug.** The fix space is "keep the error
channel shallow-comparable" — primitive/value-type error codes, or give `CellError` a custom
`IEquatable`/equality so F# Adaptive never structurally walks it — nothing in the propagation
logic. (Out of scope here: flagged, not fixed.)

---

## Side by side

| | **Propagators** (Approach A) | **Constraint propagation** (Approach B) |
|---|---|---|
| Where | `Cell` in `constraintprop.fsx`; `Cell2` in `constraintprop2.fsx:418` | `Cell` in `constraintprop2.fsx:255` |
| Dependency storage | mutable `ResizeArray` | adaptive `clist` |
| Who drives propagation | one hand-written callback **per dependency** | F# Adaptive, via one `AList.fold` |
| What a value *is* | recomputed from external transforms on each change | the standing **meet** of all contributions (incl. own intent + seed) |
| Set vs. computed | one shared `cell` | split: `baseValue` (intent) vs `cell` (result) |
| Folds own value into the meet? | **no** → can convert/overwrite | **yes** → narrowing only |
| Requires of `minimize` | any reconciliation | a true lattice meet with `initial` = top |
| Handles | C↔F, bidirectional conversions, linear systems | Sudoku / arc-consistency domain narrowing |
| Breaks on | — | C↔F (meet ≠ function application) |
| Runtime status on C↔F | `constraintprop.fsx` converges but cannot narrow; **`Cell2` `NRE`s** (CellError × `shallowEquals`) | also `NRE`s on the error path (same cause) |
| Flavour | independent directional agents (Sussman–Radul) | global domain narrowing (AC-3) |

A caveat worth stating: the line is fuzzy. Both share the `minimize`/meet core, both use
the callback-into-`transact` trick to close cycles, both keep a circuit breaker. The real
divide is *who runs the loop* (your per-edge callbacks vs. Adaptive's fold) and *how a
value relates to its sources* (directional recompute vs. accumulated meet of all
contributions including your own).

---

## Inferred genealogy

This is not a git repository, so the lineage below is reconstructed from **content** —
what is a superset of what, and what each engine can and cannot do — not from history.

```
        ┌─────────────────────────────────────────────┐
        │  Gen 0: shared substrate                      │
        │  AdaptiveExpression + "callback escapes the   │
        │  DAG" insight (both file headers)             │
        └───────────────────────┬───────────────────────┘
                                │
        ┌───────────────────────▼───────────────────────┐
        │  Gen 1: Cell  (constraintprop.fsx)             │
        │  PROPAGATOR engine: ResizeArray +              │
        │  per-edge callback + runMinimize + breaker.    │
        │  Handles C↔F, linear systems. Errors = string. │
        └─────────┬───────────────────────────┬──────────┘
                  │                            │
   (carried forward + hardened)      (ground-up rethink for narrowing)
                  │                            │
   ┌──────────────▼─────────────┐  ┌───────────▼──────────────────┐
   │ Gen 2a: Cell2              │  │ Gen 2b: Cell (the new one)    │
   │ (constraintprop2.fsx:418)  │  │ (constraintprop2.fsx:255)     │
   │ PROPAGATOR engine + CellError│ │ CONSTRAINT-PROP engine:       │
   │ + enriched conflicts +     │  │ clist + AList.fold meet +     │
   │ assignment checking.       │  │ baseValue/cell split.         │
   │ C↔F by design, NREs now.   │  │ Handles Sudoku; FAILS on C↔F. │
   └────────────────────────────┘  └───────────┬───────────────────┘
                                                │
                                    ┌───────────▼───────────────┐
                                    │ Sudoku solvers built on   │
                                    │ the constraint-prop Cell   │
                                    │ (2-cell → 3-cell → 4×4)    │
                                    └────────────────────────────┘
```

Reasoning behind the edges:

- **`Cell2` is a direct descendant of `constraintprop.fsx`'s `Cell`.** Identical skeleton
  (`ResizeArray`, per-edge `AddCallback`, collect-then-`runMinimize`, the same circuit
  breaker), plus additive features (structured errors, conflict enrichment, assignment
  checking). It is an evolution, not a rewrite — and architecturally still the C↔F engine.
  But one of those "additive" features (the `CellError<'T>` error channel) is regressive in
  practice: it makes `Cell2` throw on the very C↔F demo its ancestor converges on (see
  [The other failure](#the-other-failure-cellerror-vs-the-adaptive-graph)).
- **The new `Cell` in `constraintprop2.fsx` is a fork.** It shares almost no
  implementation with the others (no `ResizeArray`, no per-edge callback, no
  `runMinimize`), and introduces `clist`/`AList.fold` and the `baseValue`/`cell` split.
  You don't refactor one into the other; this is a restart from a different paradigm,
  built to do domain narrowing (Sudoku).
- **The two paradigms coexist in the second file.** `Cell` = constraint propagation (new),
  `Cell2` = the propagator engine preserved and upgraded — kept precisely *because* the new
  constraint-prop `Cell` cannot cover the bidirectional-conversion cases (the C↔F demo was
  ported onto it at [`constraintprop2.fsx:634`](../constraintprop2.fsx) and broke, hence
  `logErrors = true`).
- **`CellError` is a shared Gen-2 maturation — and a shared Gen-2 regression.** Both Gen-2
  engines depend on the `CellError<'T>` DU, which does not exist in `constraintprop.fsx` at
  all — dating the second file as conceptually later regardless of filenames. It is also the
  single change that makes *both* of them crash on C↔F (`shallowEquals` can't walk the DU),
  while the string-error original runs clean. The richer error model and the runtime
  breakage arrived in the same step.

**One wrinkle:** the filesystem mtimes have `constraintprop.fsx` (the simpler propagator
file) as *newer* than `constraintprop2.fsx`. The most likely reading is that the propagator
engine — being the more general one, the one that handles C↔F — was recently extracted back
into its own clean file, rather than that the design evolved *toward* fewer features and
weaker error handling. Content beats mtime here, but it is flagged so it can be corrected
if the real history ran the other way.

---

## What each is good for

- **Constraint propagation (fold `Cell`)** is the better *foundation for narrowing
  problems*: the value is a declarative function of its contributions, the merge is the
  only thing you customize, and adding a constraint is just "append a contributor." It
  scales cleanly to the 4×4 Sudoku. Its hard limit: it only models problems whose answer
  is a **meet**, with `initial` as the lattice top. It cannot express a functional
  conversion.
- **Propagators (callback `Cell` / `Cell2`)** handle *directional and bidirectional
  functional relationships* — conversions, linear systems — by pushing values around a
  network of independent agents, and (in `Cell2`) can validate an assignment against
  constraints before accepting it. The cost is the eager re-collect-on-change loop and a
  heavier reliance on the breaker and coarsening to converge. **Caveat:** only the
  `constraintprop.fsx` version converges on C↔F today, but that does
  not make it a success: it still cannot do narrowing. `Cell2` carries the right design but
  `NRE`s on C↔F until its `CellError` channel is made shallow-comparable.

**One-line arc:** the project started with a hand-rolled propagator network (directional
agents, C↔F), and later forked off a declarative constraint-propagation engine (global meet,
Sudoku) — keeping the propagator engine alongside it, because a meet can narrow but it
cannot convert. The *original* string-error `constraintprop.fsx` converges on C↔F, but it
is **still a failed prototype**: it has no credible narrowing path for Sudoku. The upgraded
`Cell2` propagator engine would be the natural replacement, but it quietly broke its own C↔F
demo with the `CellError` `shallowEquals` NRE. The constraint-prop fold engine cannot
convert. So both prototypes failed in different ways, and the current design journal describes
the rebuild rather than a patch.
