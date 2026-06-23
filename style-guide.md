# IncrementalControl — style & ergonomics guide

*— Claude (Opus 4.8), 2026-06-21*

Scope: the whole `IncrementalControl` repo. Two subsystems live here, and this
guide covers both:

- **AsyncRx-on-Hopac** — an async push-stream kernel ([AsyncRxHopac.fsx](AsyncRxHopac.fsx))
  with its examples and tests; Task/AsyncSeq interop is folded into the kernel.
- **Constraint-cell engines** — the propagator / constraint-propagation cells
  ([constraintprop.fsx](constraintprop.fsx), [constraintprop2.fsx](constraintprop2.fsx))
  and the design work for their successor.

**Part A is project-wide** — it holds in both subsystems and in anything new.
**Parts B and C** are the per-subsystem specifics. Conventions that were
previously stated only as one-off rationale across the design docs
([why-asyncrx.md](docs/why-asyncrx.md),
[AsyncRxHopac-fixup-plan.md](docs/AsyncRxHopac-fixup-plan.md),
[clauses-dsl-plan.md](docs/clauses-dsl-plan.md),
[compaction-plan.md](docs/compaction-plan.md),
[constraint-engine-design-journal.md](docs/constraint-engine-design-journal.md),
[constraint-cell-genealogy.md](docs/constraint-cell-genealogy.md)) are
consolidated here.

The through-line both subsystems share: **one honest model underneath; a small,
intention-revealing surface on top.** AsyncRx exposes one canonical operator
surface (no alias layer); the constraint work puts capability surfaces over one
lattice/meet semantic contract. Everything below is a corollary.

---

## Part A — Project-wide

### A1. Names carry semantics

Use the precise term even when a looser one is more common; a wrong-but-familiar
label is a bug in the documentation.

- The library is **`AsyncRx`, not `Rx`** — this is async *push* on Hopac, not
  classic pull Reactive Extensions ([fixup-plan §60](docs/AsyncRxHopac-fixup-plan.md)).
  (`AsyncRx` is also the name of the run/consume module — the front door.)
- **"Propagators" and "constraint propagation" are not synonyms** — one is a
  per-edge callback engine of independent directional agents; the other is a
  global fold over a meet. They have different powers (C↔F reassignment vs
  Sudoku narrowing). Calling the fold engine "propagators" is the exact mislabel
  the genealogy doc exists to correct
  ([constraint-cell-genealogy.md](docs/constraint-cell-genealogy.md)) — confirm a
  cell's paradigm by tracing execution, not by the "constraint"/"merge" words in
  its code.
- **Conversions are `ofX` / `toX`**, consistently: `ofSeq`, `ofTask`,
  `ofTaskFactory`, `ofAsyncSeq`; `toTaskList`, `toAsyncSeq`. A new adapter
  follows the same shape.
- **Combinators are organized by algebraic role** — interleave (`merge`), choice
  (`amb`/`orElse`/`firstValue`), products (`zip`/`bothOnce`), transforms — but on
  the *one* `AsyncObservable` surface, not scattered across micro-modules (the
  `Joinad` grab-bag *and* the later `Merge`/`Choice` splinters were each
  dissolved). Role guides naming and file order, not module count.
- **Builders are lowerCamel CE values**: `asyncRx { }`, `clauses { }`.

### A2. One model underneath; friendly / capability surfaces on top

Both subsystems independently landed on the same shape — a single honest model,
with the surface tuned for the reader rather than the implementer.

- **AsyncRx — one operator surface, no alias layer.** `AsyncObservable` owns each
  operator name *exactly once*; `EventChoice` is the public substrate for
  authoring new ones; `AsyncRx` is the consume front door. There is no
  friendly-alias facade (the old one was dissolved) — if a canonical name is
  cryptic, fix the name, don't alias it. (Module map in Part B.)
- **Constraint engine — one semantic contract + capability surfaces.** The
  design direction (journal D1–D2) is one lattice/meet/monotone-propagator model
  with *capability-specific* surfaces above it (a solver session vs a reactive
  session) that "need not expose identical operations." Same instinct: freeze the
  semantics, shape the surface to the use.

Corollary: the canonical names *are* the register for examples and application
code — called qualified (`AsyncObservable.map`, the `List.map` rule of A5). There
is no shorthand-alias layer to reach for instead.

### A3. Wrap the substrate; don't make its idioms your surface

Keep the host library's mechanics behind your own vocabulary.

- **AsyncRx wraps Hopac.** The project idiom is the noun-based `EventChoice`
  API; Hopac's symbolic operators (`<|>`, `^=>`, …) stay behind named helpers and
  are never the surface idiom (`EventChoice<'T>` is literally `Hopac.Alt<'T>` —
  the alias exists so examples can talk about *event choices* without leaning on
  symbols). `job { }`, `Hopac.run`, `Hopac.start`, `timeOutMillis` are fine —
  they read as words.

  ```fsharp
  // DO
  EventChoice.choose [ EventChoice.afterMillis 100 timeout; EventChoice.take ch id ]
  // DON'T — symbolic Hopac operators as the idiom
  ev1 <|> ev2
  ```

- **The constraint work wraps FSharp.Data.Adaptive** and scopes it deliberately:
  Adaptive is the *outer reactive shell* only, kept out of the solver kernel
  (journal D7) — its DAG-only nature, its `AddCallback`+`transact` cycle hack, and
  its per-node cost are not allowed to define the engine's surface or its hot
  path.

### A4. DSLs: put the vocabulary on the builder

Prefer `[<CustomOperation>]` clause heads on the builder over free helper
functions ([clauses-dsl-plan §5](docs/clauses-dsl-plan.md)). On-builder
vocabulary is cohesive and discoverable — IntelliSense inside the `{ }` surfaces
exactly the legal heads — and it reads as one DSL rather than "a builder plus
loose helpers." Strip ceremony: no mandatory `yield`, no `Some/None` chooser
noise for the common case.

```fsharp
// DO
clauses {
    whenValue a ((=) true) "a was true"
    always (asyncRx { ... })
}
// DON'T
clauses {
    yield case a (function true -> Some "a was true" | _ -> None)
    yield asyncRx { ... }
}
```

Justify DSL work as **readability- and discoverability-driven, not
line-count-driven** ([clauses-dsl-plan §82](docs/clauses-dsl-plan.md)). A builder
may *grow* in lines while the call sites get clearer; that is the win.

### A5. Examples are the user-idiom artifact

Examples model how a user should write against the surface, so they hold to
A1–A4 strictly. [AsyncRxHopac.Examples.fsx](AsyncRxHopac.Examples.fsx) is the
reference.

- **Call operators qualified by their role module — `AsyncObservable.map`, not a
  bare `map` from an `open` (the `List.map` rule).** The qualifier is a deliberate
  clarity annotation, like a type ascription under inference: it says which `map`,
  and which layer, every time. So `open` brings *module-name prefixes* and *types*
  into scope, never bare function names:
  ```fsharp
  open AsyncRxHopac              // module prefixes (AsyncObservable, AsyncRx) + asyncRx/clauses
  open AsyncRxHopac.Core         // the types: AsyncObservable, Subscription, …
  // then call qualified, the way you'd write List.map:
  AsyncObservable.intervalMillis 50
  |> AsyncObservable.map (fun i -> ...)
  |> AsyncRx.run onNext onError onCompleted
  ```
  Still no Hopac at the call site — `AsyncRx.run` consumes to the terminal,
  blocking, with plain handlers — so the A3 test holds (§A3): an example that must
  `open Hopac` to run a stream is leaking the substrate. Drop to
  `AsyncRx.subscribeJob` / `AsyncRx.runJob` (and `open Hopac`) only for
  `Job<unit>` handlers (sink backpressure) or a stoppable `Subscription` handle.
- **Consume shapes, in order of ceremony:** *run to completion* (the common case)
  — `source |> AsyncRx.run onNext onError onCompleted`, blocking until the
  terminal, no Hopac at the call site (the bridge demo uses this); *fire-and-collect*
  one handler — `AsyncRx.runBlocking source onNext`; *subscription-handle* (only
  when you need `Stop`/`Completion`) — `Hopac.run <| job { let! sub = AsyncRx.subscribeJob … in do! sub.Completion }`,
  the one shape that still surfaces Hopac, by design — you've opted into the
  lifecycle.
- **Each demo is a named `xDemo ()` function**, invoked at the bottom of the file
  under a labeled header so the script runs end-to-end.
- **Make the interesting property visible in the trace.** The bridge demo
  timestamps each line so backpressure (producer paced by consumer) is legible,
  not merely claimed.

A companion/interop example qualifies the bridge conversions the same way
(`AsyncObservable.toAsyncSeq` / `AsyncObservable.ofAsyncSeq`) and otherwise reads in
the same dialect — don't let a second file drift into a second idiom.

### A6. Tests are maintainer-facing

Tests assert behaviour, so the core/canonical surface (and the `Internal`
helpers, reachable in the same `#load` assembly) is fine here. Mirror
[AsyncRxHopac.Tests.fsx](AsyncRxHopac.Tests.fsx):

- **Record full notification sequences** — assert on `[ N x; …; C ]` /
  `[ … ; E "msg" ]`, so terminal grammar is checked, not just printed output.
- **`check name cond` / `checkEq name expected actual`**, a `failures` counter,
  and `exit failures` at the end (non-zero on failure for CI).
- **Bound every wait** (`record`'s `waitMs`, an `Alt.choose` against
  `timeOutMillis`) so a deadlock fails visibly instead of hanging.
- **For a model with a clear oracle, test backends differentially** — the
  constraint design plans a persistent `Set<int>` implementation run against the
  same semantic tests as the fast bitset, as a clarity oracle (journal step 3).
- Run with `dotnet fsi <File>.Tests.fsx`.

### A7. Keep the docs honest over time

The design docs are working artifacts, not write-once prose, and each subsystem
has a discipline for keeping them true:

- **Plans flip to as-built.** AsyncRx plans get a `Status: as-built` banner and a
  dated "As-built deltas" section recording where the build diverged from the
  design, rather than silently editing the original
  ([asyncrx-asyncseq-plan.md](docs/asyncrx-asyncseq-plan.md)).
- **Living vs append-only.** The constraint design journal keeps its *Design
  snapshot* and *Decisions ledger* living (edited in place) and its *Journal*
  append-only (new dated entries; never rewrite history). Don't silently reverse
  a decision — mark it `Revised`/`Superseded`, keep the original date, append the
  why.
- **Separate confidence from commitment.** Use explicit status —
  `Decided` / `Tentative` / `Revised` / `Superseded` — so a strong argument isn't
  mistaken for a settled decision.
- **Evidence beside performance claims.** Until measured, timings and allocation
  goals are acceptance targets or hypotheses, not facts.

### A8. Behavioural contracts are part of the UX

Users rely on the model's promises; new code must not break them.

- **AsyncRx** (source contract at [AsyncRxHopac.fsx:59](AsyncRxHopac.fsx)):
  *serialized callbacks* (`OnNext`/`OnError`/`OnCompleted` never invoked
  concurrently for one subscription — multi-source operators funnel through one
  channel + consumer loop); *terminal grammar* (zero or more `OnNext`, then *at
  most one* of `OnError`/`OnCompleted`, then nothing); *real backpressure*
  (`OnNext : 'T -> Job<unit>` is awaited, so a slow consumer paces the producer —
  a new source must await `OnNext` before advancing); *deterministic teardown*
  (`Subscription.Completion` fires after the terminal / Stop-unwind — await it
  rather than sleeping).
- **Constraint engine** (semantic laws, journal): information is ordered by
  *refinement* (`x ≤ y` means "`x` has at least as much information"; `Top` = the
  universe, meet = intersection, propagation descends to the greatest fixed point
  below the store); a cell is a lattice element, merge is the meet, a propagator
  is a monotone contribution; **contradiction is the empty domain** (`⊥` = `0`
  bitset), tested by compare — not a thrown exception or a rich error value (D5).
  Re-assignment (the C↔F demo) is non-monotone and needs an explicit retraction
  layer (trail / premises) — don't assume a meet-only engine can widen.

### A9. Implementation conventions that protect the surface

These are *maintainer* clarity, a different register from the user-facing A1–A6
(cf. [compaction-plan §22](docs/compaction-plan.md), which keeps the race loops
legible after factoring). Both matter; don't confuse one for the other.

- **Stack-safe loops:** iterate with Hopac `job { while … }`, never recursive
  `return! loop ()` (overflows for fully synchronous chains). Used by
  `For`/`While`/`ofSeq`/`ofAsyncSeq`.
- **One error on a throw:** route a thrown source through the same path as
  `OnError` (`Internal.forwardInto`), and keep the boundary
  `Internal.terminalGate` as the last line of defense.
- **Keep the error channel cooperating with the substrate** — two faces of one
  rule. Unwrap a single-inner `AggregateException` at any Task/Async boundary so
  the real message surfaces, not "One or more errors occurred." (kernel P2-8;
  `unwrapAggregate`, on the `AsyncObservable` surface). And keep a constraint error channel
  *shallow-comparable*: a rich `CellError<'T>` DU on a `Result` error arm makes
  FSharp.Data.Adaptive's `shallowEquals` throw an NRE — the bug that broke both
  Gen-2 cells; the successor sidesteps it with `⊥ = 0`, no DU
  ([constraint-cell-genealogy.md](docs/constraint-cell-genealogy.md)).
- **Weigh interop dependencies deliberately.** The kernel now carries the
  AsyncSeq dependency directly: the 2026-06-21 consolidation chose
  interop-on-the-surface over a dependency-free kernel (one fewer file/module beat
  dependency purity for a small, commonly-paired package). Still declare
  `#r "nuget:"` refs *before* any `#load` (a nested `#r "nuget:"` two levels down
  won't resolve), and document any per-element cost (e.g. the Async↔Job hop) so
  nobody mistakes the adapter for zero-cost. A genuinely heavyweight, rarely-used
  dependency is the case to reconsider isolating; the same "scope the dependency"
  instinct keeps Adaptive out of the solver kernel (A3).
- **Opens bring prefixes and types, not bare functions; avoid `[<AutoOpen>]`.**
  Functions are called qualified by their module (§A5, the `List.map` rule), so a
  name's provenance is visible at the call site. `open` earns its keep for two
  jobs: bringing *module-name prefixes* into scope (`open AsyncRxHopac` so you can
  write `AsyncObservable.map` / `AsyncRx.run`, the way `List` is in scope — this
  also brings the top-level `asyncRx`/`clauses` CE values) and bringing *types*
  into scope (`open AsyncRxHopac.Core` for `AsyncObservable`). Never `[<AutoOpen>]`
  — a name appearing unbidden is precisely
  what to avoid; the kernel's core types are exposed by an explicit `open Core`
  inside the file, not an attribute.
- **F# comment gotcha:** never put the tokens `#` or `*)` inside a `(* *)` block
  comment — the lexer still acts on them and breaks the build.

### A10. Writing register — a sharp beginner, not a blank slate

Explanatory prose — tutorials, articles, walkthroughs (the `tutorial-*.md`
series, e.g. [tutorial-propagation-part1.md](docs/tutorial-propagation-part1.md))
— is pitched at a **smart high-school senior or early-STEM undergrad**: fluent
with functions, variables, algebra, and set notation, and unafraid of a little
code, but *not* assumed to know the domain jargon (lattice, meet, monotone,
propagator, TMS, arc-consistency). Introduce each real term once, plainly, then
*use* it; don't keep re-explaining it.

- **Trust the fundamentals.** Don't explain what a variable or a function is, and
  don't reassure ("don't worry", "it's easy", "promise!") — to this reader that
  scans as condescension. Warmth comes from a sharp analogy and momentum, not from
  hand-holding. Aim a notch above the reader and let them rise to it.
- **Name the real thing, then move on.** One good analogy beats three. Give the
  precise term — and, for the curious, its standard name in a parenthetical they
  can go search — rather than talking around it. A comforting-but-wrong
  simplification is the A1 mislabel bug in a friendlier coat.
- **Keep the genre's voice.** The flipcode / Denthor / Red Blob tutorial register
  (second person, ASCII diagrams, a worked run, a teaser, a signoff) is welcome —
  the lift is in *pitch*, not personality. Part-numbered sections, a difficulty/
  time header, and a "try this" close are part of the fun, not clutter.
- **Break code into digestible chunks, and *explain* each.** Interleave short,
  single-idea code blocks with the prose that motivates them, rather than dropping
  one wall of source. Naming a block is not explaining it: walk through what it does
  and any non-obvious language idiom, so a reader fluent in programming but new to
  the language (or the domain) can follow it line by line. (For a long type/class,
  open it once and mark continuations clearly so the chunks still paste back into
  one runnable file.)
- **Precision still binds (A1, A8).** A light tone is not licence to fudge: keep
  the refinement order, the meet, `⊥`, and especially the
  *retraction-is-reason-bookkeeping-not-search* framing exactly right. And a code
  listing in an article must actually run and match any output it prints.

---

## Part B — AsyncRx-on-Hopac specifics

**Rule: a small module count, with one operator surface carrying the work — and
the canonical name, called *qualified*, is the only surface (`AsyncObservable.map`).**
The earlier design splintered operators across micro-modules
(`Merge`/`Choice`/`TaskInterop`) behind a friendly `AsyncRx` *alias-bag* facade;
both were dissolved in the 2026-06-21 consolidation
([module-consolidation-plan.md](docs/module-consolidation-plan.md)) — "too many
modules" is its own design smell ([fixup-plan §64](docs/AsyncRxHopac-fixup-plan.md)).
There is now **no facade and no alias layer**: if a canonical name is cryptic,
rename it rather than aliasing it. This is the concrete instance of A2 for the
push-stream subsystem.

Five modules (+ two top-level CE values), each owning a name exactly once:

| role | module | examples |
| --- | --- | --- |
| the library types | `Core` | `AsyncObservable<'T>`, `AsyncObserver`, `Subscription`, `SubscribeContext`, `EventChoice<'T>` |
| event-choice (Alt) + cancellation substrate — **public**, for authoring combinators | `EventChoice` | `choose`, `stop`, `orStop`, `take`, `afterMillis`; `Token`, `create`, `cancel`, `childContext` |
| private plumbing (one `#load` assembly, reachable from tests) | `Internal` | `forwardInto`, `giveOrStop`, `terminalGate`, `rootContext`, … |
| run / consume — the front door | `AsyncRx` | `subscribeJob`, `runJob` (`Job<unit>` handlers); `run`, `runBlocking`, `subscribe` (plain handlers) |
| the operator surface — sources, transforms, products, choice, interleave, Task/AsyncSeq interop, CE builder classes | `AsyncObservable` | `singleton`, `ofSeq`, `intervalMillis`, `map`, `mapJob`, `filter`, `choose`, `scan`, `take`, `bind`, `switchLatest`, `zip`, `bothOnce`, `first`, `firstWhere`, `merge`/`merge2`, `amb`/`orElse`/`firstValue`, `ofTask`/`ofTaskFactory`/`toTaskList`, `ofAsyncSeq`/`toAsyncSeq` |
| CE keywords (top level) | `asyncRx`, `clauses` | one `open AsyncRxHopac` brings these into scope |

Naming notes:
- `AsyncRx` (the run module) and `asyncRx` (the CE value) coexist by case, like
  `List`/`list`, `Seq`/`seq`.
- `EventChoice` is both a module (the combinator substrate) and a type
  (`EventChoice<'T> = Hopac.Alt<'T>`) — the companion pattern.
- **Inside `module AsyncObservable`, call sibling operators *unqualified***
  (`singleton`, `firstValue`): a self-qualified `AsyncObservable.foo` resolves
  `AsyncObservable` to the *type* (in scope via `open Core`) and fails to compile.
  Qualified calls are the rule *from outside* the module (examples/tests).

```fsharp
// DO (application / examples): canonical names, qualified — like List.map
open AsyncRxHopac              // module prefixes (AsyncObservable, AsyncRx) + asyncRx/clauses
AsyncObservable.intervalMillis 50
|> AsyncObservable.map (fun i -> ...)
|> AsyncRx.run onNext onError onCompleted

// DON'T: open the operator module to get bare names (floods every operator into
// scope — the [<AutoOpen>]-style unbidden-name problem §A5/§A9), or wrap a plain
// consume in Hopac
open AsyncRxHopac.AsyncObservable
intervalMillis 50 |> map (fun i -> ...)
Hopac.run <| job { let! sub = AsyncRx.subscribeJob … in do! sub.Completion }
```

**Checklist — adding an operator/combinator**
1. Define it once, canonical name, on the `AsyncObservable` surface, beside the
   operators of its algebraic role (transform / product / choice / interleave /
   conversion). Refer to sibling operators **unqualified** inside the module.
2. No facade, no alias — the qualified canonical call is the surface. If the name
   is genuinely cryptic, rename it; don't add a synonym.
3. Add an example that calls it **qualified** (`AsyncObservable.foo`) (A5).
4. Add a test that records the full notification sequence (A6).
5. Uphold the A8 contracts (serialized, single terminal, backpressure).

**Checklist — adding an interop bridge (Task / AsyncSeq / …)**
1. `ofX`/`toX` naming, on the `AsyncObservable` surface beside the existing
   boundaries. The kernel now carries the interop dependency directly (the
   consolidation chose one-fewer-file over a dependency-free kernel); a genuinely
   heavyweight, rarely-used dependency is the one case to reconsider isolating.
2. Reuse kernel internals (`forwardInto`, `giveOrStop`, `terminalGate`,
   `rootContext`) rather than re-deriving lifecycle.
3. Document the boundary cost; unwrap `AggregateException` at the seam
   (`unwrapAggregate`).
4. Example in the qualified dialect (`AsyncObservable.toAsyncSeq`); tests
   recording sequences + bounded waits.

---

## Part C — Constraint-cell engines specifics

The two existing files (`constraintprop.fsx`, `constraintprop2.fsx`) are **failed
prototypes under study**, not style exemplars — read them through the post-mortem
in [constraint-cell-genealogy.md](docs/constraint-cell-genealogy.md), and read
the successor's intended conventions in
[constraint-engine-design-journal.md](docs/constraint-engine-design-journal.md).
What carries forward:

- **Vocabulary is lattice-theoretic and frozen first.** cell = lattice element,
  merge = meet (⊓), propagator = monotone contribution, `Top` = universe, `⊥` =
  empty/contradiction, retraction = trail/premises. Freeze these laws early; let
  the domain representation (`Set<int>` clear ↔ bitset fast) and the optimization
  mechanism (SRTP vs struct-generic vs object interface) stay tentative until
  benchmarks decide (journal D3/D6).
- **State the order before saying "fixed point."** Name the refinement order;
  don't write "least fixed point" without saying which order it's least in (A8).
- **No rich error DU in the hot path** — contradiction is a compared empty
  domain, not a `CellError` value (A8/A9, and the `shallowEquals` post-mortem).
- **Adaptive is the reactive shell, not the kernel** (A3 / journal D7).
- **Distinguish the paradigms by architecture, not by the words in the code**
  (A1).

The design journal is the authority on its *own* process — it carries a
"Collaboration culture / Editing rules" section (living vs append-only,
attribute-proposals/own-decisions-together, confidence-vs-commitment). A7
summarizes the parts that generalize; defer to the journal when amending it.
