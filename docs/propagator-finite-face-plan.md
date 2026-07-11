# Generalized Finite Face and WFC Application Plan

**Status:** Ratified implementation plan, 2026-07-10  
**Scope:** General finite constraint propagation, Friendly UX, seeded generation, snapshot observation,
and the new WFC application proof  
**Historical basis:** The recut third-slice order in
[propagator-surface-design.md](propagator-surface-design.md), plus Deen's subsequent Friendly and relation
clarifications  
**Execution record:** Append-only in section 13

This is an enduring architecture and execution plan, not a temporary compaction handoff. It explains why
the finite face is being remodeled, what belongs at each boundary, what the outward experience must feel
like, how the optimized implementation is allowed to work, and how the result will be proved. Later
implementation results must be appended to section 13 rather than rewriting this plan to resemble the
code after the fact.

## 1. Purpose

WFC is finite constraint propagation with a particular application vocabulary: tiles, compatibility,
and grid topology. The current library can describe that meaning, but its optimized execution path was
dimensioned by the early Sudoku proof. It fixes every finite cell to one `uint64`, allocates a closure per
placed relation, and tuple-enumerates a general GAC predicate on every fire. The historical WFC engine then
demonstrated the missing general finite machinery: runtime-width bitsets, compact binary support tables,
a flat store, premise-free collapse, restart, and efficient change-driven selection.

The task is to generalize that machinery into the propagator library while keeping it internal. Friendly
must be the coherent outward UX for ordinary and optimized use. A consumer should never need to understand
`General`, `Optimized`, lowering, bitset width, table layout, premise representation, or engine lifecycle to
author a finite problem. Easy cases must stay easy; arbitrary relations, repeated relations, custom
topologies, generation, and observation must remain possible and understandable.

The new WFC file is the demanding application proof. It must be concise and domain-shaped because the
library is doing the general work, not because a WFC-local wrapper hides another propagator engine.

## 2. Ratified Principles

1. **One semantic surface, hidden execution machinery.** Friendly is the user-facing library UX.
   `Optimized` is an internal implementation and test target, not a second ceremony users must learn.

2. **WFC is an application, not a core feature.** Tiles, compatibility directions, grid dimensions,
   neighborhood construction, weights, and rendering stay outside propagator core. Finite domains, binary
   and n-ary relations, repeated relation scopes, collapse, generation, and observation are general.

3. **Semantic breadth is not traded for the fast path.** Binary relations receive a compiled optimized
   path. Arbitrary n-ary relations remain authorable through the same outward model and retain a correct
   generic GAC path. A fast special case may improve execution but may not fracture authoring.

4. **Repeated meaning is represented once.** A relation used on many scopes is authored once and applied
   many times. The implementation must not depend on accidental F# closure identity and must not compile
   the same support table once per edge.

5. **No optimized sibling architecture.** The generalized optimized finite store is runtime-width and
   cell-major. Its one-word case is `W = 1`, not a separate engine. Domain width is not a capability limit.

6. **Premise width and domain width are independent.** The optimized premise representation remains one
   `uint64`. That is an honest solve/edit capability. It does not constrain the number of values in a
   finite domain or the number of cells that premise-free generation may collapse.

7. **`solve` and `generate` are different verbs.** `solve` remains complete deterministic DDB with authored
   value order. `generate` is seeded stochastic restart, consumes no premises for collapses, and returns one
   map per call. Neither silently changes the other's policy.

8. **Observation reports state, not engine events.** The public callback carries a cell and its current
   `CellState`. Subscription establishes a synthetic initial snapshot. Last-write-wins replay reconstructs
   final state without inspecting the net.

9. **Methods and `Ops` remain equal dialects.** New Friendly capabilities must be available through the
   object-shaped and function-shaped forms without duplicating semantics. Different spellings are allowed
   only where F# overload rules require them.

10. **Performance plumbing is private.** Multiword slices, table ids, flattened scopes, arc arrays, heaps,
    support masks, reset machinery, and backend selection do not become user-facing concepts.

11. **The historical WFC file is evidence.** `propagator-wfc.fsx` remains unchanged. The new implementation
    lives in a second file and is compared against the historical engine honestly.

12. **Benchmarks are relative and correctness-gated.** Run the complete relevant suite first, record the
    active power plan, preserve row order, and interpret same-session ratios and scenario context. Absolute
    timings on this machine are environmental observations.

## 3. Current Architectural Failure

The failure is an overfit finite backend, not an inability of constraint propagation to express WFC and
not a need for WFC-specific propagation machinery.

### 3.1 Relation placement is fused with relation meaning

Today a `RelationBox` contains one `cells` list and one `allows` predicate. A 250,000-cell grid therefore
creates a box for every edge even when all east-facing edges share exactly one relation. If lowering simply
compiles every box, table memory and construction become proportional to edges times domain squared.

### 3.2 The optimized value representation is fixture-sized

`optimizedDomainWidth = 64` is the Sudoku-era one-word choice. It was accidentally treated like the
separate 64-premise support limit. A finite domain of 65 values is semantically ordinary and must lower.

### 3.3 Generic GAC is paid repeatedly for a common binary case

`Gac.narrow` enumerates the Cartesian product every time a relation fires. This is the correct fallback for
arbitrary n-ary predicates. For a binary relation over a stable finite domain, the allowed pairs can instead
be sampled once at lowering and represented as support masks used by every placement.

### 3.4 Friendly currently names one backend internally

`PayloadAdapter` stores a `GeneralNet`, so the Friendly finite experience cannot reach optimized generation
without exposing or recreating `Model -> Optimized.lower -> Optimized.generate`. A WFC-local helper around
that pipeline would conceal the UX failure. Friendly must delegate semantic operations without naming the
backend in its public shape.

### 3.5 The deferred observation shape is not lattice-general

The current `CellChange` taxonomy assumes candidate lists and resolution. Scalar and interval lattices do
not share those semantics. A current `CellState` snapshot already expresses both finite and rich domains
without inventing false candidate behavior.

## 4. Ownership Boundaries

| Layer | Owns | Must not own |
|---|---|---|
| Core vocabulary | domains, cells, constraints, grouped scopes, models, cell state, solutions | WFC tiles, grids, directions, rendering |
| Core engines | General reference behavior, multiword finite store, compiled binary propagation, generic n-ary fallback, premise support, reset/collapse, PRNG, observation hooks | user-facing WFC policy or UI |
| Friendly | payload/view adaptation, semantic methods, `Ops`, CE ergonomics, premise names, clean generation/observation entry points | propagation algorithms, bitsets, lowering pipelines, duplicated model semantics |
| WFC application | tile vocabulary, compatibility, grid construction, optional weights, rendering, app-level validation | a private propagator store, worklist, GAC, trail, reset, or observer implementation |
| Tests | direct General/Optimized differential access, independent oracles, test-local GAC, instrumentation | shipped backend-selection UX |

The dependency boundary remains separate: core and Friendly are pure F#. An AsyncRx/Hopac adapter remains
an outer dependency-bearing layer and is not part of this slice.

## 5. Target Outward UX

The examples below state the cognitive shape. Exact final spelling may be tightened during the UX-first
phase, but implementation may not replace these semantic calls with public lowering or representation
plumbing.

### 5.1 Ordinary finite problem

```fsharp
let net = Domain.finite [ Red; Green; Blue ]
let left = net.Cell "left"
let right = net.Cell "right"

net.Relate([ left; right ], function
    | [ a; b ] -> a <> b
    | _ -> false)

net.Given(left, Red)
let solution = net.Solve()
```

The current `Constrain(Constraint.relation ...)` route may remain as the general escape hatch. `Relate` is
the direct Friendly expression of the common semantic operation, not another constraint implementation.

### 5.2 Arbitrary n-ary relation

```fsharp
net.Relate([ a; b; c ], function
    | [ x; y; z ] -> validTriple x y z
    | _ -> false)
```

This is not lowered to a binary approximation. General and Optimized use the same authored predicate; the
optimized face takes the honest n-ary GAC fallback.

### 5.3 One relation over many scopes

```fsharp
net.RelateMany(edges, function
    | [ a; b ] -> compatible a b
    | _ -> false)
```

`edges` may be a sequence of cell lists or an equally clear scope value. Friendly must not expose flattened
arrays or relation-table ids. The core stores the repeated relation compactly and compiles one binary table
for the group.

### 5.4 Custom topology

```fsharp
let scopes =
    graph.Edges
    |> Seq.map (fun edge -> [ cells.[edge.From]; cells.[edge.To] ])

net.RelateMany(scopes, relation)
```

Grid topology is only one producer of scopes. The general interface remains useful for graphs, schedules,
layout constraints, and other finite CSPs without acquiring a core `Grid` concept.

### 5.5 General generation and observation

```fsharp
use subscription = net.Observe(fun (cell, state) -> render cell state)

match net.Generate(seed) with
| Ok solution -> useSolution solution
| Error failure -> report failure
```

The simple call accepts a seed and uses a documented default restart bound. A second overload or options
value may expose the bound if required by the failure oracle. Do not require an options record for the easy
case merely because one may be useful for advanced control. The result is one map, never a lazy stream.

### 5.6 WFC application

```fsharp
let grid = Grid.create 64 64 [ Sea; Coast; Land ]
Grid.constrain compatibility grid

use subscription = grid.Observe render
let map = grid.Generate 42UL
```

`Grid`, tile names, compatibility directions, and rendering live in `propagator-wfc-app.fsx`. Their
implementation delegates finite cells, grouped relations, generation, and observation to Friendly. If the
WFC file must mention `Model`, `General`, `Optimized`, lowering, masks, table compilation, or propagation
queues in its application-facing path, the UX acceptance test has failed.

## 6. General Relation Representation

### 6.1 Required semantics

A relation has one membership predicate and one or more scopes. Every scope in a group has the same arity
and uses the same ordered argument meaning. Grouping is semantic reuse, not an optimization hint that may be
ignored by authoring.

Conceptually:

```fsharp
type RelationBox<'a> =
    { arity: int
      scopes: Cell<'a> array       // internally flat: scope i begins at i * arity
      allows: 'a list -> bool }
```

The exact stored collection may differ, but it must avoid retaining one list/closure/table per WFC edge.
Public helpers preserve the singular form and add the repeated form without a new authoring noun:

```fsharp
Constraint.relation  : Cell<'a> list -> ('a list -> bool) -> Constraint<'a>
Constraint.relations : seq<Cell<'a> list> -> ('a list -> bool) -> Constraint<'a>
```

The singular helper wraps one scope. The repeated helper validates uniform nonzero arity and stores one
predicate with all placements. Friendly delegates through `Relate` and `RelateMany`; `Ops` receives matching
function-shaped verbs.

### 6.2 Lowering rules

- General expands every scope into the existing clear GAC propagator behavior.
- Optimized recognizes arity two, samples `allows` over `domain x domain` once for the grouped box, and
  produces forward and reverse support masks so asymmetric relations remain correct.
- Every binary placement stores only endpoints plus a compiled-table reference.
- N-ary groups retain the shared predicate but use generic `Gac.narrow` per scope.
- Unary and zero-support-at-top relations participate in initial propagation; a model with no givens must
  still reach its real initial fixpoint.
- `allows` is a pure, stable membership predicate. Compilation changes when it is evaluated, so side effects
  were never a valid authoring contract and must be documented as such.
- Table reuse is explicit through grouping. Do not cache by F# function reference identity.

### 6.3 WFC grouping

The WFC grid builder emits at most one grouped relation per distinct directional compatibility rule, not one
box or table per edge. A four-direction presentation may compile four tables; symmetric rules may share
fewer. The application chooses topology and direction meaning, while the core sees only repeated binary
scopes.

## 7. Generalized Optimized Finite Engine

### 7.1 Store

- Compute `W = ceil(domainCount / 64)` at lowering.
- Store all candidate words in one flat cell-major `uint64[]`.
- Mask unused high bits in the final word.
- Keep one `uint64` premise-support word per cell.
- Keep authored values in an ordered array and a read-only value-to-index dictionary.
- Decode strictly in authored order. Dictionary enumeration never determines behavior.
- Remove `FiniteDomainTooWideForOptimized`; duplicate domain values remain rejected at authoring.

The existing one-word implementation becomes the `W = 1` case. A construction-time-selected one-word hot
loop is allowed if benchmarks prove it necessary, but it remains an internal specialization of the same
store and laws, not another face.

### 7.2 Binary tables and arcs

For each grouped binary relation, compile bitset rows:

```text
forward[a] = values of b for which allows [a; b]
reverse[b] = values of a for which allows [a; b]
```

An arc references a source cell, target cell, table id, and direction. Watches map a changed cell to its
outgoing arcs. Firing unions the support rows of the source candidates, meets that mask into the target, and
propagates only on a real narrowing. This is generic bit-parallel arc consistency, not a WFC engine.

### 7.3 N-ary fallback

N-ary scopes decode authored-order candidates, call the single private `Gac.narrow`, encode the narrowed
sets, and meet them into targets. This path is expected to be slower and must remain correct. Do not contort
the public API or the binary hot loop to pretend arbitrary n-ary predicates have the same cost.

### 7.4 Premises, reset, and initial propagation

- Givens and named authored assumptions use premise bits `0..63`.
- Structural relation narrowing carries the union of source supports.
- Premise-free generated collapses contribute support zero.
- Retraction re-establishes a correct fixpoint and emits snapshots for every actual state change.
- Reset removes generated collapses, restores authored information, re-runs initial propagation, and keeps
  subscriptions attached.
- Sealing/build completion must enqueue the constraints needed for a genuine initial fixpoint, including
  models with no givens.

### 7.5 Public invisibility

No store, table, arc, word slice, heap, or premise-bit type is added to Friendly. Direct core
`General`/`Optimized` operations remain available for differential tests and low-level library work, but
ordinary authoring and execution do not require them.

## 8. Solve and Generate

### 8.1 Solve remains unchanged in meaning

- Complete dependency-directed depth-first backtracking.
- Deterministic first open cell according to authored model order.
- Deterministic candidate order according to authored domain order.
- Failed guess premise ids reclaimed in LIFO order.
- Existing `[2; 1]` and sparse-search canaries remain.

`PremiseWidthExceeded` moves from `Optimized.lower` to `Optimized.solve`. Lowering rejects only conditions it
actually cannot represent, including more than 64 authored live premises. The 65-cell guard becomes a
positive lowering/generation test and a loud pre-search solve failure.

### 8.2 Generate is a general sampler

For each attempt:

1. Reset to authored information and propagate to fixpoint.
2. If contradictory, fail this attempt.
3. If all cells are singleton, return one solution snapshot.
4. Choose the open cell with minimum candidate count; tie by authored cell order.
5. Draw uniformly from its authored-order candidate list using the explicit pure PRNG.
6. Collapse without allocating a premise and propagate.
7. On contradiction, discard the whole attempt and restart.
8. Return an explicit failure after the bounded restart count.

There is no DDB interleaving inside `generate`. Repeated maps come from repeated calls with explicit seeds.

### 8.3 RNG and picker

- Implement a pure, version-stable generator such as SplitMix64 in core.
- Use unbiased bounded draws; document the exact seed-to-stream behavior with a small golden test.
- Random draws depend only on visible candidate lists and authored cell order.
- General and Optimized may use different picker data structures but must select the same MRV cell.
- Optimized should use a stale-entry min-heap or equivalent change-driven structure so large generation does
  not scan every cell after every collapse.
- Weighted entropy and weighted tile selection are deferred strategy extensions. Plain MRV plus uniform
  choice is the general default for this slice.

### 8.4 Result and materialization

One call returns one explicit success or bounded-restart failure. Keep the current `Solution<'a>` semantic
contract for this slice. Because an F# `Map` may dominate a 250,000-cell benchmark, record materialization
cost honestly. Do not silently invent a second solved handle. If measurement proves `Map` is the limiting
cost, append a follow-up design decision for a compact immutable snapshot rather than changing the contract
inside this implementation pass.

## 9. Snapshot Observation

Replace the deferred public `CellChange` taxonomy with a snapshot callback:

```fsharp
Cell<'a> * CellState<'a>
```

Required behavior:

- `onCell` synchronously emits the selected cell's current snapshot when subscribed.
- `onNet` synchronously emits one current snapshot per cell in authored order when subscribed.
- Every subsequent actual state change emits the new snapshot.
- Narrowing, contradiction, restoration, DDB backtrack, generated collapse, and restart all use the same
  event shape.
- Folding by cell key with last-write-wins reproduces final state.
- Callbacks run on the engine thread, mid-propagation.
- `IDisposable.Dispose` is idempotent and stops later delivery.
- No core buffering, scheduling, backpressure, AsyncRx, or rendering policy is added.
- With no subscribers, the optimized hot path pays only the smallest practical notification check.

This is coherent for `FiniteCandidates` and `LatticeValue`; no false notion of lattice resolution is needed.

## 10. Friendly Integration

### 10.1 Outward rule

Friendly presents semantic operations and does not present backend selection. A user may author finite cells,
single or repeated relations, givens/restrictions, solve, generate, and observe without touching a core model
record or lowering result. General and Optimized remain names used by tests and implementers.

### 10.2 Internal delegation

Refactor the private adapter away from a hard-coded `GeneralNet` field toward a private record of semantic
operations. That record may delegate rich lattices to General and portable finite execution to the
generalized finite machinery. It is delegation only: Friendly must not implement propagation, GAC, reset,
search, table compilation, or observer replay.

The authoritative authored definition and live engine state must have one core-owned source of truth.
Do not maintain two independently mutable models in Friendly and try to synchronize them. The exact private
core handle is implementation discretion, provided these laws hold:

- Current scalar, interval, provenance, restriction, assumption, retraction, and solve behavior remains.
- Portable finite `Generate` and `Observe` reach optimized execution without public lowering plumbing.
- Reads never rebuild a model or create a second live handle.
- An explicit structural edit may incur an internal rebuild if unavoidable, but ordinary reads, facts,
  generation steps, and observation may not rebuild merely to simulate liveness.
- Backend choice does not appear in ordinary method signatures.
- A low-level backend override, if retained for differential testing, stays outside Friendly's normal UX.

### 10.3 Surface discipline

Provisional semantic verbs are:

- methods: `Relate`, `RelateMany`, `Generate`, `Observe`
- functions: `Ops.relate`, `Ops.relateMany`, `Ops.generate`, `Ops.observe`

Before locking names, write compile-only/readability examples for the easy binary case, arbitrary n-ary
case, repeated custom graph, and WFC grid. Prefer overloads on methods where natural; add separate function
spellings only where F# cannot express the overload. Do not add public builder, backend, table, strategy, or
compiled-network types merely to route calls internally.

`Constrain` remains the advanced semantic escape hatch for an already-authored `Constraint`. It is not the
required path for common relation authoring.

## 11. New WFC Application

Create `propagator-wfc-app.fsx` and leave `propagator-wfc.fsx` byte-for-byte unchanged.

### 11.1 Application content

- A small readable tile DU, initially at least three tiles.
- Directional compatibility authored as ordinary application data/functions.
- A grid helper that creates cells and grouped edge scopes.
- A `Grid.constrain`-shaped operation that delegates repeated binary relations to Friendly.
- Seeded generation through the general Friendly verb.
- Snapshot observation through the general Friendly verb.
- A compact textual rendering or equally direct result presentation.

The application-facing path should load `propagator-friendly.fsx`. Direct core face calls are permitted only
inside clearly test-local differential helpers, never as the WFC authoring story.

### 11.2 Independent oracles

1. **Map validity:** walk the produced grid without calling an engine; every cell is resolved and every
   adjacent pair satisfies compatibility.
2. **Cross-route seed:** at a small size, the same authored model and seed produce the same map through the
   General reference and generalized optimized route.
3. **Replay:** fold snapshots from their synthetic baseline; the final candidate state equals the generated
   grid without reading the net during replay.
4. **Restart exhaustion:** a contradiction-capable model demonstrates restart and explicit bounded failure.
5. **Friendly readability:** the application section contains no model/lowering/representation/engine nouns.

### 11.3 Historical comparison constraint

The historical script ends with an unconditional `exit (main ())`, so it cannot currently be `#load`ed as a
library without terminating the host process. Preservation is stronger than benchmark convenience. The
default comparison therefore runs the historical and new scripts consecutively as one controlled benchmark
session, records the process boundary, and makes no false same-process claim. If exact same-process access is
later judged essential, stop and request explicit permission for a minimal historical harness guard; do not
silently edit the preserved file or copy its engine into the new file.

## 12. Ordered Implementation and Acceptance

### Phase 0 - Establish the baseline

- Re-read `AGENTS.md`, this plan, and the recut work order after compaction.
- Inspect the dirty worktree and preserve every unrelated/user-authored change.
- Run silent core and Friendly loads plus the complete correctness suite.
- Record current same-suite finite benchmark ratios before changing hot loops.
- Do not edit the historical WFC file.

### Phase 1 - Lock UX examples before mechanics

- Add compile-oriented tests/sketches for ordinary binary, arbitrary n-ary, repeated scopes, custom graph,
  generation, observation, and the intended WFC call site.
- Select the smallest method/`Ops` spelling that reads clearly across all examples.
- Reject any design requiring a public backend, lowering, representation, or compiled-table concept.

### Phase 2 - Generalize relation grouping

- Change relation storage to represent one predicate over one or many equal-arity scopes.
- Preserve `Constraint.relation`; add the minimal repeated-scope helper.
- Update General lowering and independent test oracles.
- Prove grouped and individually expanded models have identical fixpoints and solutions for binary and n-ary
  examples.

### Phase 3 - Replace the one-word optimized finite store

- Introduce the flat runtime-width store and authored-order codec.
- Compile grouped binary relations once and install compact arcs.
- Retain generic n-ary GAC.
- Establish initial fixpoint behavior, support propagation, retraction, and `W = 1` fidelity.
- Remove only the domain-width capability error; preserve premise-width truth.

### Phase 4 - Implement generation and observation

- Add pure RNG, bounded unbiased draws, MRV selection, premise-free collapse, and reset/restart.
- Relocate solve premise preflight and rewrite its guard test as specified.
- Implement snapshot subscriptions on both faces, including synthetic baselines and disposal.
- Keep `solutions` deferred unless implementation genuinely requires it; do not add it by symmetry.

### Phase 5 - Complete Friendly optimized UX

- Refactor private delegation without moving propagation mechanics into Friendly.
- Add the ratified relation, generation, and observation verbs to methods and `Ops`.
- Preserve all current facade examples and provenance behavior.
- Verify that optimized execution is reachable through Friendly with no backend choice in the call site.

### Phase 6 - Build the WFC application proof

- Create the new file over Friendly.
- Add tiles, compatibility, grid grouping, rendering, and all independent oracles.
- Keep every WFC-shaped concept local and every propagation mechanism delegated.
- Run it cleanly under FSI with useful, bounded output.

### Phase 7 - Verify performance and document reality

- Run all correctness before timing.
- Run the complete benchmark set in preserved order and record the active power plan.
- Compare existing `W = 1` General/Optimized rows to detect a regression.
- Run historical and new WFC workloads consecutively at matching scenarios where the preserved harness
  permits; state process boundaries and workload differences.
- Add 16x16, 32x32, 64x64, and larger new-route rows as FSI tolerates, working toward 500x500.
- Report best, mean, ratios, failure/restart context, and whether solution materialization is included.
- Do not optimize the n-ary GAC fallback merely to improve a binary WFC number.

### Phase 8 - Close the record

- Run `git diff --check` and all silent-load/correctness commands again.
- Append benchmark evidence to `benchmarks.md`; never rewrite earlier measurements.
- Append current implementation status and dated history to `propagator-surface-design.md`.
- Append the execution outcome to section 13 of this plan.
- Keep this plan and both WFC files.

### Acceptance matrix

| Concern | Required proof |
|---|---|
| Existing behavior | all current Friendly and differential rows pass |
| Domain width | 65-, 128-, and at least one wider-than-one-word domain lower and propagate correctly |
| One-word cost | existing optimized/general canary shows no material systematic regression |
| Relation reuse | grouped binary scopes match expanded semantics and compile one table per group |
| Arbitrary relations | n-ary relation remains authorable and matches independent oracle |
| Solve law | authored order and sparse DDB canaries unchanged |
| Premise law | 65-cell model lowers/generates; optimized solve fails before search |
| Generate law | same model + seed gives identical General/Optimized map |
| Restart law | collapses use no premises and bounded exhaustion is explicit |
| Observation law | synthetic baseline plus last-write-wins fold equals final state |
| Friendly UX | no backend/lowering/representation nouns in ordinary finite or WFC calls |
| WFC validity | independent adjacency walk accepts every generated map |
| Preservation | historical WFC file has no diff |
| Performance | correctness-gated relative tables with power plan and scenario context |

### Risks and stop conditions

- **`W = 1` regression:** profile before adding a sibling engine. A construction-time internal fast loop is
  allowed; public architecture duplication is not.
- **Per-edge allocation survives grouping:** stop if the model still retains a closure/list/table per WFC
  edge. Fix scope storage rather than disguising it in benchmarks.
- **Asymmetric binary bug:** forward and reverse tables must be tested independently.
- **Predicate impurity:** document purity and add a deterministic compilation test.
- **Support drift:** compare support/retraction behavior against General on small models.
- **Observer leakage:** repeated subscribe/dispose and restart cycles must not retain handlers.
- **Facade dual state:** stop if Friendly begins synchronizing two independently mutable engines/models.
  Move ownership into one core abstraction.
- **Materialization dominates:** measure and report; do not silently change `Solution` or benchmark only an
  internal array.
- **Historical benchmark cannot be loaded:** use the declared controlled two-process session or request an
  explicit preservation exception. Do not work around it by copying code.
- **Surface proliferation:** if a new public type exists only to route optimized internals, remove it and use
  private delegation.

### Non-goals for this pass

- Weighted tile choice or Shannon entropy.
- Fair/interleaving search or persistent search stores.
- Hansei-backed generation.
- AsyncRx/Hopac adapters, scheduling, buffering, or backpressure.
- Godot integration.
- Heterogeneous finite domains in one model.
- AC-4 support counters unless comparative evidence establishes the current compiled binary route as the
  bottleneck and a separate design decision approves the change.
- A core `Grid`, `Tile`, `Direction`, or WFC preset.
- A public backend selector.
- A lazy `solutions` implementation added merely for symmetry.

## 13. Append-Only Execution History

- **2026-07-10 - Codex.** Created this durable plan from the accepted recut order and Deen's clarifications:
  Optimized remains internal; Friendly is the low-cognitive-overhead outward UX for optimized finite work;
  easy authoring stays easy while arbitrary n-ary relations, repeated scopes, and custom topologies remain
  understandable; WFC contributes only its domain vocabulary and application proof. No implementation was
  performed as part of creating the plan.

-- Codex, 2026-07-10

- **2026-07-11 - Codex (comparative benchmark strengthened).** Added opt-in matching 500x500 gravity and
  3-color rows to `propagator-wfc-app.fsx`. Both use the historical constraint semantics and seed 42 over a
  retained Friendly network; lowering and independent validity checks are outside the timed interval, as
  in the preserved harness. Two measured trials after warmup produced Friendly gravity 5,959.679 ms best /
  6,174.909 ms mean and Friendly 3-color 4,958.214 ms best / 5,155.247 ms mean. The preserved run recorded
  363.597 ms and 54,743.329 ms respectively. The record states the remaining `Map` versus `int[]`, SplitMix
  versus `System.Random`, restart versus DFS, process, and sampling differences rather than claiming false
  mechanical identity.

-- Codex, 2026-07-11

- **2026-07-10 - Fable (Tier 0) acceptance gate.** Reviewed this plan line-by-line against the recut work
  order (propagator-surface-design.md section 10) and accepted it; the "Ratified" status header is true as
  of this gate. Every locked rule survives intact: the one width-parameterized store with `W = 1` as a case
  not a sibling, premise-64 as a solve capability with the preflight relocation and guard-row rewrite, the
  `solve`/`generate` verb split and premise invariant, SplitMix64 with the same-seed cross-route theorem,
  snapshot observation with the synthetic baseline and last-write-wins replay, and byte-for-byte
  preservation of the historical WFC file. Two Codex improvements over the order itself are credited on the
  record: (1) section 11.3 caught an error in the order — "may `#load` the historical file" is unworkable
  because that file ends with an unconditional `exit (main ())` (verified, line 1108); the two-process
  consecutive-session resolution with preservation ranked above benchmark convenience is the correct
  escalation shape. (2) The grouped-relation representation (sections 3.1/6) closes a gap the order left
  implicit: its attention list demanded per-distinct-relation table dedupe, but the ungrouped `RelationBox`
  gave authors no way to express sharing except accidental closure identity, which section 6.2 rightly
  forbids; `Constraint.relations`/`RelateMany` makes the sharing structural, and the forward-plus-reverse
  table requirement with an independent asymmetric test strengthens a shape the order proved only on a
  symmetric-leaning fixture. Also credited: section 7.4's no-givens initial-fixpoint requirement (WFC has
  no givens) and section 8.4's refusal to let `Map` materialization cost silently mutate the `Solution`
  contract mid-pass. Two watch items flagged for future gates, not blocking: the Phase 5 Friendly
  delegation refactor is the riskiest step (the `PayloadAdapter` hard-codes `core: GeneralNet`; the gate
  must audit specifically against the plan's own dual-mutable-model stop condition), and Phase 1's
  "no backend/lowering/representation nouns" criterion must be checked against the actual authored
  examples, not this plan's idealized ones.

-- Claude (Fable 5, Tier 0), 2026-07-10

- **2026-07-10 - Codex (execution complete).** Implemented phases 0-8. Grouped binary and n-ary scopes
  match individually expanded models; 65-, 128-, and 130-value domains lower and propagate; asymmetric
  forward/reverse tables pass; the 65-cell model lowers and generates while `solve` fails before search;
  seed `0` is pinned to `[5; 0; 9; 4]` and same-seed General/Optimized maps agree; snapshot baseline/replay,
  disposal, restart exhaustion, support, and retraction pass. Friendly finite work now delegates to one
  live optimized handle through a private semantic-operation record, while scalar/interval retain one
  live General handle. The actual UX examples contain no backend/lowering/representation nouns.

  Added `propagator-wfc-app.fsx` over Friendly with WFC-only tiles, directions, topology, rendering, and
  four independent oracles; no propagation mechanics are local. The historical WFC file retained SHA-256
  `836A49E4E511E23BE0A05A598403EBFE2E9512B1C1F82583B1FA8498FD357621`. During the large-grid gate an
  endpoint-membership check was found to scan all cells for every scope endpoint; replacing it with the
  core net's cell-id set changed the 500x500 stress case from not completing within five minutes to a valid
  6.673-second result including `Map` materialization. Final same-process finite canary: General 32,651.1
  us best, Optimized 1,001.6 us best, 32.60x ratio on Power saver. Full historical and new WFC results are
  recorded append-only in `benchmarks.md`.

-- Codex, 2026-07-10

- **2026-07-11 - Fable (Tier 0) implementation review gate: PASSED; gravity-optimization proposal:
  ACCEPTED with notes.** Reviewed the completed phases 0-8 against this plan on primary evidence, not the
  execution record's prose. Both pre-flagged watch items pass. Phase 5: the hard-coded `core: GeneralNet`
  adapter field is gone; Friendly holds a private `CoreOperations` closure record over exactly one core
  handle per network, its only retained state the premise name dictionaries (provenance display, not a
  model); no propagation, GAC, reset, table, or observer mechanism exists in the facade, so the
  dual-mutable-model stop condition has nothing to bite. Phase 1: checked against the actual files — the
  WFC app authors through `Tile`/`Direction`/`Compatibility`/`Grid` over `Domain.finite`, `RelateMany`,
  `Generate`, `Observe`; core-face nouns are confined to `module private Oracles`, the permitted
  test-local differential carve-out. Locked rules verified in code: one runtime-width store with a
  final-word mask (`W = 1` a case, not a sibling); one forward+reverse table per grouped relation box;
  n-ary via generic GAC; `Collapse` writes support `0UL` (the premise invariant holds at mechanism level);
  restart re-asserts givens; per-attempt observer subscriptions are `use`-scoped; SplitMix64 with
  rejection-sampled draws consuming randomness only from visible candidate counts; MRV ties by cell id.
  The premise split is sharper than the recut order's slogan and correct: `lower` keeps only givens <= 64
  (givens physically consume premise bits at lowering), `solve` preflights givens + cells <= 64 loudly
  before search, `generate` checks neither; the rewritten guard row pins exactly this. Preservation
  verified independently: recomputed SHA-256 matches `836A49E4...357621`, no diff. Empirical: the full
  Friendly suite and all four WFC oracles ran green during the review.

  One non-blocking finding: `cellPosition` in the optimized facade is a linear `Seq.tryFindIndex` — the
  same accidental-linear-scan family as the endpoint bug this plan's own execution already caught once.
  No current benchmark path hits it (generation and `solution` bypass it), but Friendly `Value` on a
  250,000-cell net is quadratic in aggregate. It should become an incrementally maintained dictionary,
  folded into the optimization pass below as a candidate-0-grade direct win.

  The 2026-07-11 gravity-optimization proposal (`benchmarks.md`) is accepted: measure-before-remodel with
  a phase split, strict boundary (WFC stays app-local; reusable wins go to the private core; Friendly
  gains no vocabulary), honest stop conditions including stopping when the residual gap is the immutable
  `Map` contract. Its strongest item is the seed-42 fingerprint locking the search tree rather than merely
  a valid output. The candidates' diagnoses were verified against the code: `fireArc` allocates a fresh
  `uint64[]` per firing; gravity's always-true east rule authors 249,500 no-op scopes compiling to 499,000
  directed arcs; materialization builds a full candidate list per solved cell to take its head. Three
  executor notes, none blocking: (1) candidate 1 is a model change, outside the fingerprint's
  order-preserving framing, yet must pass the fingerprint too — and should: universal arcs never narrow
  and never union supports, transmitting only bottom from an already-empty source, and during `generate`
  any bottom aborts the attempt before another draw; require the empirical proof, not the argument. The
  proposal's caveat that the core must never discard universal relations stands, precisely because that
  bottom transmission is observable. (2) Candidate 3's shared scratch interacts with the synchronous
  mid-propagation observer contract: a handler that re-enters the engine would corrupt an in-flight arc
  computation; document that observer handlers must not mutate the network or add a cheap re-entrancy
  guard before introducing scratch reuse. (3) Candidate 2 (cached settled baseline) is the pass's riskiest
  item — the in-core cousin of the dual-model hazard; beyond the proposal's own invalidation rules, hold
  it to a dedicated test where an assertion or retraction lands between two `Generate` calls, proving the
  baseline refreshes rather than serving stale quiescence, and ensure the re-assert `rebuild` path
  invalidates it too.

-- Claude (Fable 5, Tier 0), 2026-07-11

## 14. Gravity Optimization Proposal (2026-07-11)

The matching gravity row is now the clearest performance question: Friendly takes 5,959.679 ms best while
the preserved structural-neighbor engine takes 363.597 ms. That 16.39x observation is not a promise that
the generalized route can or should become mechanically identical to the historical engine. It is a reason
to locate the cost precisely. The public immutable `Solution Map`, stable SplitMix choices, bounded-restart
search, and general relation engine remain part of the current contract unless a separate design decision
changes them.

The optimization boundary is strict. WFC authoring improvements stay in `propagator-wfc-app.fsx`; reusable
finite-relation improvements may enter the private optimized core; no grid, tile, direction, or gravity
concept enters the library. Friendly gains no representation or backend vocabulary. Prefer a small direct
change over a second engine or a speculative abstraction.

### Measure before remodeling

Split the current aggregate timing into private benchmark phases without changing the public API:

1. Network authoring and deferred lowering. This remains outside the matching generation stopwatch, but
   should still be measured because a 500x500 model creates 250,000 cells and about 998,000 directed arcs.
2. `ResetGenerated` and restoration of the settled non-generated state.
3. Collapse propagation, including arc dequeues, actual target changes, support-row unions, and bytes
   allocated in the binary hot loop.
4. MRV picker setup and maintenance, including initial heap construction, stale dequeues, and change-driven
   insertions.
5. Final singleton extraction and immutable `Map` materialization.

Record counts as benchmark-only instrumentation or private diagnostics; do not expose engine plumbing
through Friendly. Before changing mechanics, record a seed-42 gravity result fingerprint so every
order-preserving optimization can prove that it retained the same search tree and output, not merely a
valid output.

### Ordered candidates

1. **Do not author a WFC direction that permits every tile pair.** Gravity's east rule is always true, so
   the current WFC helper authors 249,500 no-op horizontal scopes, which compile to 499,000 directed arcs.
   `Grid.constrain` already owns WFC compatibility and can omit that direction after checking its finite
   tile table. In WFC, universal compatibility means there is no adjacency constraint. Keep this decision
   local to the WFC file; generic core relations must not be silently discarded because their current
   bottom-propagation behavior is observable.
2. **Restore a cached settled baseline instead of recomputing it.** A repeated `Generate` currently clears
   generated choices, refills every cell, enqueues every arc, and reaches the same authored closure again.
   If profiling confirms that cost, retain a private snapshot of the quiescent state and supports that
   existed before generated choices. Assertions and retractions must refresh or invalidate it, restoration
   must notify observers for cells that actually changed, and the existing live edit semantics remain the
   correctness oracle.
3. **Remove allocation and needless unions from the binary arc loop.** `fireArc` currently allocates a new
   `uint64[]` for every firing. Reuse private scratch storage, or use a local one-word scalar hot path while
   retaining the single runtime-width engine. Precompute which support rows equal the full target domain;
   if an active source value already contributes that row, the union is complete and the firing can return
   without scanning the remaining rows. Continue to handle an empty source exactly as today.
4. **Tighten the MRV picker only if it remains hot.** The present `PriorityQueue` inserts every unresolved
   cell individually and permits stale entries. First use linear heap construction if the runtime API makes
   that direct. If profiling still justifies more machinery, use an indexed heap or another compact picker
   that preserves the exact `(candidate count, cell id)` order. Do not change deterministic tie-breaking or
   consume random values in a different order merely to improve this row.
5. **Make solved-value extraction direct.** Materialization currently asks for a candidate list for every
   solved cell and then takes its head. A private singleton-index/value accessor can remove those scans and
   lists while still returning the same immutable `Map`. Measure the remaining `Map` construction cost
   separately; changing the public result to an array is outside this optimization pass.
6. **Revisit arc/watch layout only with evidence.** Flattened watch ranges or denser arc storage may reduce
   pointer chasing across a million arcs, but they come after the simpler wins above and only if the phase
   counters still identify layout as material. Do not introduce an AC-4-style counter system for this
   two-value case without a broader workload showing that its extra state pays for itself.

### Acceptance and stop conditions

Run the finite canary, cyclic WFC ladder, matching gravity, and matching 3-color rows together in the same
order and power plan after each accepted stage. Report same-run ratios and allocations; absolute late-run
milliseconds remain environmental observations on this machine. Every stage must pass the full Friendly
suite, multiword-domain and n-ary relation cases, generation replay, observation, assertion/retraction,
independent WFC adjacency validation, and the locked gravity fingerprint. The preserved historical WFC
file remains untouched.

An improvement is worthwhile when gravity falls relative to the same-run controls without surrendering
the generalized route's large 3-color advantage or complicating the public surface. Stop when the remaining
gap is dominated by the deliberately different immutable `Map` contract or by specialization unique to the
historical grid engine. At that point, record the boundary honestly rather than moving WFC machinery into
core or making Friendly expose the pipes.

## 15. Gravity Optimization Execution Plan (2026-07-11)

This is the implementation plan for the accepted proposal above, including Fable's Tier 0 review notes.
It records work order, proof obligations, review gates, and stop conditions. It does not record any of the
steps as implemented.

### 1. Scope and invariants

The pass may change `propagator-surface-vocab.fsx`, `propagator-friendly.tests.fsx`, and
`propagator-wfc-app.fsx`. Friendly should not need a mechanical change: its existing operations continue
to delegate to one optimized network. `propagator-wfc.fsx` remains byte-identical.

The following invariants are fixed for the whole pass:

- No WFC noun or grid-specific representation enters core.
- No propagator mechanics or backend vocabulary enters Friendly.
- The optimized finite engine remains one runtime-width implementation. A one-word branch is a local hot
  path inside that implementation, not a second backend.
- `Generate` keeps stable SplitMix draws, bounded restart, MRV order `(candidate count, cell id)`, and the
  public immutable `Solution Map`.
- Generic universal relations remain real core relations because their bottom transmission is observable.
- Candidate 1's WFC-local model change must preserve the locked gravity fingerprint exactly, not merely
  produce another valid map.
- Snapshot restoration includes supports as well as candidate words. It never becomes a second mutable
  model or a second propagation authority.
- Performance decisions use fixed-order, same-run relative comparisons. Absolute times on this machine
  remain environmental observations.

### 2. Phase 0 - lock behavior and collect a cost profile

#### 2.1 Stable gravity fingerprint

Add a private oracle in `propagator-wfc-app.fsx` that enumerates a solution in row-major grid order and
encodes each tile with an explicit stable byte. Hash those bytes with SHA-256; do not use F# `hash` or a
process-dependent comparer. Dimensions and tile count must be included in the canonical input.

Before changing `Grid.constrain`, run the current 500x500 gravity model at seed 42, record the digest, and
make the matching benchmark print it outside the timed region. Add a smaller default-running oracle that
compares complete row-major tile arrays rather than hashes.

The fingerprint is a search-tree lock. Every later candidate that claims to preserve generation order must
produce exactly the same digest. Candidate 1 is included even though it changes the authored model.

#### 2.2 Temporary private instrumentation

Instrument the current implementation before optimizing it. Keep the instrumentation private to
`propagator-surface-vocab.fsx`, activate it only for the profiling run, and remove it after the final useful
measurements. Do not route diagnostics through `Network`, `CoreOperations`, or any Friendly operation.

Collect at least these phase durations and counts:

- Engine construction: cells, compiled relation tables, directed arcs, watch entries, and elapsed time.
- Generated reset: refill/rebuild time, enqueued work, dequeued work, and changed-cell notifications.
- Propagation: arc firings, n-ary firings, target changes, support-row unions, full-domain results, and
  temporary arrays allocated by `fireArc`.
- Picker: initialization time, initial entries, change-driven entries, dequeues, stale dequeues, guesses,
  contradictions, and restarts.
- Output: singleton extraction time and immutable `Map` construction time, measured separately.

Counters must not alter cell selection, queue order, random draws, observer delivery, or the timed validity
boundary. Run the profiler once for gravity and once for 3-color so an apparent gravity win cannot hide a
general regression.

#### 2.3 Baseline verification sequence

Use this fixed sequence before the first candidate and after every retained candidate:

```powershell
dotnet fsi .\propagator-friendly.tests.fsx
dotnet fsi .\propagator-wfc-app.fsx
dotnet fsi .\propagator-friendly.tests.fsx --benchmark
$env:TRIALS='2'
dotnet fsi .\propagator-wfc-app.fsx --benchmark --benchmark-large --benchmark-like-for-like
```

Record the active power plan, runtime, command order, finite Optimized/General ratio, cyclic ladder,
gravity/3-color ratio, validity, fingerprint, and allocation profile. Do not compare a late absolute sample
directly with an earlier first sample; compare each candidate to the controls collected alongside it.

**Phase 0 gate:** no optimization begins until the seed-42 digest and the first phase breakdown have been
recorded.

### 3. Candidate 0 - make optimized cell lookup constant-time

Fable identified `cellPosition` as the same accidental-linear-scan family as the earlier relation-endpoint
bug. It is outside the current gravity stopwatch, but reading every cell through Friendly `Value` is
quadratic today and should be fixed while this file is open.

In `Optimized.finiteNet`:

1. Replace the `cellIds` set and `Seq.tryFindIndex` lookup with one `Dictionary<int,int>` named for cell
   positions.
2. Populate it from `model.cells` with `Dictionary.Add`, preserving duplicate-id failure rather than
   silently overwriting a position.
3. In `addCell`, capture `cells.Count` as the new position, append the cell, and add its id and position to
   the dictionary.
4. Implement `cellPosition` with `TryGetValue` and preserve the current outside-network argument error.
5. Use `ContainsKey` on the same dictionary for relation endpoint membership. Do not retain a redundant
   `HashSet`.

Add a regression that reads initial model cells and cells added through the live optimized facade, then
authors a relation over the added cells. Add an opt-in large read sweep that reads each cell once and reports
aggregate time without asserting a machine-specific threshold.

**Candidate 0 gate:** all existing tests and fingerprints are unchanged, and the read sweep scales with the
number of reads rather than cells multiplied by reads.

### 4. Candidate 1 - omit universal WFC directions before authoring scopes

This change belongs only in `propagator-wfc-app.fsx` because universal directional compatibility has a
specific WFC meaning: that direction contributes no adjacency constraint.

In the private WFC application model:

1. Retain the validated authored tiles in the private `Grid` record, preferably as a compact array.
2. Add a private `isUniversal direction compatibility grid` helper that checks every ordered tile pair.
3. Perform that check before constructing a direction's scope collection. If it is universal, construct no
   scopes and make no `RelateMany` call for that direction.
4. Keep non-universal east and south authoring exactly as today, including scope order.
5. Do not add universal-table elimination to `Constraint`, `RelationBox`, `FiniteCore`, or Friendly.

Add two independent proofs:

- A test-local explicit route that still authors the universal east relation and an elided route through
  `Grid.constrain`; compare their complete row-major seed-42 outputs.
- The 500x500 matching gravity benchmark must retain the Phase 0 digest, random draw count, restart count,
  and independently validated map.

The expected structural observation is 249,500 fewer horizontal scopes and 499,000 fewer directed arcs.
Treat that as a measured consequence, not as a substitute for the fingerprint proof.

**Candidate 1 review gate:** stop if the digest, draw sequence, restart count, or map validity changes. Do
not explain away a difference merely because both maps satisfy gravity.

### 5. Candidate 2 - restore a cached settled baseline

This is the riskiest candidate. It is allowed to cache only the quiescent value/support arrays of the same
engine. It must not duplicate constraints, assertions, queues, generated choices, or propagation logic.

#### 5.1 Snapshot state

Add private baseline candidate-word and support arrays sized exactly like `state` and `supports`, plus one
validity flag. A valid baseline means: all current authored assertions have propagated, no generated choice
is represented, and the queue is empty.

Use these transitions:

| Event | Required baseline action |
|---|---|
| Engine reaches its initial quiescent state | baseline may be captured lazily before first generation |
| Fresh assertion | invalidate before mutation |
| Replacement under an existing premise | invalidate before the existing rebuild path |
| Retraction | invalidate before rebuild |
| Generated collapse | retain the previous valid baseline unchanged |
| Reset with generated choices and valid baseline | clear generated registry and restore baseline arrays |
| Reset with generated choices and invalid baseline | clear generated registry, rebuild without generated choices, then capture |
| Reset with no generated choices and invalid baseline | capture the already-quiescent current authored state |

Search premises used by `solve` continue to use the existing assertion/retraction paths and therefore
invalidate the cache. `clearSearch` must complete before `Generate` captures or restores a baseline.

#### 5.2 Restore operation

Restoration must clear queue and queued-work flags, compare the current state with the baseline cell by
cell, copy candidate words and supports, and notify observers only for cells whose visible candidate state
changed. A support-only change follows the current notification semantics; do not invent a new event kind.
The restored snapshot is already quiescent, so it must not enqueue every relation again.

Keep the existing full rebuild as the oracle and fallback. Do not optimize assertion/retraction rebuilding
inside this candidate.

#### 5.3 Required stale-cache tests

Add Friendly-level tests for all three sequences below. After the second `Generate`, compare the solution,
every cell value, and every support set against a freshly authored equivalent network.

1. `Generate -> Assume a new premise -> Generate`.
2. `Generate -> Retract an existing premise -> Generate`.
3. `Generate -> re-Assert a different value under the same premise -> Generate`.

Also retain an observer across both generations and prove its final replay matches the second result. Include
one failed-attempt/restart case so baseline restoration after contradiction cannot retain failed choices.

**Candidate 2 review gate:** review the state transitions and dedicated tests independently before any arc
or picker optimization. If a fresh twin and restored network differ in values, support, events, or output,
remove the cache and keep the existing rebuild.

### 6. Candidate 3 - tighten binary arc firing

Apply the smallest hot-loop change justified by the Phase 0 profile.

#### 6.1 One-word scalar path

For `wordCount = 1`, accumulate allowed targets in a local `uint64` and meet the target without allocating
an array. This is a branch inside the existing engine, not a separate one-word store or lowering.

Precompute whether each forward and reverse support row equals the complete target domain. While scanning
active source values, return immediately when a full row is found because the meet would be a no-op and
would not add support today. An empty source must still produce an empty allowed mask and propagate bottom.

#### 6.2 Multiword scratch and observer re-entry

Do not introduce one engine-global shared scratch array. Gravity uses the one-word path, so leave the
multiword allocation path unchanged unless post-candidate profiling still identifies it as material.

If multiword reuse is justified, first make observer re-entry safe and explicit. Prefer scratch owned by
each `quiesce` invocation, so a handler that causes nested quiescence receives a different buffer. Add a
targeted re-entry regression. If the code cannot make that ownership obvious, stop and add a cheap
in-propagation mutation guard with a clear exception before sharing storage.

Run the asymmetric 65-value relation and 65/128/130-value domain cases, plus a relation where one row is
full and a separate empty-source case. Verify values, supports, forward/reverse behavior, and observer
replay.

**Candidate 3 gate:** the gravity fingerprint and all multiword results remain exact; allocation falls in
the measured binary path; no observer contract is made subtly less safe.

### 7. Candidate 4 - reduce MRV maintenance only if still hot

First replace one-by-one initial queue insertion with the runtime `PriorityQueue` bulk constructor or
equivalent linear heap construction. Preserve the exact priority `struct(candidateCount, cellId)` and the
same element set.

Reprofile before writing a custom picker. If stale dequeues and change-driven insertions remain material,
an indexed binary heap is the maximum justified replacement for this pass. It must maintain one position
per unresolved cell, support remove/decrease operations, build bottom-up, and compare first by candidate
count and then by cell id. It must not add entropy, random tie-breaking, or a WFC-specific heuristic.

Compare selected-cell sequence and random draw sequence with the Phase 0 trace on small fixtures before
running the 500x500 fingerprint. Do not retain a custom heap if bulk construction removes the measured
bottleneck.

**Candidate 4 gate:** identical selected cells, draws, restarts, fingerprint, and General/Optimized seeded
agreement.

### 8. Candidate 5 - extract solved values without candidate lists

Add a private `SingletonValue` operation to `FiniteCore.Engine`. Locate the sole set bit directly with
`BitOperations`, account for its word offset, and return the corresponding authored-domain value. Fail
loudly if called for zero or multiple candidates; do not silently choose the first value.

Use it only after solve/generate has established that every cell is singleton. Build the same immutable
`Map<Cell<'a>,'a>` in authored cell order. Keep extraction and `Map` construction separately timed so the
remaining cost is attributable.

Do not add an array result, lazy result, alternate Friendly method, or application-visible optimized
projection in this pass.

**Candidate 5 gate:** exact solutions and fingerprints, with lower extraction allocation. If immutable
`Map` construction dominates afterward, record it as the current contract boundary.

### 9. Candidate 6 - layout work is conditional, not scheduled by default

Flatten watches or alter arc layout only if the final profile still shows pointer chasing or watch traversal
as a material fraction after candidates 1-5. Any such change needs its own before/after counters and full
relation suite. Do not introduce AC-4 support counters, grid-neighbor storage, or a second compiled engine
for the two-value gravity case without broader evidence from gravity, 3-color, cyclic, and multiword loads.

The default outcome of this phase is no code change.

### 10. Final verification and recording

Run the fixed current suite once more under the recorded power plan. Then run the preserved historical
suite as the final comparator:

```powershell
$env:TRIALS='1'
dotnet fsi .\propagator-wfc.fsx
```

Verify the historical file's SHA-256 remains
`836A49E4E511E23BE0A05A598403EBFE2E9512B1C1F82583B1FA8498FD357621` and its git diff is empty.

The final report must include:

- Before/after phase breakdown and allocation counts.
- Finite General/Optimized same-run ratio.
- Cyclic ladder, matching gravity, and matching 3-color best/mean rows in fixed order.
- Gravity and 3-color relative reversal against the preserved implementation, with the existing contract
  caveats retained.
- Seed-42 fingerprint and independent map validity.
- Which candidates were retained, rejected, or skipped for lack of evidence.
- Residual time attributed to propagation, picker, extraction, and immutable `Map` construction.

Append benchmark results to `benchmarks.md`, then append dated implementation summaries here and to the
surface-design history. Do not rewrite this proposal, Fable's review, or earlier benchmark rows.

The pass is complete only when correctness and deterministic behavior remain locked, the measured gravity
cost is reduced without sacrificing the generalized 3-color result, and no public or architectural
complexity was added merely to chase the historical specialized engine.

-- Codex, 2026-07-11

### Placement history

- **2026-07-11 - Codex (document-role correction).** At the user's direction, relocated the accepted
  gravity proposal and detailed execution work order from `benchmarks.md` into this plan. The benchmark
  document now retains only measurements, interpretation, and comparison caveats. Fable's earlier references
  to the proposal's original location remain unchanged as historical record. No source implementation was
  performed by this relocation.

## 16. Gravity Optimization Implementation History

### 16.1 Objective, controls, and measurement limits

The pass began with one performance objective: reduce matching 500x500 gravity generation without making
3-color, the cyclic WFC ladder, the established General/Optimized Sudoku benchmark, or the General live-edit
row worse, and without changing deterministic output or propagator semantics. The contract locks were:

- gravity SHA-256 `632233C0779601913FB945173CCFDD1861FA5769185DD4114BE0FA4056D7A8B2`;
- 3-color SHA-256 `7A20D499A9E3BFC64249653EDC76CA6F7C0BE778A8F370A455910E1CBE5945FF`;
- gravity's 1,029 SplitMix draws and zero restarts;
- exact 8x6 row-major output, authored order, immutable public `Map`, supports, observer replay, bottom,
  retraction/recomputation, generic universal-relation behavior, and 65/128/130-value operation;
- historical WFC SHA-256 `836A49E4E511E23BE0A05A598403EBFE2E9512B1C1F82583B1FA8498FD357621`.

The machine remained on the Power saver plan under .NET 9.0.12. Its sustained timings degraded markedly
through the session, so each full checkpoint kept benchmark order fixed and interpreted gravity beside
3-color, the cyclic ladder, and the Sudoku/live-edit controls from that process. These are sequential
candidate checkpoints, not final-tree ablations: the final tree was not rebuilt and rerun with each retained
candidate independently disabled. Mechanical counts and allocations therefore provide the strongest
candidate attribution; absolute cross-process milliseconds are retained as observations rather than treated
as controlled deltas.

The timed current-surface finite rows were the established portable binary-relation 4x4 Sudoku with eight
givens through General and Optimized, plus the retained General two-assert/two-retract live-edit cycle.
Friendly, sparse, and generic-representation Sudokus and the scalar, interval, affine, barometer, provenance,
observer, and guard fixtures ran as correctness gates; they have no individual timing precedent and were not
invented as new benchmarks here. Older Part-1 and mutable-core Sudoku timings belong to separate unchanged
historical implementations and were not rerun.

### 16.2 Baseline and diagnosis

A canonical oracle encoded little-endian width, height, tile count, and explicit row-major tile bytes before
SHA-256. The literal 8x6 oracle and both 500x500 fingerprints passed before mechanics changed. Temporary
private diagnostics were enabled without adding any Friendly operation. Baseline gravity contained 250,000
cells, two compiled relation tables, 998,000 directed arcs, and 998,000 watch entries. Initial generation
fired 998,000 arcs and allocated 998,000 one-word `fireArc` arrays. Repeat generation performed another full
998,000-arc reset/rebuild before search. Output construction also proved material: candidate-list extraction
and immutable `Map` construction allocated about 298.5 MB in the instrumented run.

The fixed-order Phase 0 process measured General Sudoku at 33,726.2 / 39,027.3 us, Optimized Sudoku at
1,369.2 / 2,439.1 us, and General live edit at 36.5 / 48.6 us (best / mean), a 24.63x best-time Sudoku
ratio. Cyclic 500x500 took 12,011.427 ms; matching gravity took 4,544.909 / 4,929.444 ms and matching
3-color 6,203.318 / 6,526.135 ms. All correctness gates passed.

### 16.3 Choices and outcomes

| candidate | implementation and evidence | measured checkpoint | decision |
|---|---|---|---|
| 0 - cell lookup | Replaced `Seq.tryFindIndex` plus the redundant id set with one incrementally maintained `Dictionary<int,int>`. Initial cells, added cells, and relations over added cells passed. A 100,000-cell read sweep took 101.539 ms. | One-trial matching check: gravity 4,704.256 ms; 3-color 5,427.033 ms; both hashes exact. Generation was not the target of this candidate. | Retained. Direct removal of accidental quadratic aggregate reads. |
| 1 - universal WFC direction | `Grid` retained its validated tile array and checked all ordered pairs before constructing scopes. Gravity's universal east direction was omitted only in the WFC file. Generic core universal relations remained real because empty-source bottom transmission is observable. Tables fell 2 -> 1; arcs and watches fell 998,000 -> 499,000. A test-local explicit-east route and the elided route produced identical complete maps; draws stayed 1,029 and restarts zero. | Full checkpoint: cyclic 500x500 12,463.005 ms; gravity 4,078.417 / 5,093.959 ms; 3-color 7,878.721 / 8,062.427 ms. Finite ratio 51.56x. Severe late-run spread, including 128x128 cyclic at 2,789.524 ms, prevented attribution from milliseconds alone; structural counts and fingerprint were decisive. | Retained. WFC-local authoring simplification with exact behavior. |
| 2 - settled baseline | Added one lazy snapshot of the existing engine's quiescent candidate words and supports. Assert, same-premise replacement, and retract invalidate before mutation. Generated reset restores the snapshot, clears queue flags, and notifies only visibly changed cells; it does not cache constraints, assertions, queues, or a second model. Fresh-twin tests covered assertion, retraction, and replacement between two `Generate` calls; a retained observer and odd-cycle failed-attempt/restart case passed. | Comparable gravity profile reset fell 266.907 -> 21.648 ms initially and measured 29.172 ms at the next combined checkpoint; reset allocation became zero and 499,000 reset arcs disappeared. Full checkpoint: cyclic 500x500 8,099.838 ms; gravity 4,078.752 / 4,141.916 ms; 3-color 6,044.318 / 7,898.973 ms. Finite ratio 46.15x. | Retained after the dedicated cache review gate. |
| 3 - binary arc loop | Added a scalar `uint64` branch inside the same runtime-width engine and precomputed full-row flags. No sibling backend or shared scratch was introduced, so observer re-entry gained no scratch-corruption hazard. Empty sources still transmit bottom. Asymmetric 65-value and 65/128/130-value cases, full-row behavior, supports, reverse propagation, and observers passed. | One-word `fireArc` arrays fell from 998,000 per repeat after Candidate 1 (1,996,000 in the original repeat profile) to zero. Full checkpoint: cyclic 500x500 3,842.648 ms; gravity 1,057.193 / 1,196.598 ms; 3-color 1,228.796 / 1,311.783 ms. Finite ratio 43.29x. | Retained. Largest demonstrated shared-path win; gravity and 3-color both improved. |
| 4 - picker | Profile showed gravity picker initialization near 15 ms, 4,711 stale dequeues, and only 1,029 guesses. Initial priorities are already inserted in increasing cell-id order, so bulk construction offered little expected gain; an indexed heap would add substantial state and proof burden. | No implementation or timing delta. This was intentionally stopped before code. | Skipped for lack of evidence. |
| 5 - singleton extraction | Added private `SingletonValue`, using `BitOperations` and failing on zero or multiple candidates. Generation still returns the same immutable `Map` in authored cell order. | Instrumented gravity extraction fell 260.486 -> 57.601 ms and output allocation about 298.5 -> 270.5 MB. The late final full process measured cyclic 500x500 12,087.915 ms; gravity 3,569.262 / 4,445.822 ms; 3-color 3,941.998 / 4,584.026 ms; finite ratio 50.80x. All controls slowed together after the long session, so the extraction counters, allocation delta, and exact outputs are the candidate evidence. | Retained. Removes lists without changing the result contract. |
| 6 - layout | After scalar arcs, measured propagation no longer dominated; immutable `Map` construction was the principal residual. Flattening watches, adding support counters, or creating grid storage would complicate the core without evidence. | No implementation or timing delta. | Skipped. The immutable `Map` is the stopping boundary for this pass. |

### 16.4 Failures and corrections

The work encountered three concrete failures. The new baseline-refresh regression initially failed to compile
because F# could not infer the Friendly network type at `net.Value`/`net.Support`; adding the explicit
`Network<int,int,Set<int>>` annotation fixed the test, after which all cache sequences passed. The full-row
precomputation initially failed to compile because the support-row parameter type was indeterminate; an
explicit `uint64[]` annotation fixed it, after which all one-word and multiword tests passed. Neither was a
runtime semantic failure.

The first final historical run hit the five-minute command timeout after 301.9 seconds. It had already passed
all correctness checks and completed propagation/retraction timing, but had not reached full-map generation,
so it was recorded as incomplete rather than treated as a failure or quoted selectively. A second run with a
larger timeout completed in 444.6 seconds. It passed every correctness row and measured reset W=8 9.780 ms,
ramp512 corner 5,722.003 ms, ramp128 center 137.628 ms, reset W=1 2.463 ms, gravity column 2.635 ms,
gravity-small cone/replay 1.961/1.334 ms, ramp512-large cone/replay 7,658.648/5,393.498 ms, gravity
generation 292.197 ms, 3-color generation 55,390.970 ms, and ramp32 generation 161.740 ms. The historical
hash and empty git diff were rechecked afterward.

Temporary profiling itself imposed substantial overhead, especially the per-union counters, and one paired
profile took 96 seconds. Its wall times were never used as production benchmark claims. Once the useful
operation and allocation evidence had been collected, all profiling fields, environment switches, counter
branches, and profile-only WFC mode were removed. The full correctness and benchmark suites passed again on
the production path.

### 16.5 Direct comparison with preceding records

The applicable preceding values are repeated here so the outcome can be read without consulting older
benchmark sections.

| established current-surface row | immediate pre-session best / mean | final best / mean | result |
|---|---:|---:|---|
| General binary-relation 4x4 Sudoku | 32,651.1 / 36,149.4 us | 31,483.6 / 195,034.4 us | Best improved 3.6%; final mean is a late-session outlier on unchanged General code. |
| Optimized identical Sudoku | 1,001.6 / 2,506.6 us | 619.8 / 1,491.8 us | Best improved 38.1%; mean improved 40.5%. |
| General two-assert/two-retract live edit | 27.9 / 38.4 us | 32.1 / 43.8 us | 15.1% slower by best, within observed spread and faster than this session's Phase 0 row. |

| matching WFC workload | prior historical best | completed historical rerun | immediate pre-session Friendly best | strongest retained checkpoint | final late Friendly | direct comparison |
|---|---:|---:|---:|---:|---:|---|
| gravity 500x500 | 363.597 ms | 292.197 ms | 5,959.679 ms | 1,057.193 ms | 3,569.262 ms | Historical remains 3.62x faster than the strongest checkpoint and 12.21x faster than the final late sample; Friendly improved 5.64x at its strongest checkpoint versus its immediate prior record. |
| 3-color 500x500 | 54,743.329 ms | 55,390.970 ms | 4,958.214 ms | 1,228.796 ms | 3,941.998 ms | Friendly is 45.08x faster than the historical rerun at its strongest checkpoint and 14.05x faster in the final late sample; strongest Friendly improved 4.03x versus its immediate prior record. |

There is no historical cyclic workload, so cyclic 500x500 is compared only with its own Phase 0 value:
12,011.427 ms -> 3,842.648 ms at the strongest checkpoint, followed by the degraded 12,087.915 ms final
sample. Historical ramp timings were rerun and preserved, but no equivalent ramp workload was timed through
the changed Friendly/optimized core; the generic multiword correctness tests do not fill that performance
gap.

### 16.6 Final choice

Retain Candidates 0, 1, 2, 3, and 5. They are direct, orthogonal reductions in demonstrated work and preserve
the exact outputs and general propagator laws. Leave Candidates 4 and 6 unimplemented. Do not change the
public immutable `Map`, add WFC vocabulary to core, introduce a second finite engine, or expose mechanics
through Friendly merely to approach the historical gravity-specialized `int[]` path.

The strongest clean checkpoint reduced matching gravity from the Phase 0 observation of 4,544.909 ms best
to 1,057.193 ms while matching 3-color improved from 6,203.318 to 1,228.796 ms and cyclic 500x500 from
12,011.427 to 3,842.648 ms. The much later final process was slower in every WFC control but still retained
exact hashes and a 50.80x Optimized/General Sudoku ratio. This is why the final judgment rests on the full
checkpoint series plus removed arc/reset/allocation counts, not on selecting either the fastest or latest
absolute sample. Detailed timing tables remain in `benchmarks.md`.

-- Codex, 2026-07-11

## 17. Ramp Measurement and Multiword Rebuild History

### 17.1 Scope and authoring boundary

This pass closed the explicit gap left by section 16: run the historical ramp shapes through the changed
Friendly/Optimized core without placing grid, tile, ramp, or WFC policy in the library. The app-local grid was
made generic over its tile type and gained only WFC-facing conveniences for cell lookup, named assumptions,
retraction, value, and support. Integer ramp models then use the same `Grid.create`, `Grid.constrain`,
`Grid.assume`, `Grid.retract`, and `Grid.generate` vocabulary as the earlier tile examples. Friendly itself
and its one-authority delegation record did not change.

The correctness lock reproduces the historical small models: one pin across a 100-value domain, the 64-bit
word seam, two pins forcing every value, exact OR-on-narrow support names, assert-site bottom, and retraction
against a fresh surviving-premise twin. The large timing gate checks the same 500x500 ramp512 corner and
ramp128 center counts, the opposite-corner ramp512 edit, and a valid 64x64 ramp32 generated map. The current
ramp32 result fingerprint is
`3AD38489B2BB9D6165F761FAF7881C838EF338286F031B60C6419A6555C2FDAD`.

### 17.2 Diagnosis and retained changes

The first current-core run measured ramp512 corner at 19,614.021 ms, ramp128 center at 2,563.402 ms,
ramp512 retraction at 23,636.274 ms, and ramp32 generation at 186.608 ms. The large propagation gap exposed
the conditional multiword work deferred by Candidate 3, but its dominant cause was broader than scratch
allocation.

1. Multiword `fireArc` allocated a fresh accumulator for every arc. It now allocates one scratch array per
   `quiesce` invocation and clears it between firings. Nested observer-triggered propagation owns a separate
   invocation and therefore a separate buffer. A 65-value observer re-entry regression performs a nested
   assertion and checks final values plus OR-on-narrow support.
2. Multiword arcs tested all authored values. They now enumerate set bits word by word with
   `TrailingZeroCount`, retaining the same flattened forward/reverse tables and full-row shortcut.
3. Rebuild was the dominant issue. Replacing an assertion or retracting a premise reset every cell to top,
   enqueued every arc, and recomputed the whole structural fixpoint. The engine now snapshots its one
   constraint-only quiescent state at construction. Rebuild restores that immutable derived state, clears
   support, applies the authoritative assertion registry, and propagates only from cells actually narrowed.
   This is not a second mutable model: constraints and assertions remain the only authority, and the snapshot
   contains only their static no-assertion fixpoint. Relations that remove unsupported values from top remain
   correct because the restored state is the computed structural fixpoint, not an assumed all-top state.

### 17.3 Sequential checkpoints and controls

The one-trial checkpoints were sequential and ran under the repository's known machine drift. They are not
strict independent final-tree ablations.

| checkpoint | ramp512 corner | ramp128 center | ramp512 retract | ramp32 generation |
|---|---:|---:|---:|---:|
| initial | 19,614.021 ms | 2,563.402 ms | 23,636.274 ms | 186.608 ms |
| scratch | 17,427.153 ms | 2,898.273 ms | 22,929.159 ms | 238.842 ms |
| set-bit iteration | 19,794.289 ms | 1,738.741 ms | 24,938.788 ms | 407.618 ms |
| structural baseline | 6,256.278 ms | 156.072 ms | 6,010.373 ms | 332.095 ms |
| final fixed-order process | 6,469.697 ms | 182.009 ms | 6,822.673 ms | 305.038 ms |

The structural checkpoint reduced ramp512 3.16x and ramp128 11.14x relative to the immediately preceding
checkpoint. Against the completed historical rerun, the final Friendly rows are 1.13x slower on ramp512 and
1.32x slower on ramp128. Current retraction is 1.12x faster than the historical cone-local row but 1.27x
slower than historical full replay; the mechanisms differ, so replay is the closer comparison. Ramp32
generation is 1.89x slower, with the existing result/RNG/search-policy caveat.

The final fixed-order WFC process also measured cyclic 500x500 at 4,167.414 ms, gravity at 1,338.347 ms,
and 3-color at 1,324.329 ms. Both established 500x500 fingerprints remained exact. The adjacent finite run
measured General/Optimized Sudoku at 38,748.9/994.7 us best and General live edit at 31.9 us best, a 38.95x
same-run Sudoku ratio. All scalar, interval, provenance, sparse-search, 65/128/130-value, asymmetric relation,
full-row, bottom, baseline-refresh, restart, observer, and retraction regressions passed.

### 17.4 Corrections and decision

The new nested-observer test initially expected the nested premise to appear on an already-singleton middle
cell. That contradicted the established OR-on-narrow law: no second narrowing means no second support join.
The oracle was corrected to `{outer}` for the middle and `{nested}` for the independently narrowed right
cell. No implementation behavior changed to satisfy the test, and no runtime semantic gate failed.

Retain invocation-local multiword scratch, set-bit source iteration, and the immutable structural rebuild
baseline. They are general finite-propagator machinery, reduce demonstrable work, preserve synchronous
observation and Sussman-style propagation laws, and add no public vocabulary. Do not import the historical
grid-shaped queue, cone-local WFC retraction, or AC-4 support counters into core on this evidence. The current
generic implementation is within 13-32 percent of the specialized historical propagation rows while keeping
one coherent arbitrary-relation engine.

-- Codex, 2026-07-11
