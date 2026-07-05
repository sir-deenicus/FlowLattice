# Constraint Engine — Design Journal

A running engineering journal for the **next** constraint engine: one semantic model
(lattice + meet + monotone propagators + retraction) that can be instantiated either
**fast** (Sudoku, WFC) or **clear** (teaching, debugging), without forking the engine.

- **Companion (analysis of the past):** [constraint-cell-genealogy.md](constraint-cell-genealogy.md) — the genealogy of the two existing engines (`constraintprop.fsx`, `constraintprop2.fsx`) and why they split.
- **Source under study:** [constraintprop.fsx](../constraintprop.fsx) (Gen-1 propagator, `string` errors) and [constraintprop2.fsx](../constraintprop2.fsx) (fold `Cell` @255, `Cell2` @418, `CellError<'T>`). Both are failed prototypes; their post-mortem is in [constraint-cell-genealogy.md](constraint-cell-genealogy.md).
- **Status:** design phase — nothing here is implemented yet. "Decided" below means *committed design direction*, not shipped code.

> **Convention.** The *Design snapshot* and *Decisions ledger* are living — edit them in place so they always reflect current thinking. The *Journal* is append-only: add a new dated entry at the bottom; never rewrite history above it.

---

## Design snapshot (living — last updated 2026-06-23)

**Thesis.** Constraint sat is not a second paradigm bolted onto propagators — it *is* the propagator model restricted to the monotone case (cell = lattice element, value = meet of current contributions; *never retract* ⟹ arc-consistency / domain narrowing, i.e. Sudoku). So there is *one semantic model*; "propagation vs constraint sat" is just the lattice instance plus whether retraction is in play. The genuinely hard axis is therefore **retraction**, and specifically **premise-based, dependency-directed (non-chronological) retraction** — the TMS lineage: retract a premise, propagate the lost support. That is what C↔F re-assignment needs, it is *more propagation* (not search), and it distinguishes a *propagator engine* from a pure monotone narrower. It is core to this project's identity, not an optional extra — drop it and what remains is *just constraint sat*. **Search (chronological backtracking / guess-and-undo) is not part of this engine at all** — it is a separate, fully modular layer that may *drive* the propagation core but sits outside the semantic model and the provenance (the CSP/SAT-solver lineage; see the 2026-06-22 entries).

**Order convention.** Information is ordered by refinement: `x ≤ y` means "`x` contains at least as much information as `y`." For powerset domains this is subset order, `Top` is the universe, meet is intersection, and propagation descends from `Top` to the **greatest fixed point below the initial store**. This journal avoids saying "least fixed point" without naming the chosen order.

**Target shape.**

```
  ┌──────────────────────────────────────────────────────────────┐
  │ Optional reactive observation façade — OUTSIDE the engine      │
  │ (FSharp.Data.Adaptive)                                         │
  │ incremental derived collections (aset / alist / amap)          │
  │ glitch-free views · UI binding;                                │
  │ adopted only when a reactive view / search index               │
  │ is needed — never the core's substrate                         │
  └───────────────────────────────┬──────────────────────────────┘
                                  │  observes (read-only)
  ┌───────────────────────────────┴──────────────────────────────┐
  │ One semantic contract = THE ENGINE                             │
  │ cell = lattice element · merge = meet (⊓)                      │
  │ propagator = monotone contribution                             │
  │ retraction = premises / TMS (dependency-directed)              │
  │ ONE hand-rolled propagation core (worklist):                   │
  │ native cycles + retract-then-assert  ⟹  live C↔F is in-core     │
  │ pluggable domain (D3):  Set<int> = clear · bitset = fast       │
  └───────────────────────────────┬──────────────────────────────┘
                                  │  driven by (asserts premise, reads ⊥)
  ══════════ separate, optional module — NOT the engine ══════════
  Search / observe driver  ·  CSP-solver lineage  ·  fully modular
  guess → propagate (core) → retract on ⊥ → repeat
  owns: cell-selection (MRV / min-entropy), restarts, any trail
  drives the propagation core; absent for pure propagation / C↔F
  (its MRV / min-entropy index may use the Adaptive façade — benchmark)
```

**Substrate.** One propagation substrate — the hand-rolled worklist core, instantiated *clear* (`Set<int>`) or *fast* (bitsets), D3. FSharp.Data.Adaptive is **not** a propagation backend: it cannot host the cell's meet, premise/TMS retraction, or cycles (it is DAG-only), so forcing propagation onto it means fighting the substrate — the fold prototype's `AList.fold`-simulated meet, the `AddCallback`/`transact` cycle hack, and the `shallowEquals` NRE are all that fight. Its sole, *optional* role is the outer reactive **observation** façade (incremental derived collections + UI binding), outside the engine (D7; 2026-06-22 "Adaptive is an optional observation façade").

**Fast-vs-clear should be an instantiation choice, not a fork.** The semantic laws and propagation algorithm should be representation-independent. The exact operational interface is deliberately not frozen yet: a fast mutable bitset may need ownership/copy operations and a narrowing operation that reports removed values, while a clear `Set<int>` implementation can remain persistent. Plug in bitsets → fast; plug in `Set<int>` → clear; prove both implementations agree through differential/property tests.

**Static genericity is the intended default, subject to measurement.** `inline`+SRTP can specialize aggressively; struct-constrained generics may also be near-zero-cost when the JIT devirtualizes them, but that is a benchmark question rather than a design guarantee. Object-interface dispatch remains the explicit runtime-switching option.

**Store axis & build order (2026-06-23, D10–D11).** Beyond the value-representation dial (D3: `Set<int>` clear / bitset fast), the *cell store* is a second, orthogonal instantiation dial: a mutable array (O(1) in-place, single live state, reverse via `Retract`/trail) or a persistent vector (`PersistentVector<Cell>` — snapshot = hold the root O(1), branches share structure, reverse via held roots) for cheap **multi-snapshot search**. Build order: mutable core first (DFS rides the single state), the persistent variant later to unlock arbitrary strategies (fair interleaving / multi-frontier). Two drop-in, project-independent assets cover the persistent path — `AspectGameEngine/PersistentVector.fs` (PV + transients) and `Hansei/Hansei.Continuation/TreeSearch.fs` (nondeterminism monads incl. `FairStream`); see the 2026-06-23 entry and [mutable-core-plan.md](mutable-core-plan.md).

## Decisions ledger

| ID | Decision | Status | Since |
|----|----------|--------|-------|
| D1 | One semantic model and shared laws: lattice element + meet + monotone propagators + explicit retraction semantics. | Decided | 2026-06-14 |
| D2 | Two capability-specific surfaces over that model: ~~Adaptive/reactive for small live networks~~, hand-rolled **propagation kernel** for batch workloads. **Search is *not* one of these surfaces — it is a separate, fully modular module that drives the propagation kernel (2026-06-22).** **Revised again 2026-06-22 ("Adaptive is an optional observation façade") — Adaptive is *not* a propagation surface:** the worklist core handles the live / re-assignable / C↔F regime natively (cyclic + retract-then-assert; see the Sketch entry). There is *one* propagation engine, instantiated *clear* or *fast* (D3); Adaptive is demoted to an optional reactive *observation* façade outside the engine (D7). They need not expose identical operations. | Revised | 2026-06-14 |
| D3 | Domain representation is pluggable. Fast = bitset (`uint16`/`uint64[]`), clear = `Set<int>`; extract the exact operational interface only after Sudoku and a minimal WFC slice expose their needs. | Revised, tentative shape | 2026-06-14 |
| D4 | Originally: search backtracking uses a **trail** (chronological undo log). **Superseded 2026-06-22** — search and its trail are *not part of the engine*; they form a separate, fully modular layer (CSP-solver lineage). The engine's sole retraction is premise/dependency-directed (TMS), which is also what supports live input replacement / C↔F re-assignment. A search module may keep an internal trail; that is the module's concern, not the engine's. | Superseded | 2026-06-14 |
| D5 | Contradiction = empty domain (⊥ = `0` bitset), tested by compare. No rich error DU in the hot path. | Decided | 2026-06-14 |
| D6 | Prefer static genericity (`inline`/SRTP or struct-constrained generic); choose the mechanism by benchmark. Object-interface (`ILattice`) is reserved for runtime switching or where measured cost is irrelevant. | Revised, tentative mechanism | 2026-06-14 |
| D7 | ~~FSharp.Data.Adaptive is **not** in the propagation kernel — it is the outer reactive shell + the small live propagator regime.~~ **Revised 2026-06-22 ("Adaptive is an optional observation façade")** — Adaptive is **not in the engine at all** (neither the kernel nor a live-propagation backend); the worklist core covers the live / C↔F regime. Adaptive's only remaining role is the *optional* outer reactive **observation** façade — incremental derived collections (`aset`/`alist`/`amap`), glitch-free views, UI binding — adopted when a reactive view or an incremental search index is wanted, and only if it beats a hand-rolled equivalent. | Revised | 2026-06-14 |
| D8 | Incremental strengthening may **warm-start** from the current store. Removing or replacing an input widens the solution space and requires checkpoint rollback, affected-region reconstruction from `Top`, or premise tracking. | Revised | 2026-06-14 |
| D9 | Start with AC-3-style propagation for Sudoku and investigate AC-4-style support counters for WFC. They share semantics, but may require distinct operational propagator protocols. | Tentative | 2026-06-14 |
| D10 | Cell **store** is a second pluggable axis, orthogonal to D3 (value rep). Mutable array/dict (O(1) in-place; reverse via `Retract`/trail) vs persistent vector `PersistentVector<Cell>` (snapshot = hold root O(1); branches share structure; reverse via held roots). Payoff: cheap multi-snapshot search. | Decided, tentative shape | 2026-06-23 |
| D11 | Build order: **mutable core first** (DFS-only search riding one live state), **persistent-vector store variant later** to unlock arbitrary search strategies. | Decided | 2026-06-23 |

## Open questions

- **Operational domain interface.** Beyond `Top`, bottom/singleton tests, count, and enumeration, does the kernel need `NarrowInto` to report the removed-value delta? How are ownership, copying, and restoration represented for mutable multiword bitsets?
- **AC-3 vs AC-4 protocol.** Which semantic laws are shared, and which operational state must remain backend-specific? WFC support counters need initialization, tile-removal events, restoration, and possibly a different propagator shape.
- **Scope of the premise / dependency-directed-retraction layer for v1.** This is *the* propagator-identity feature (see 2026-06-22 entries): without it, v1 is pure monotone narrowing, not a propagator engine. The question is no longer *whether* (it's the point) but *where and when* — does dependency-directed retraction live in the propagation kernel, the Adaptive shell, or both, and does C↔F re-assignment land in v1 or immediately after Sudoku/WFC validate the monotone core? (Search is out of this question entirely — it is the separate module, D2/D4.)
- **Adaptive backend's error channel — answered (2026-06-22).** The kernel sidesteps the `CellError × shallowEquals` NRE entirely (⊥ = 0, no DU, D5). A kept Adaptive live engine *does* still need a non-DU error channel: empirically the NRE is **F#-DU-specific** — value-type codes, `[<Struct>]` records, and *plain reference records* are all safe; **custom `IEquatable` is not** (the comparer never consults it, it walks the union structurally). Use a record or value-type code, not a DU. See genealogy doc's "other failure" section for the fix table.
- **Heuristic maintenance (lives in the separate search module, D2/D4).** Incrementally maintaining MRV (Sudoku) and a min-entropy heap (WFC) as domains shrink and on retract/restore. Cell-selection, restarts, and backtracking are the search module's concern, not the propagation core's. *Candidate mechanism:* the optional Adaptive observation façade's incremental `amap`/`aset` reductions (2026-06-22 "Adaptive is an optional observation façade") — benchmark against a hand-rolled heap before committing.
- **Incremental widening strategy.** For changed/removed givens, when is rollback sufficient, when is affected-region reconstruction cheaper, and when are explicit premises justified?
- **Public capability surfaces.** What belongs to the shared semantic contract, `PropagationSession` (batch kernel), and `ReactiveSession` respectively? (A search module, if built, sits above `PropagationSession` and is not part of the engine's surface.)
- **Adaptive for the live regime — answered 2026-06-22 ("Adaptive is an optional observation façade"): replace it.** The hand-rolled worklist core already runs the live / re-assignable / C↔F regime natively — cyclic by construction, retract-then-assert for re-assignment (verified in the Sketch entry) — which *is* the purpose-built cyclic propagator scheduler this question anticipated; and Adaptive cannot host the core's meet, premise-retraction, or cycles regardless (DAG-only; the `AddCallback`+`transact` cycle hack is also the `shallowEquals` NRE path). Adaptive is retained only as an *optional* reactive **observation** façade outside the engine (D2/D7) — incremental `aset`/`alist`/`amap`, glitch-free views, UI binding — worth adopting when a reactive view or an incrementally-maintained search index (MRV / min-entropy) is needed, and only if it beats a hand-rolled equivalent.

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

### 2026-06-22 — Retraction is the propagator identity; constraint sat is its monotone fragment
**Author:** Claude (Opus 4.8)
**Status:** Accepted — sharpens the snapshot thesis and reframes OQ-3 + the error-channel OQ. No ledger decision reversed (D4/D8 framing tightened, not their status).

Prompted by a correction from Deen: *the whole point of C↔F is that one engine does both propagation and constraint sat; if C↔F isn't supported, what's left is just constraint sat.* A review suggestion to "consider not unifying C↔F" is therefore **withdrawn** — it amounts to shipping the special case and discarding the general engine.

**The framing.** Constraint sat is not a second paradigm to bolt on; it is the propagator model restricted to the monotone case. Cell = lattice element, value = meet of current contributions; *never retract a contribution* and that restriction is exactly arc-consistency / domain narrowing (Sudoku). So the live axis is **retraction**, and it has two strengths — which D4 already separates:

- **Trail** (chronological undo): enough for backtracking *search*, i.e. a full CSP **solver**. Still constraint sat.
- **Premises / non-chronological retraction**: pull the specific assertion "C=100" out of the store wherever it sits and keep the rest. This is what C↔F *re-assignment* needs, and it is what makes the engine a *propagator* engine rather than a solver.

Lattice walk on the flat scalar lattice (⊤ ⊐ value ⊐ ⊥): asserting C=100 then propagating c→f ⟹ F=212 is pure information gain — monotone, plain meet — so *set-once C↔F unifies with CSP for free*. Re-asserting C=0 while C=100 is `meet(100,0) = ⊥`; to land on 0 you must first retract the premise that put C at 100. That single non-monotone step is the entire differentiator.

**Consequence for the plan.** The premise / non-chronological-retraction layer is **core to the engine's identity, not a deferrable extra**. Deferring it (the prior lean of OQ-3 and of the 2026-06-14 implementation-order step 6) caps v1 at CSP-with-search. Architecturally this is the same question as D2/D7: if non-chronological retraction lives *only* in the Adaptive shell and the kernel stays monotone, the kernel is "just constraint sat" wearing a reactive hat. Whether that division of labour is acceptable should be a *stated* decision, not a side effect of deferral. (Snapshot thesis updated; OQ-3 reframed. D4/D8 statuses unchanged — this sharpens their framing rather than reversing them.)

**Genealogy note.** This also retires the "propagators vs constraint propagation" split (constraint-cell-genealogy.md) as a *paradigm* distinction: Gen-1's "does C↔F by not merging at all (last-write-wins)" is a **degenerate single-contribution propagator** (`meet [x] = x`), not a separate model — which is precisely why it cannot narrow (a missing meet, not a different paradigm).

**Empirical findings folded in** (fsi + Adaptive 1.2.16, 2026-06-22), answering the error-channel OQ: the `CellError × shallowEquals` NRE reproduces in isolation (no `Cell`/cycle) and is **F#-DU-specific** — value-type codes, `[<Struct>]` records, *and plain reference records* are all safe; **custom `IEquatable` on the DU is not** (the comparer never calls it). D5 (⊥ = empty bitset, no DU) already dodges this for the kernel; a kept Adaptive live engine must use a value-type or record error channel, not a DU. Separately verified for the genealogy doc: Gen-1's C↔F convergence owes nothing to `coarsen`/the breaker — it is Adaptive's structural value-equality halting on an *exact-inverse* float round-trip; non-inverse transforms send it into the breaker (an error state), not convergence.

### 2026-06-22 — Search is a separate, fully modular layer (not part of the engine)
**Author:** Claude (Opus 4.8), at Deen's direction
**Status:** Accepted — Supersedes D4; revises D2; tightens the snapshot thesis, the target-shape diagram, OQ-3, and the heuristic-maintenance OQ. Supersedes the trail/search bullets of the earlier 2026-06-22 entry.

Deen's correction: *backtracking and searching are not this project's provenance — search is something separate, fully modular from the engine.* The earlier 2026-06-22 framing (and the 2026-06-14 founding design's "Solver kernel · search-heavy · trail," D2's "search workloads," D4's "search backtracking uses a trail," the implementation order's "Backtracking via trail," and MRV/min-entropy *cell-selection*) put chronological backtracking search inside the engine's kernel. That is the CSP/SAT-solver lineage (DPLL/CDCL), not the propagator lineage this project descends from. (The Stallman-Sussman term "dependency-directed backtracking" is a historical misnomer for non-chronological premise retraction — it is *not* tree search and belongs in the core, below.)

**The corrected architecture — three concerns, cleanly separated:**

1. **Semantic core (the engine)** — lattice + meet + monotone propagators + **premise/dependency-directed (TMS) retraction**. This is the propagator model and the whole provenance. Its only retraction is non-chronological (justification-based): retract a premise, propagate the lost support. This is exactly what C↔F re-assignment uses; it is *more propagation*, never search.
2. **Two propagation backends** of that core — Adaptive reactive shell (live, heterogeneous, C↔F) and a hand-rolled bitset/worklist kernel (batch, homogeneous, fast narrowing). Both are *propagation* engines; neither contains search or a trail.
3. **Search / observe — a separate, optional, fully modular layer** that *drives* the core: assert a guess (as a premise/assumption), propagate to quiescence, and on ⊥ retract and try another. It owns cell-selection (MRV / min-entropy), restarts, and any internal trail. It is the CSP-solver lineage — plugged in for workloads that need guessing (hardest-9×9, WFC's observe-step), absent for pure propagation and C↔F. This mirrors Sussman-Radul, where the propagator/TMS core and the `amb`/search strategy are distinct components.

**Consequence / open perf question.** Pure propagation (AC-3) is incomplete: the hardest 9×9 and WFC's observe-step do need the search module. Because that module is *separate*, the engine keeps a clean propagation identity while a tight backtracking searcher can still chase the sub-1 ms target *above* the core. Whether the search module retracts via its own chronological trail (fast) or by calling the core's dependency-directed retraction (provenance-faithful, possibly slower) is the module's internal choice and an open benchmark question — but it no longer contaminates the engine's semantic model.

No change to the C↔F-is-the-point thesis, nor to premise/TMS retraction living in the core. D1/D5/D7/D8 unaffected; the genealogy doc carries no search framing and is unaffected.

### 2026-06-22 — Sketch: one core that runs both C↔F and 4×4 Sudoku
**Author:** Claude (Opus 4.8), at Deen's request
**Status:** Experiment — **runs as written** (`dotnet fsi`, plain F#, no packages): C↔F yields `F=212`, then after `Retract 1` both cells return to `Top`, then re-asserting gives `F=32`; the 4×4 narrows to a full valid grid with no guess. Demonstrates D1 (one model) and D5 (⊥ = 0), and puts the premise/dependency-directed (TMS) retraction of the 2026-06-22 entries into a single generic core above both backends. No ledger change.

One `Lattice<'a>` contract and one `Engine<'a>` (cells = lattice elements, propagators = monotone contributions, plus `Assert` / `Retract` / quiesce). The *only* things that differ between the two demos are the **lattice instance** and the **propagators**:

- **C↔F** = flat scalar lattice (`Top ⊐ value ⊐ Bot`) + two directional transforms. Re-assignment (`set C=100` then `set C=0`) is **retract-then-assert**: `meet(100,0)=⊥` is never formed, because retracting premise `1` first removes C's `100` *and everything justified by it* (F's `212`), and only then does asserting premise `2` propagate fresh. Dependency-directed, no trail, no search — *more propagation*.
- **4×4 Sudoku** = bitset lattice (`Top=0b1111`, meet=`&&&`, `⊥=0`) + all-different propagators (naked- and hidden-single). Givens are premises that are *never retracted*, so the retraction machinery stays dormant: this instance is the **monotone fragment** — pure narrowing to quiescence. Same core, same `Assert`; Sudoku simply never calls `Retract`.

```fsharp
open System.Collections.Generic

// ===== Generic semantic core: ONE model; only the lattice instance + propagators vary =====

type Premise = int
type Origin  = Ext of Premise | Prop of int            // who placed a contribution on a cell

type Contribution<'a> = { value: 'a; support: Set<Premise> }   // a value + the premises it rests on

type Cell<'a> =
    { id: int
      contribs: Dictionary<Origin, Contribution<'a>>   // keyed by source ⇒ a re-fire replaces, not appends
      mutable value: 'a                                // cached meet of contribs
      mutable support: Set<Premise> }                  // union of supports of the binding contribs

type Lattice<'a> = { top: 'a; meet: 'a -> 'a -> 'a; isBot: 'a -> bool; eq: 'a -> 'a -> bool }

// Monotone: every emitted value can only narrow its target under meet. Targets are chosen at fire time.
type Propagator<'a> = { pid: int; reads: Cell<'a> list; fire: unit -> (Cell<'a> * Contribution<'a>) list }

type Engine<'a>(L: Lattice<'a>) =
    let cells = ResizeArray<Cell<'a>>()
    let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()    // cell.id ⇒ propagators reading it
    let mutable nCell = 0
    let mutable nProp = 0

    member private _.Recompute (c: Cell<'a>) =                    // meet contributions back into the cache
        let mutable v = L.top
        let mutable s = Set.empty
        for kv in c.contribs do
            v <- L.meet v kv.Value.value
            if not (L.eq kv.Value.value L.top) then s <- Set.union s kv.Value.support
        c.value <- v; c.support <- s

    member private this.Quiesce (frontier: seq<Cell<'a>>) =       // worklist to fixpoint (monotone ⇒ terminates)
        let q = Queue<Propagator<'a>>()
        let wake (c: Cell<'a>) = for p in watch.[c.id] do q.Enqueue p
        Seq.iter wake frontier
        while q.Count > 0 do
            let p = q.Dequeue()
            for (t, k) in p.fire () do
                let before = t.value
                t.contribs.[Prop p.pid] <- k
                this.Recompute t
                if not (L.eq t.value before) then wake t          // narrowed ⇒ wake its readers

    member _.NewCell () =
        let c = { id = nCell; contribs = Dictionary(); value = L.top; support = Set.empty }
        nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

    member _.AddProp (reads, fire) =
        let p = { pid = nProp; reads = reads; fire = fire }
        nProp <- nProp + 1
        for c in reads do watch.[c.id].Add p
        p

    member _.Value (c: Cell<'a>) = c.value

    member this.Assert (p: Premise, c: Cell<'a>, v: 'a) =          // assert a premise, then propagate
        c.contribs.[Ext p] <- { value = v; support = Set.singleton p }
        this.Recompute c
        this.Quiesce [c]

    member this.Retract (p: Premise) =                            // dependency-directed: drop p's support, re-derive
        let touched = ResizeArray<Cell<'a>>()
        for c in cells do
            let dead = [ for kv in c.contribs do if kv.Value.support.Contains p then yield kv.Key ]
            if not dead.IsEmpty then
                for k in dead do c.contribs.Remove k |> ignore
                this.Recompute c                                  // value rises back toward Top
                touched.Add c
        this.Quiesce touched                                      // re-propagate what's still supported (sans p)
```

```fsharp
// ===== Instance A: C↔F — flat scalar lattice (Top ⊐ value ⊐ Bot) =====
// the Scalar DU is safe here: it is our engine's own value type, NOT a value on
// Adaptive's shallowEquals change-detection path (the NRE context — see genealogy doc).

type Scalar = Top | Val of float | Bot

let scalarL =
    { top = Top; isBot = (fun x -> x = Bot); eq = (=)
      meet = fun a b ->
        match a, b with
        | Top, x | x, Top       -> x
        | Val x, Val y when x=y -> Val x
        | _                     -> Bot }     // Val x ⊓ Val y (x≠y), or anything ⊓ Bot  ⟹  ⊥

let e  = Engine<Scalar>(scalarL)
let cC = e.NewCell()
let fF = e.NewCell()
e.AddProp([cC], fun () -> match cC.value with Val x -> [ fF, { value = Val (x*9.0/5.0 + 32.0); support = cC.support } ] | _ -> []) |> ignore  // C→F
e.AddProp([fF], fun () -> match fF.value with Val y -> [ cC, { value = Val ((y-32.0)*5.0/9.0); support = fF.support } ] | _ -> []) |> ignore  // F→C

e.Assert(1, cC, Val 100.0)     //  C=100  ⟹  e.Value fF = Val 212.0
e.Retract 1                    //  drops C=100 *and* the F=212 justified by it ⟹ both back to Top
e.Assert(2, cC, Val 0.0)       //  C=0    ⟹  e.Value fF = Val 32.0   (meet(100,0)=⊥ never formed)
```

```fsharp
// ===== Instance B: 4×4 Sudoku — bitset lattice (Top = {1,2,3,4}, meet = &&&, ⊥ = 0; D5) =====

let bitL = { top = 0xFus; meet = (&&&); isBot = (fun d -> d = 0us); eq = (=) }
let bit v : uint16 = 1us <<< (v - 1)
let single (d: uint16) = d <> 0us && (d &&& (d - 1us)) = 0us       // popcount == 1

let s    = Engine<uint16>(bitL)
let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]

let units =                                       // 4 rows ∪ 4 cols ∪ 4 boxes
    [ for r in 0..3 -> [ for c in 0..3 -> r, c ] ]
  @ [ for c in 0..3 -> [ for r in 0..3 -> r, c ] ]
  @ [ for br in 0..1 do for bc in 0..1 -> [ for dr in 0..1 do for dc in 0..1 -> 2*br+dr, 2*bc+dc ] ]

for u in units do
    let cs    = at u
    let sup () = cs |> List.map (fun p -> p.support) |> Set.unionMany   // over-approx (dormant in Sudoku)
    // all-different, naked single: a peer fixed to v removes v from this cell
    s.AddProp(cs, fun () ->
        [ for t in cs ->
            let gone = cs |> List.fold (fun a p -> if p.id <> t.id && single p.value then a ||| p.value else a) 0us
            t, { value = 0xFus &&& ~~~gone; support = sup () } ]) |> ignore
    // all-different, hidden single: a value with a unique home in the unit lands there
    s.AddProp(cs, fun () ->
        [ for v in 1..4 do
            match cs |> List.filter (fun p -> p.value &&& bit v <> 0us) with
            | [t] when not (single t.value) -> yield t, { value = bit v; support = sup () }
            | _ -> () ]) |> ignore

// givens as premises (consistent with one solution; never retracted)
let givens = [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]
givens |> List.iteri (fun i ((r, c), v) -> s.Assert(100 + i, grid.[r, c], bit v))
// each Assert propagates to quiescence; a proper 4×4 narrows to all-singletons with no guess.
```

**What it demonstrates.** (1) **D1 is concrete, not aspirational** — the lattice plus the propagators are the *entire* delta between a bidirectional, re-assignable scalar network and a Sudoku solver; the cell/meet/propagator/`Assert`/`Retract` machinery is byte-for-byte shared. (2) **Retraction lives in the core, above both backends, and is purely justification-based**: `Retract p` = drop every contribution whose `support ∋ p`, recompute the meet, re-propagate — never a chronological trail. The C↔F re-assignment is exactly *retract-then-assert*. (3) **The core already exposes the surface a *separate* search module would drive**: `Assert(guess as a premise)` → quiesce → on `isBot`, `Retract` and try another. Search is *absent* here (a proper 4×4 settles by narrowing; C↔F is pure propagation + one retraction) — consistent with the 2026-06-22 "search is a separate, fully modular layer" decision.

**What it deliberately omits / where it's honest.** It *ran* (above), but it is still a **blueprint, not the engine** — a standalone fsi prototype that verifies the *semantics*, not a performance or completeness claim (the journal's top status line — "nothing here is implemented yet" — still holds for the real engine). The givens were chosen to have a clean solution; this run is an existence proof that one core does both, not a benchmark or a uniqueness check. Support sets use a safe **over-approximation** (union of all a propagator's reads' supports — never keeps something it should drop; may drop something it could keep, triggering harmless re-derivation). This is the **clarity build** (boxed `Set<Premise>`, dictionary contribs, list-based units); the fast bitset/worklist kernel (D2/D3) would drop premises and support tracking *entirely* for the monotone Sudoku path, since that path never retracts. The two propagators (naked + hidden single) solve *proper* 4×4s; an instance that needs a guess is exactly where the separate search module attaches, through this same `Assert`/`Retract` surface. ⊥ is merely *recorded* (a cell at `0us` / `Bot`), not acted on — detecting `isBot` and reacting is the search module's job, not the core's.

### 2026-06-22 — Adaptive is an optional observation façade, not a propagation substrate
**Author:** Claude (Opus 4.8), at Deen's request
**Status:** Accepted — revises D2 and D7; answers the "keep Adaptive for the live regime?" open question; redraws the snapshot target-shape diagram and adds a "Substrate" line. D1/D3/D5 unaffected.

Prompted by Deen's question — *do we need Adaptive; does it enable anything interesting?* The verified Sketch entry (above) forces the issue: it runs the live, re-assignable C↔F network on the plain hand-rolled worklist core — native cycles, retract-then-assert — which is exactly the regime the target-shape diagram had reserved for the Adaptive "reactive backend." That backend's rationale is therefore gone.

**Why Adaptive cannot be the propagation substrate (a paradigm mismatch).** FSharp.Data.Adaptive is self-adjusting computation: a derived `aval` is a pure function of its inputs, recomputed — incrementally, cached, glitch-free — when a `cval` changes. Three properties follow, each disqualifying it from the core:

- **No meet / no multi-writer cell.** An `aval` is one value from one expression; our cell *accumulates* contributions from many propagators and merges them by meet. Hosting that on Adaptive means hand-rolling the meet inside a node and managing the contributor set yourself — Adaptive then does change-tracking plumbing, not constraint work. (Precisely the fold prototype's `AList.fold`-simulated meet, whose per-node machinery *was* the overhead — see the genealogy doc.)
- **No retraction.** Adaptive re-assigns an input and recomputes derived values wholesale; it has no notion of "F=212 holds *because of* premise 1." Dependency-directed (TMS) retraction is the engine's identity (2026-06-22 "Retraction is the propagator identity") — so the capability that *defines* the engine is the one thing Adaptive structurally cannot host.
- **DAG-only.** C↔F is a cycle; Adaptive closes cycles with the `AddCallback`+`transact` hack — the same path that carries the `CellError × shallowEquals` NRE.

In one line: Adaptive **recomputes from inputs**; the engine **accumulates contributions and retracts by premise**. Constraint propagation is the second model, and the worklist core is its honest substrate — which already gives incremental *scalar* propagation anyway, via the change-guard (`wake` fires only on a narrowed value).

**What Adaptive uniquely enables (the part worth keeping).** Its crown jewel is not `aval` but **incremental derived _collections_** — `aset`/`alist`/`amap` propagating element-level deltas, glitch-free and demand-driven — which is genuinely hard to rebuild. Two uses, both strictly *outside* the engine:

1. **A live reactive view** over the cell network (WFC / Sudoku / constraint explorer): derived collections (the `aset` of unsolved cells, aggregates, a rendered grid) update by delta as the solve runs, never exposing an inconsistent intermediate. The natural façade *in front of* the engine.
2. **The separate search module's heuristic index** (D2/D4): MRV / min-entropy is "undecided cells ordered by domain size," maintained as domains shrink and restore — an incremental `amap` reduction, which can drive the searcher without touching the propagation core. Caveat (house style): a hand-rolled min-entropy heap may beat it; benchmark, don't assume.

**Decision.** Demote Adaptive from *propagation backend* (the diagram's left box; D2/D7) to *optional reactive observation façade*. The engine — core plus both clear/fast instantiations (D3) — depends on nothing. Adaptive is adopted only at the observation boundary, when a reactive view or an incremental search index is wanted, and only after it beats a hand-rolled equivalent. Net: not needed by the engine; a real asset at the edge; never in the engine.

This does not touch the C↔F-is-the-point thesis or premise/TMS retraction living in the core; it removes the last reason the engine itself would depend on Adaptive. Open follow-up (benchmark, not model): if/when a reactive view or incremental MRV index is built, measure the Adaptive façade against a hand-rolled equivalent before committing.

### 2026-06-23 — Store is a second pluggable axis; mutable core first, persistent variant for search
**Author:** Claude (Opus 4.8), at Deen's request
**Status:** Accepted — introduces D10 (store axis) and D11 (build order); extends D3 (a second instantiation dial) and the search-module decisions (D2/D4 + 2026-06-22 "search is a separate, fully modular layer"). Sketch only — no code, no benchmark. Detailed mutable-core plan tracked in [mutable-core-plan.md](mutable-core-plan.md).

Two ideas from a design conversation, recorded to rehydrate from.

**Idea 1 — the cell store is a second pluggable axis, orthogonal to D3.** D3 makes the *value in a cell* pluggable (`Set<int>` clear / bitset fast). Independently, the *store of cells* is also pluggable:

- **Mutable array/dict** — O(1) in-place update; one live state; reverse a guess via `Retract` (premise/TMS) or a trail. This is what the verified Sketch core already uses.
- **Persistent vector** (`PersistentVector<Cell>`) — snapshot a choice point = keep the immutable root (O(1)); branch = two roots sharing structure, paying only for diverged cells; reverse = reuse the held parent root (no trail, no undo). A transient gives array-speed mutation *within* a propagation burst, frozen at the choice boundary.

Same propagators, same meet — the store is just another instantiation dial beside the value rep. Its payoff is **multi-snapshot search**: holding many live partial assignments cheaply (fair interleaving, multi-frontier, all-solutions, user undo/redo) — what a single-state mutable store cannot do without copying. It also gives the open "how does the searcher reverse?" question a third answer for this variant: not a chronological trail, not the core's dependency-directed retraction, but *held roots* — reversal is free because nothing was mutated.

**Idea 2 — build order: mutable core first, persistent variant later.**

1. **Mutable core first** (the key deliverable) — harden the verified Sketch into a real worklist engine. Search support at this stage = **DFS only**, which a single-state mutable store serves soundly (guess → propagate → on ⊥ reverse → next).
2. **Persistent store variant later** — swap in `PersistentVector<Cell>` to unlock *any* search strategy. Deferred because the persistent store is pure overhead for single-state DFS; it earns its keep only when search needs many live snapshots.

**Reusable assets (both independent of this project — drop-in, not built here).**
- `AspectGameEngine/PersistentVector.fs` — Clojure-style persistent vector + `TransientVector` (the `EditSessionId` ownership trick = the mutable-in-burst / persistent-across-choices hybrid). Caveat: `obj[]` backing **boxes value types**, so it pairs naturally with the *clear* `Set` rep; the fast bitset rep would want a primitive-backed store (an interaction between this axis and D3).
- `Hansei/Hansei.Continuation/TreeSearch.fs` — a menu of nondeterminism monads (`choices`/`guard`/`constrain`/`fail`) including `FairStream` (fair interleaving). The persistent variant's strategy *driver*, sitting above the core (CSP-lineage, D2/D4); it never touches meet or `Retract`. In F#, search threads the persistent root as a plain value — no `StateT`.

Provenance unaffected: the core keeps meet + premise/TMS retraction; the persistent store and the Hansei driver are both outside the semantic model, consistent with the 2026-06-22 "search is a separate, fully modular layer" decision.
