# Constraint Engine — Design Journal

A running engineering journal for the **next** constraint engine: one semantic model
(lattice + meet + monotone propagators + retraction) that can be instantiated either
**fast** (Sudoku, WFC) or **clear** (teaching, debugging), without forking the engine.

- **Companion (analysis of the past):** [constraint-cell-genealogy.md](constraint-cell-genealogy.md) — the genealogy of the two existing engines (`constraintprop.fsx`, `constraintprop2.fsx`) and why they split.
- **Source under study:** [constraintprop.fsx](../constraintprop.fsx) (Gen-1 propagator, `string` errors) and [constraintprop2.fsx](../constraintprop2.fsx) (fold `Cell` @255, `Cell2` @418, `CellError<'T>`). Both are failed prototypes; their post-mortem is in [constraint-cell-genealogy.md](constraint-cell-genealogy.md).
- **Status:** design phase — nothing here is implemented yet. "Decided" below means *committed design direction*, not shipped code.

> **Convention.** The *Design snapshot* and *Decisions ledger* are living — edit them in place so they always reflect current thinking. The *Journal* is append-only: add a new dated entry at the bottom; never rewrite history above it.

---

## Design snapshot (living — last updated 2026-06-14)

**Thesis.** Constraint propagation is the propagator model specialized to a powerset lattice with intersection-merge. So there is *one semantic model*; the paradigms differ in the lattice and propagation protocol they instantiate. The genuinely hard axis is not paradigm but **monotonicity** — re-assignment (the C↔F demo) is non-monotone and needs a retraction layer that pure narrowing does not.

**Order convention.** Information is ordered by refinement: `x ≤ y` means "`x` contains at least as much information as `y`." For powerset domains this is subset order, `Top` is the universe, meet is intersection, and propagation descends from `Top` to the **greatest fixed point below the initial store**. This journal avoids saying "least fixed point" without naming the chosen order.

**Target shape.**

```
            ┌─────────────────────────────────────────────┐
            │  One semantic contract                      │
            │  cell = lattice element                     │
            │  merge = meet (⊓)                           │
            │  propagator = monotone contribution         │
            │  retraction layer (trail | premises/TMS)    │
            └───────────────┬─────────────────────────────┘
                            │ two execution backends
          ┌─────────────────┴───────────────────┐
          ▼                                       ▼
  Reactive backend                        Solver kernel
  (FSharp.Data.Adaptive)                  (hand-rolled)
  small · live · heterogeneous            large · batch · homogeneous
  re-assignable · C↔F                     monotone · search-heavy
  cvals/avals, demand-driven              bitset domains · worklist · trail
```

**Fast-vs-clear should be an instantiation choice, not a fork.** The semantic laws and solver algorithm should be representation-independent. The exact operational interface is deliberately not frozen yet: a fast mutable bitset may need ownership/copy operations and a narrowing operation that reports removed values, while a clear `Set<int>` implementation can remain persistent. Plug in bitsets → fast; plug in `Set<int>` → clear; prove both implementations agree through differential/property tests.

**Static genericity is the intended default, subject to measurement.** `inline`+SRTP can specialize aggressively; struct-constrained generics may also be near-zero-cost when the JIT devirtualizes them, but that is a benchmark question rather than a design guarantee. Object-interface dispatch remains the explicit runtime-switching option.

## Decisions ledger

| ID | Decision | Status | Since |
|----|----------|--------|-------|
| D1 | One semantic model and shared laws: lattice element + meet + monotone propagators + explicit retraction semantics. | Decided | 2026-06-14 |
| D2 | Two capability-specific surfaces over that model: Adaptive/reactive for small live networks, hand-rolled solver session for batch/search workloads. They need not expose identical operations. | Revised | 2026-06-14 |
| D3 | Domain representation is pluggable. Fast = bitset (`uint16`/`uint64[]`), clear = `Set<int>`; extract the exact operational interface only after Sudoku and a minimal WFC slice expose their needs. | Revised, tentative shape | 2026-06-14 |
| D4 | Search backtracking uses a **trail** (chronological undo log). A trail does not by itself support arbitrary live input replacement; that requires rollback to an owning checkpoint, reconstruction, or premises/TMS. | Revised | 2026-06-14 |
| D5 | Contradiction = empty domain (⊥ = `0` bitset), tested by compare. No rich error DU in the hot path. | Decided | 2026-06-14 |
| D6 | Prefer static genericity (`inline`/SRTP or struct-constrained generic); choose the mechanism by benchmark. Object-interface (`ILattice`) is reserved for runtime switching or where measured cost is irrelevant. | Revised, tentative mechanism | 2026-06-14 |
| D7 | FSharp.Data.Adaptive is **not** in the solver kernel — it is the outer reactive shell + the small live propagator regime. | Decided | 2026-06-14 |
| D8 | Incremental strengthening may **warm-start** from the current store. Removing or replacing an input widens the solution space and requires checkpoint rollback, affected-region reconstruction from `Top`, or premise tracking. | Revised | 2026-06-14 |
| D9 | Start with AC-3-style propagation for Sudoku and investigate AC-4-style support counters for WFC. They share semantics, but may require distinct operational propagator protocols. | Tentative | 2026-06-14 |

## Open questions

- **Operational domain interface.** Beyond `Top`, bottom/singleton tests, count, and enumeration, does the kernel need `NarrowInto` to report the removed-value delta? How are ownership, copying, and restoration represented for mutable multiword bitsets?
- **AC-3 vs AC-4 protocol.** Which semantic laws are shared, and which operational state must remain backend-specific? WFC support counters need initialization, tile-removal events, restoration, and possibly a different propagator shape.
- **Scope of the premise/TMS layer for v1.** Is the C↔F flat-scalar lattice + re-assignment in v1, or deferred until after Sudoku/WFC land on the kernel?
- **Adaptive backend's error channel.** The new kernel sidesteps the `CellError × shallowEquals` NRE entirely (⊥ = 0, no DU). But if we keep an Adaptive-backed live engine, does it still need a shallow-comparable error channel (value-type codes or `IEquatable`)? See genealogy doc's "other failure" section.
- **Heuristic maintenance.** Incrementally maintaining MRV (Sudoku) and a min-entropy heap (WFC) as domains shrink and on backtrack/restore.
- **Incremental widening strategy.** For changed/removed givens, when is rollback sufficient, when is affected-region reconstruction cheaper, and when are explicit premises justified?
- **Public capability surfaces.** What belongs to the shared semantic contract, `SolverSession`, and `ReactiveSession` respectively?
- **Do we keep Adaptive at all for the live regime,** or eventually replace it with a purpose-built cyclic propagator scheduler (Adaptive is DAG-only; cycles need the `AddCallback`+`transact` hack)?

---

## Collaboration culture

This journal is shared by three collaborators. Optimize for a clear current design **and** an honest record of how it changed.

### Editing rules

1. **Living sections are edited in place.** Keep the Design snapshot, Decisions ledger, and Open questions aligned with current group thinking.
2. **The Journal is append-only.** Record substantive arguments, discoveries, reversals, and experiments as new dated entries. Do not rewrite earlier entries to make history look cleaner.
3. **Do not silently reverse a decision.** Change its ledger status to `Revised` or `Superseded`, preserve the original date, and append an entry explaining why.
4. **Silent edits are for non-semantic cleanup only.** Typos, broken links, formatting, and wording that does not change meaning may be fixed without a journal entry.
5. **Attribute proposals; own accepted decisions together.** New entries include an author/signature. Once the group accepts a proposal, the living snapshot states it in the collective voice rather than treating it as one person's property.
6. **Separate confidence from commitment.** Use `Decided`, `Tentative`, `Revised`, or `Superseded` in the ledger. A strong argument is not automatically a settled decision.
7. **Record evidence beside performance claims.** Until measured, timings and allocation goals are acceptance targets or hypotheses, not facts.
8. **Prefer small reversible decisions.** Freeze semantic laws early; let interfaces and optimization mechanisms remain tentative until multiple workloads exercise them.

### Entry template

```md
### YYYY-MM-DD — Short title
**Author:** Name or handle
**Status:** Proposal | Accepted | Experiment | Supersedes Dn

What changed, why it changed, evidence or counterexample, and consequences.
```

---

## Journal

### 2026-06-14 — Founding entry: unify the paradigms, split the backends

Three design conversations crystallized into the snapshot above. Recording the reasoning so the *why* survives.

**1. The two paradigms are one engine over a lattice.**
Constraint propagation = the propagator model with a powerset lattice and intersection-merge. So "propagators vs. constraint propagation" is a false split — it's just the lattice instance:
- powerset bitset + intersection + arc-consistency operators → Sudoku / WFC;
- flat-scalar lattice (⊤ = unknown / value / ⊥ = conflict) + two directional transforms → C↔F.

Same three primitives (cell-as-lattice, merge-as-meet, monotone propagator), different lattice. The fold engine ([constraintprop2.fsx:255](../constraintprop2.fsx)) already has the *right idea* (a real meet, monotone narrowing) but is welded to one lattice (hard-wired equality-minimize) and folds its own seed, so it can't be re-parameterized to C↔F.

**2. The hard axis is monotonicity, not paradigm.**
Both pure paradigms are monotone (least fixpoint, never retract). The C↔F demo *as written* is **re-assignable** — `set C=0` then `set C=100` is `0 ⊓ 100 = ⊥` under any honest meet. That is non-monotone by construction, and it's the real reason the codebase has two engines:
- `constraintprop.fsx`'s `Cell` does C↔F by **not merging at all** (last-write-wins dataflow) — which is *why* it can re-assign and *why* it can't do Sudoku (it would clobber a domain instead of intersecting it).
- the fold `Cell` has a meet, so it narrows (Sudoku) but is monotone, so re-assignment is *supposed* to conflict.

Bridge: a **retraction layer**. Cheap version = a trail (D4). Expressive version = premises/TMS (set = retract+assert; Sudoku is narrowing within one worldview, C↔F is worldview-switching) — reserved for when we actually want conflict learning or live re-assignment.

**3. Efficiency is orthogonal to the unification — it's three representation swaps.**
The current Sudoku is the *clarity build*, and it shows exactly what to change: `Set<int>` domains ([constraintprop2.fsx:688](../constraintprop2.fsx), only 4×4), `Set.intersect` meet ([:715](../constraintprop2.fsx), [:849](../constraintprop2.fsx)), folded via `AList.fold` over Adaptive ([:345](../constraintprop2.fsx)), `CellError` DU on the error arm. To go fast:
1. **Domain → bitset; meet → `&&&`; ⊥ → `0`.** `uint16` (Sudoku) / `uint64[]` (WFC). Singleton = `popcount==1`, pick = `tzcnt`. Contradiction is the empty bitset, a compare — no allocation, no error DU (this is also the decision that dodges the `shallowEquals` NRE).
2. **Propagation → owned worklist (AC-3), not `AList.fold`/Adaptive.** Queue of dirty cells; pop, AND into neighbors, push the shrunk ones, stop at empty. Monotonicity guarantees the queue drains. The abstraction *is* the algorithm.
3. **WFC → AC-4 support counters + min-entropy heap.** O(1) amortized per tile removal instead of re-intersecting; AC-3's bitset-AND is already cheap enough for Sudoku.

Backtracking via trail (D4); MRV (Sudoku) / min-entropy (WFC) for cell selection.

**Expected:** hardest 9×9 well under 1 ms, allocation-free hot loop; WFC grids in tens of ms (contradictions/restarts are the variance). Same logic on Adaptive + `Set` is orders of magnitude slower — from per-node incremental machinery over tree-sets, not from the paradigm.

**4. Generic over the data structure without (mostly) selling speed.**
F# lets the genericity be resolved statically: be 100% generic in source, 100% specialized in emitted code. Ladder (D6):
- **`inline` + SRTP** → meet emitted as `&&&` inline, zero tax (cost: code bloat, viral `inline`).
- **struct-constrained generic** (`Engine<'D,'L when 'L :> ILattice<'D> and 'L : struct>`) → CLR monomorphizes over value types, JIT devirtualizes; near-zero tax, not viral, debuggable. **Likely the sweet spot.**
- **object `ILattice<'D>`** → runtime switching, but a callvirt per meet + boxing risk on struct `'D`. This is the *only* rung where you "sell a bit of speed," and it bites only the bitset instance (the `Set` instance is dominated by its own cost anyway).

The representation choice itself (`Set` ↔ bitset) is the dial — 10–100× plus GC pressure — and that's **not** a genericity tax; it's the point. You sell speed only for *dynamic* (runtime) switching.

**5. Adaptive is scoped, not abandoned (D7, D8).**
Adaptive's per-node overhead amortizes when nodes are few/coarse/heterogeneous and starves when they're many/fine/identical — exactly the regime boundary from the genealogy. Keep it for the reactive boundary (cvals for givens/tileset → one aval triggers a re-solve → avals drive UI) and the small live C↔F-style networks. Keep it *out* of the solver kernel: no native retraction, DAG-only (cycles need the `AddCallback`+`transact` hack), and the fold engine's `AList.fold`-simulated meet *is* the overhead. The kernel is a pure `givens -> result` wrapped in **one** Adaptive node. Cheap re-solve on edit comes from the kernel's warm-start (dynamic AC), not from per-cell `aval`s.

**Net:** the paradigm split, the representation split, and the substrate split are the *same* split. One API spans it; F# lets us put the seam at compile/JIT time so the abstraction is ~free unless we demand runtime switching.

**Next candidate steps (not started):** sketch the lattice interface (resolve OQ-1); prototype the bitset kernel + AC-3 worklist + trail on 9×9 Sudoku; then layer AC-4 + min-entropy for WFC; defer the premise/TMS layer.

### 2026-06-14 — Let workloads reveal the operational interface
**Author:** Codex
**Status:** Accepted revision to D2, D3, D4, D6, and D8; clarification of D9

The founding direction still stands: use one lattice-and-propagator semantic model, keep Adaptive outside the large solver kernel, use bitsets for the fast representation, and use a trail for search. This revision narrows several claims that were stronger than the mechanisms established so far.

**1. Share semantics without forcing identical APIs.**
The reactive and solver regimes should obey the same laws, but their useful capabilities differ. A solver session needs propagation, checkpoints, restoration, and search. A reactive session needs input replacement and live observation. Calling these "one API" risks either exposing operations a backend cannot honestly implement or hiding important costs. The shared artifact is therefore a semantic contract plus conformance tests; capability-specific surfaces may sit above it.

**2. Extract the domain interface after two workloads.**
`Top`, `Meet`, `IsBottom`, `IsSingleton`, and `Enumerate/Count` describe the mathematics, but may not be enough for an allocation-conscious implementation. A mutable `uint64[]` domain introduces ownership, copying, and restoration. AC-4-style WFC benefits from knowing exactly which values were removed. The likely hot operation is closer to:

```text
NarrowInto(target, contribution)
    -> Unchanged
     | Changed(removed-values)
     | Bottom
```

That shape is a hypothesis, not a new frozen interface. First implement a specialized `uint16` Sudoku path, then a minimal WFC slice with support bookkeeping, and only then extract the smallest protocol that serves both. A persistent `Set<int>` implementation should run against the same semantic tests and act as a clarity oracle.

**3. Distinguish narrowing from widening.**
A warm-start is valid when an edit only strengthens the store: add a given, remove domain values, or add a constraint. Removing or replacing a given can widen domains. A meet-only engine cannot discover those restored possibilities from its narrowed state. The choices are chronological rollback to a checkpoint that predates the premise, reconstruction of the affected region from `Top`, or explicit premise/TMS bookkeeping. The trail remains the v1 answer for search; it is not being claimed as a complete live-reassignment mechanism.

**4. Let AC-3 and AC-4 share laws, not accidental machinery.**
Both propagate monotone loss of possibilities to quiescence and detect bottom. AC-4 additionally owns support counters and consumes value-removal events; those counters must be initialized and restored. The implementations may therefore use different propagator protocols while still conforming to the same cell/domain semantics.

**5. Treat genericity and speed as measured engineering.**
SRTP can specialize operations aggressively. Struct-constrained generic interfaces may also perform very well, but devirtualization is a JIT behavior to verify, not promise. The implementation ladder remains useful; the final choice will be based on representative Sudoku and WFC benchmarks.

**6. Turn performance expectations into acceptance targets.**
"Hardest 9×9 under 1 ms," an allocation-free hot loop, and WFC in tens of milliseconds remain motivating targets. They are not design evidence until the benchmark corpus, hardware, runtime, and solver strength are specified. In particular, singleton-peer elimination is not a complete measure of a competitive Sudoku propagator.

**Revised implementation order:**

1. State the order, lattice laws, contradiction, quiescence, and retraction semantics.
2. Build a specialized `uint16` Sudoku kernel with explicit change events and a trail.
3. Add a `Set<int>` clarity implementation plus differential/property tests.
4. Build a minimal WFC constraint to expose support-state and removed-value requirements.
5. Extract and benchmark the generic domain/propagator interfaces.
6. Add the Adaptive outer shell; defer arbitrary C↔F re-assignment until its premise semantics are explicit.
