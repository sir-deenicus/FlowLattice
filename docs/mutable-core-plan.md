# Mutable-core engine — build plan (SKETCH)

> Sketch written 2026-06-23 to survive a context compaction. The **detailed** mutable-core
> plan gets expanded here *after* compact. Right now this only fixes the decision + sequencing.

## Sequence (the decision)

1. **Mutable core first — the key deliverable.** Worklist engine: cells = mutable slots
   (array/dict store), `meet` (⊓), monotone propagators, `Assert` / `Retract`,
   propagate-to-quiescence. This *hardens the already-verified journal sketch* (C↔F via
   retract-then-assert + 4×4 Sudoku ran under `fsi`) — hardening, not greenfield.
2. **Search v1 = DFS only**, riding the mutable core. guess → propagate → on ⊥ reverse → next.
   One live state, so a mutable store is sound. Reverse = premise/TMS `Retract` (provenance)
   or a trail (benchmark the two).
3. **Persistent variant later** — swap the cell store to `PersistentVector<Cell>`. Reverse
   becomes *free*: hold the parent root, drop the branch's. Snapshot = keep a root (O(1));
   branches share structure. Unlocks **any** search strategy (fair interleaving /
   multi-frontier / all-solutions).

## The store is a second axis (orthogonal to journal D3)

- D3 (existing) = *value in a cell*: `Set<int>` clear vs `uint16` bitset fast.
- **New axis** = *store of cells*: mutable array (in-place; reverse via `Retract`/trail) vs
  `PersistentVector<Cell>` (snapshot-cheap; reverse via held roots).
- The core is parameterized over the store; mutable and persistent are two instantiations of
  one engine. Same propagators, same meet.

## Immutability sketch (the persistent variant)

- `store = PersistentVector<Cell>`. A propagation burst runs on `store.AsTransient()`
  (array-speed `UpdateInPlace` / `updateMany`), then `Persistent()` freezes at quiescence /
  the choice boundary.
- A guess snapshots by keeping the frozen root. Backtrack = reuse the parent root (untouched;
  sharing pays only for diverged cells). No trail needed in this variant.
- Search threads the root as a plain value — F#, no `StateT`.
- Provenance stays clean: the persistent variant's search is the **outer driver**
  (CSP-lineage); the core keeps `meet` + premise/TMS `Retract`. Driver drives the core, never
  replaces it.

## Reusable assets (both independent of this project — drop-in)

- `AspectGameEngine/PersistentVector.fs` — Clojure-style persistent vector + `TransientVector`
  (EditSessionId ownership = the mutable-in-burst / persistent-across-choices hybrid).
  Caveat: `obj[]` backing boxes value types → couples to the **clear** `Set` rep; the fast
  bitset rep would want a primitive-backed store.
- `Hansei/Hansei.Continuation/TreeSearch.fs` — nondeterminism-monad menu
  (`choices` / `guard` / `constrain` / `fail`) incl. `FairStream` (fair interleaving). The
  persistent variant's strategy layer.

## Step 1 — array-backed mutable core — BUILT + verified 2026-06-24

> **DONE.** Built as [propagator-mutable-core.fsx](../propagator-mutable-core.fsx) exactly to the
> design below. One process carries both engines; all four solvers match the known solution and the
> ported C↔F retraction behaves (checked before timing). Head-to-head result (best-of-5 × 2000,
> recorded in [benchmarks.md](benchmarks.md) 2026-06-24): **M beats baseline at the same rep — Set
> 6.22×, bit 14.74×**; best config M-bit 127.0 µs = 41.99× the original baseline-Set, *no law
> changed*. The bit win (14.74×) >> the Set win (6.22×) because `uint16` is a register so removing
> the surrounding allocation — especially the baseline's generic `<>` **boxing** the `uint16`, now a
> primitive `equals` — exposes the full machinery savings; `Set<int>`'s heap value caps its win.
> Next: step 2 (DFS driver).

**Task framing (Deen 06-24):** scope = *start the array-backed core* (this step, not an in-place
tune of the Part-1 engine); form = *working perf fsx* (A9 maintainer register, benchmark harness,
results → [benchmarks.md](benchmarks.md)). Deliverable file: **`propagator-mutable-core.fsx`**.
Goal: beat the 06-23 baseline (Set 6968 / bit 1747 best µs, 4×4 Sudoku) on the SAME scenario,
**without changing a law**. NOT in step 1: search/DFS (step 2), persistent store (step 3).

### Lattice — 4-field (was `{top; meet}`)
`{ top:'a; meet:'a->'a->'a; isBot:'a->bool; equals:'a->'a->bool }`.
- `equals` is the change-detector, REPLACING the old engine's generic `<>` (`when 'a:equality`),
  which **boxes value types** (uint16) through generic equality — a suspected real baseline cost.
  Rep-specific `equals` (uint16 `=`, Set structural) avoids the box.
- `isBot` = contradiction test (Set.isEmpty / bitset = 0us) — for the laws + the C↔F demo; not on
  the Sudoku hot path (solvable, no ⊥).

### Store = structure-of-arrays (kills per-cell `Dictionary` alloc)
- `values:'a[]`, `supports:uint64[]`. A cell IS an `int` index — no per-cell record, no per-cell
  `Dictionary`.
- **Premises = a `uint64` bitmask** (premise k = bit k); union = `|||`, no allocation — replaces the
  `Set<Premise>` stamps. LIMIT ≤64 live premises (Sudoku 8 givens, C↔F 2 — fine; document the cap,
  growable bitset later if needed).

### Worklist = `Queue<int>` of prop-ids + `queued:bool[]` dedup
- Each propagator in the queue **at most once** (set on enqueue, clear on dequeue). Kills the old
  engine's duplicate-enqueue churn — the "repeated quiescence", likely the single biggest cost.
- `watch: int[][]` (cell-id → prop-ids), built from ResizeArrays then frozen to a jagged array.

### Propagator API = emit-callback (kills per-fire list alloc)
- `fire: Emit -> unit`, where `Emit` is ONE closure per engine (reused) doing meet+support+wake.
  Props call `emit targetId value support` per output — no `[ t, {…} ]` list allocated per fire
  (the old engine allocated one every fire).

### Forward path = incremental meet (no re-fold)
- On `emit t v s`: `nv = meet values.[t] v`; if `not (equals nv values.[t])` then
  `values.[t]<-nv; supports.[t]<-supports.[t] ||| s; enqueue watchers of t`; else nothing.
- **Support OR-on-narrow only** — add a contribution's premises to a cell iff it actually narrowed
  the cell. A non-narrowing contribution has no downstream effect, so excluding it keeps support
  ~minimal and stays correct; downstream support flows via value-change wakes. Valid because meet
  only descends (monotone) and is idempotent → quiesces.

### Retraction = reset support-cone + replay (NO per-prop contrib storage)
- Store only the live Ext assertions: `Dictionary<struct(premise:int * cell:int), 'a>` — ONE small
  dict, not per-cell. `Assert(p,c,v)` writes it and meets `v` in under support `1UL<<<p`.
- `Retract(p)`: drop p's Ext entries; reset every cell with `supports &&& (1UL<<<p) <> 0UL` to
  `top`/support `0`; re-`Assert` all surviving Exts; full re-quiesce.
- Correct by **confluence** (the surviving-Ext meet fixpoint is unique — an engine law), so
  reset+rederive = the same store as surgical subtraction. Dependency-directed: only the
  support-cone is touched (provenance via the bitmask). NOT a trail, NOT search/backtracking
  ([cf-propagator-capability-non-negotiable]). Over-approx support is safe — rederive recomputes
  true values. Retraction is NOT benchmarked (Sudoku has none); correctness shown by the C↔F
  retract-then-assert demo ported onto the new engine.

### Benchmark = head-to-head in ONE run (benchmarks.md "never compare across runs")
- COPY into the fsx: old Part-1 engine (lines ~24-100) + its Set & bitset Sudoku wirings
  (143-192) + the `bench` harness (203-218) + `givens`/`unitCoords` (131-137). ADD the new engine
  + new Set & bitset wirings.
- Time **4 rows**: old-Set, old-bit, new-Set, new-bit (same machine/session/flags). Show new beats
  old at each rep.
- Verify FIRST (A6 differential): new solves == `expected` == old for both reps; refuse to time a
  wrong solver. Then add a dated section to benchmarks.md (env block + 4-row table + reading).

> Step-2 (DFS driver) and step-3 (persistent `PersistentVector<Cell>` store) detailed plans still
> pending — keep the journal sketch as the reference skeleton for those.

## Target application: WFC at 500×500 (Godot map gen) — 2026-06-24, reshapes steps 2 & 3

Deen will use this engine for **Wave Function Collapse** tile-map generation in a **Godot** game,
grids up to **500×500 = 250k cells**. WFC *is* the propagator model, so the core fits directly:

- **cell value** = set of still-possible tiles (a superposition) → a **bitset**; **meet = intersection
  (AND)**; **⊤** = all tiles; **⊥** = empty = contradiction.
- **adjacency rule** = a monotone propagator between neighbours: B's possibilities `∩=` (tiles allowed
  next to A's current possibilities in that direction). ~4 per cell → ~1M directed propagators at
  500×500; monotone (A narrows ⇒ B's allowed set only shrinks).
- **collapse** = `Assert` a cell to one tile, then propagate. **contradiction** = a cell hits ⊥.
- **min-entropy selection + backtracking** = the SEARCH DRIVER (step 2), NOT the core.

So WFC is the forcing application for the rest of the roadmap. The propagation core is already
suited (local waves + the `Queue`/`queued` worklist); the work is on four axes around it:

1. **Value width = tile count.** ≤16 → `uint16` (current); ≤64 → `uint64`; >64 → a value-typed
   **multiword bitset struct** (`struct` of N×`uint64`; `meet` = AND per word, `equals` = per-word
   compare, `isBot` = all-zero). The generic `Lattice<'a>` + 4-field design accept this with **no
   engine change** — `'a` becomes the struct. Avoid a per-cell `uint64[]` (heap alloc × 250k).

2. **Backtracking mechanism — the important one.** The step-1 `Retract` (reset support-cone + **replay
   ALL propagators**) is O(all-props) ≈ 1M replays per call at this scale — fine for an occasional
   interactive edit, unusable as a search primitive. WFC's chronological backtracking is the **search
   driver's** job (step 2): a **trail** (log each narrow since the last choice point; undo by
   restoring) or the **persistent store** (step 3: hold the pre-collapse root, revert O(1) with
   structure sharing). This does NOT reframe the core's retraction — the core keeps TMS premise-cone
   `Retract` for provenance/interactive edits (the non-negotiable); the trail/snapshot lives in the
   separate search layer the plan always kept out of the engine.

3. **Premise/support cap is a non-issue if (2) is respected.** The `uint64` support mask (≤64 live
   premises) is plenty for *authored* constraints (user-painted tiles, region locks) — keep it for
   incremental editing. It must NOT be spent per-collapse on search depth (would exceed 64 and cost
   per-cell stamps); search depth rides the trail/snapshot.

4. **Min-entropy cell selection at 250k** is O(n)/step if scanned naively ⇒ O(n²) total. The driver
   needs an indexed priority queue / popcount-bucketed dirty-set, updated as cells narrow. (Driver
   concern, step 2.)

**Provenance/retraction STAYS** (Deen: "abs not" to dropping support). And the earlier "optimization"
list was mostly the Sudoku DEMO wiring, not the engine: only emit's redundant `values.[t]` read and
`Queue`/array preallocation are engine-level; the hidden-single alloc / `set[1..4]` hoist / array-units
are demo-specific and won't transfer to the WFC propagators.

**Godot:** the engine is pure .NET (no Godot deps) → reference the compiled F# assembly from a Godot 4
C#/.NET project, write collapsed tile indices into a `TileMap`. A long 500×500 run can stream progress
/ accept cancellation via the existing AsyncRx-on-Hopac kernel (optional synergy).

**Build-order shift:** WFC needs **step 2 (search driver: trail-backtracking + min-entropy PQ)** next,
and likely **step 3 (persistent store)** for cheap snapshots; the value-width struct is a small add
once the tileset exceeds 64.

### Locked by Deen 2026-06-24

- **Tiles: hundreds+, varies per map.** ⇒ the value rep is *not* a fixed small struct. Plan a
  **bit-plane SoA**: `W = ⌈tiles/64⌉` planes, each a `uint64[nCells]`; a cell's value is its `W` words
  `plane[w].[c]`. meet = AND per plane, `isBot` = OR-of-planes = 0, entropy = Σ popcount. `W` is a
  **runtime** value, so this warrants a **specialized WFC store** — the generic `Lattice<'a>` /
  `values:'a[]` assumes one array of `'a`; bit-planes are a different store (same laws, specialized for
  density + runtime width). This is just another instantiation of the plan's "store is a second axis."
  A fixed struct rep stays the answer only for small fixed tile counts.
- **Backtracking search** (confirmed) ⇒ step-2 driver with a **trail** (log `(cell, oldValue, oldSupport)`
  on each narrow since the last choice point; on ⊥ unwind LIFO restoring both fields, clear the worklist,
  drop the tried tile, retry). NOT the step-1 replay-all `Retract`. **Trail must journal `support`, not
  just `value`** (corrected 2026-07-02): propagation flows provenance along the edges a collapse activates
  (`B.support |= A.support`) even when the collapse itself spends no premise bit. Sharpened 2026-07-03:
  supports only grow during a descent, so a value-only restore leaves masks that are strict *supersets* —
  later fixpoints stay CORRECT (over-approx support is safe, per step 1 above), but `Support c` stops
  answering provenance truthfully and every authored `Retract` cone inflates toward the whole grid (bits
  flowed on *abandoned* branches stick forever), defeating the cone-LOCAL retract step 5 exists for.
  Journaling both fields makes the restore land exactly on the pre-guess fixpoint with **no re-propagation**
  (LIFO restore ⇒ the saved values *are* the previous quiescent state), which is the whole reason a trail
  beats `Retract` for search. **"Clear the worklist" = drain the queue AND unset the `queued[]` dedup
  flags** (2026-07-03): `q.Clear()` alone leaves the drained props flagged queued-forever, and `emit`'s
  dedup check would never re-admit them — silently missed propagation after the first backtrack.
- **Incremental-async first, interactive editing the goal** ⇒ BOTH retraction layers are real: the
  **trail** for generation backtracking, and the core's **TMS premise-cone `Retract`** for authored
  edits (paint/lock a tile = a premise; un-paint = retract its cone). The `uint64` premise mask is for
  these authored constraints (≤64 at a time is ample); search depth never spends a premise. **But**
  interactive editing on 250k cells also kills the step-1 replay-ALL `Retract` (O(1M)/edit) — WFC's
  propagator graph is the local grid, so retraction must become **reset-cone + re-fire only the
  propagators incident to the cone** (local; correct because the graph is structured), not a global
  replay. Incremental generation + cancellation stream via the AsyncRx kernel.
- **Tiled / explicit adjacency** ⇒ author `allowed[tile][dir]` bitsets; the edge propagator is
  `B ∩= ⋁_{t∈A} allowed[t][dir]`. Cheap when A is near-collapsed (the impactful case). If dense-A ORs
  dominate, the **AC-4 support-counter** variant is the known faster-but-stateful alternative —
  benchmark before adopting.

### Work left, ordered (2026-07-02)

Godot + AsyncRx streaming is the *target*, not a tracked build step — dropped from this list.

1. **Bit-plane SoA store** — `W = ⌈tiles/64⌉` planes of `uint64[nCells]`, runtime `W`; meet = AND/plane,
   `isBot` = all-planes-zero, entropy = Σ popcount. New specialized store (generic `'a[]` can't hold a
   runtime-width multiword value).
2. **Tiled adjacency propagator** — `allowed[tile][dir]` bitsets, `B ∩= ⋁_{t∈A} allowed[t][dir]`, looped
   over planes. AC-4 counters as benchmarked fallback.
3. **Trail** — journal `(cell, value, support)` on each narrow, marks at choice points, LIFO unwind +
   clear worklist — the queue AND the `queued[]` dedup flags (see the backtracking bullet above for both
   corrections).
4. **Search driver** — min-entropy via popcount over a bucketed/indexed PQ (not an O(n²) scan); collapse →
   propagate → on ⊥ unwind + exclude the tried tile + retry. **Needs ⊥ early-exit in propagation**
   (2026-07-03): `quiesce` today runs the queue dry regardless; in WFC a ⊥ cell propagates ∅ outward and
   avalanches the whole grid before control returns to the driver. Abort quiescence when `emit` meets to
   `isBot` (the lattice already carries `isBot`; it is unused on the hot path) — the abort reuses the same
   drain-queue + clear-flags machinery as the unwind.
5. **Cone-local `Retract`** — replace the step-1 replay-ALL with reset-cone + re-fire only the propagators
   incident to the cone (the interactive-edit layer). **Incident = props that read OR WRITE a cone cell**
   (2026-07-03): the writers are the re-derivers — a reset cell is never re-narrowed by its readers plus
   cascade, because the writers' inputs didn't change so nothing ever wakes them; readers-only leaves cone
   cells at ⊤+Exts, which is not the least fixpoint (not even quiescent). The engine indexes reads only
   (`watch`), so step 5 needs a write-side index (or the grid's structural neighbor enumeration). Fold in
   here too: step-1 `Retract`'s step 3 re-melds surviving Exts into ALL cells, unconditionally OR-ing
   premise bits into non-cone supports — violates the OR-on-narrow policy (harmless under replay-ALL,
   pollutes provenance and inflates future cones under cone-local). Restrict the re-meld to cone cells.

Order: 1 → 2 → (verify on a small grid) → 3 + 4 (generation works end-to-end) → 5 (authored edits).
Benchmark at 500×500 along the way. The earlier micro-opts (#4 emit read, #5 preallocation) stay noise at
this scale — skip.
