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
   **BUILT + verified 2026-07-03** as [propagator-wfc.fsx](../propagator-wfc.fsx), with one as-built
   delta: the store is **cell-major** (one flat `uint64[nCells*W]`, a cell's `W` words contiguous), not
   plane-major — same laws, same no-per-cell-heap point, but the hot path touches all `W` words of two
   cells per fire (1–2 cache lines cell-major vs `W` scattered lines plane-major), and no whole-grid
   single-plane pass exists to favor planes. `⊤` masks off the last word's bits ≥ `nTiles`.
2. **Tiled adjacency propagator** — `allowed[tile][dir]` bitsets, `B ∩= ⋁_{t∈A} allowed[t][dir]`, looped
   over planes. AC-4 counters as benchmarked fallback.
   **BUILT + verified 2026-07-03** in the same file. As-built deltas: (a) the propagator is **not a
   closure** — the worklist holds **cell ids** and firing a cell pushes into its ≤4 structural neighbors
   (avoids ~1M closure allocations at 500×500, and every edge's write-side is structurally known — which
   item 5 needs anyway); (b) the item-4 **⊥ early-exit** (abort quiescence on an emptied cell, drain
   queue + unset `queued[]` flags) is pulled forward into this file — the machinery is shared with the
   item-3 trail unwind, and without it a ⊥ avalanches ∅ across the grid even in small demos. Verified:
   ramp closed-form domains across the 64-bit word seam, OR-on-narrow supports, assert-site ⊥,
   mid-propagation ⊥ (early-exit + clean worklist + `Reset` reuse), direction-asymmetric gravity rule.
   First 500×500 timings in [benchmarks.md](benchmarks.md) (2026-07-03): cost ∝ wave, not grid — a
   499-cell column forces in ~0.12 ms; the dense-⊤ ramp waves are the union loop's worst case (T=512
   corner pin ≈ 5.3 s for ~131k dense cells under fsi optimize-off) — the AC-4 trigger to watch once the
   driver runs, since real firing cells are near-collapsed.
3. **Trail** — journal `(cell, value, support)` on each narrow, marks at choice points, LIFO unwind +
   clear worklist — the queue AND the `queued[]` dedup flags (see the backtracking bullet above for both
   corrections).
   **BUILT + verified 2026-07-03 (second pass)** in [propagator-wfc.fsx](../propagator-wfc.fsx). As built:
   the journal is engine-side MECHANISM only — struct-of-arrays with doubling (`tCell`/`tSup`/`tWords`, no
   per-entry allocation), written by `meetInto` before the support OR; `Mark`/`UndoTo` restore BOTH fields
   LIFO + drain queue + clear `queued[]` + clear the contradiction, landing on the pre-mark quiescent
   fixpoint with no re-propagation. Two additions beyond the spec, both mechanism: a premise-free
   `Collapse(cell, tile)` (search never spends a premise bit; authored provenance still flows through the
   wave) and an `OnChange` observation hook fired on every narrow AND every undo-restore — the engine's
   entire contribution to selection policy. Verified by snapshot equality (words AND supports), nested
   marks, ⊥ unwind to a usable store, and post-unwind propagation (no dead dedup flags).
4. **Search driver** — min-entropy via popcount over a bucketed/indexed PQ (not an O(n²) scan); collapse →
   propagate → on ⊥ unwind + exclude the tried tile + retry. **Needs ⊥ early-exit in propagation**
   (2026-07-03): `quiesce` today runs the queue dry regardless; in WFC a ⊥ cell propagates ∅ outward and
   avalanches the whole grid before control returns to the driver. Abort quiescence when `emit` meets to
   `isBot` (the lattice already carries `isBot`; it is unused on the hot path) — the abort reuses the same
   drain-queue + clear-flags machinery as the unwind.
   **BUILT + verified 2026-07-03 (second pass)**, same file, as `module WfcSearch` — POLICY, deliberately
   outside the engine: the engine's whole search surface is `Mark`/`UndoTo`/`Collapse` + `OnChange`, and
   selection, tile ordering, and retry are all replaceable without touching `module Wfc`. As-built deltas:
   (a) the search monad is **Hansei.TreeSearch** (`#r Hansei.Core.dll`, external asset): `guard` = an
   if-check returning empty on fail, so a failed collapse contributes nothing and the enumeration IS the
   backtracking — `Exhaustive` (the list monad) for all-solutions, `TreeSearch.LazyList` for lazy
   first-success generation. LazyList's cells are MEMOIZED: node effects run at most once, re-forcing a
   prefix is safe (a raw seq would re-run collapses); unforced tails still assume the engine is where DFS
   left it. Both strategies are strictly depth-first, which is what keeps the mark/undo bracket coherent
   on ONE mutable store — the fair/interleaving `Hansei.Backtracking` stream resumes branches out of LIFO
   order and waits for the step-3 persistent store. (b) The PQ is a **lazy binary min-heap** over packed
   `(entropy <<< 32 ||| cell)` keys subscribed to `OnChange`, validate-on-pop (discard decided, reinsert
   stale at true entropy) — not bucketed; the invariant "every undecided cell has a live entry" holds
   because every entropy transition (narrow or undo-restore) pushes one. (c) The ⊥ early-exit was already
   pulled forward into items 1+2. (d) Deep maps run the forcing on a **big-stack thread** (frames ∝
   guesses, not cells) — a cost of list-of-successes on a mutable store; the persistent variant removes
   it. Verified: staircase 1×3/1×4 closed-form solution AND failure counts (1 sol + exactly 2 failed
   guesses; unsat + exactly 3), gravity 3×3 = 4³ = 64 distinct valid solutions under the heap picker with
   exactly 0 failures (columns are paths ⇒ AC complete ⇒ zero backtracks — a sharp differential), 
   checkerboard 3×3 = 2, and LazyList first-solution valid + re-force returns the same array reference.
   Full-map generation timings (500×500 gravity/3-color, 64×64 ramp) in [benchmarks.md](benchmarks.md).
5. **Cone-local `Retract`** — replace the step-1 replay-ALL with reset-cone + re-fire only the propagators
   incident to the cone (the interactive-edit layer). **Incident = props that read OR WRITE a cone cell**
   (2026-07-03): the writers are the re-derivers — a reset cell is never re-narrowed by its readers plus
   cascade, because the writers' inputs didn't change so nothing ever wakes them; readers-only leaves cone
   cells at ⊤+Exts, which is not the least fixpoint (not even quiescent). The engine indexes reads only
   (`watch`), so step 5 needs a write-side index (or the grid's structural neighbor enumeration). Fold in
   here too: step-1 `Retract`'s step 3 re-melds surviving Exts into ALL cells, unconditionally OR-ing
   premise bits into non-cone supports — violates the OR-on-narrow policy (harmless under replay-ALL,
   pollutes provenance and inflates future cones under cone-local). Restrict the re-meld to cone cells.

   **BUILT + verified 2026-07-05** in [propagator-wfc.fsx](../propagator-wfc.fsx). As built:
   (a) `Assert` records accepted authored assertions as `(premise, cell, mask copy)` before propagation,
   including the assertion that causes `⊥`; assertions refused because the store is already contradicted
   are not recorded, and `Reset` clears the registry. (b) `quiesce` preserves bot-aborted frontier in
   `pendingBot`; in addition to drained queue ids, it records the currently firing cell when a fire is
   cut short by `⊥`, which is idempotent and covers the partially-fired-cell case. Normal quiescence,
   `Reset`, and `UndoTo` clear it. (c) `Retract p` removes every authored entry for `p`, scans the support
   cone, clears `botCell` only when the bot is in that cone, resets cone cells to `⊤` with support `0`,
   re-melds only surviving authored entries whose target is inside the cone, then enqueues cone cells,
   structural neighbors, and `pendingBot` before quiescing. It truncates the trail afterward, so authored
   edits cannot be crossed by later `UndoTo`. (d) The test oracle rebuilds a twin from the surviving
   registry; `⊥` comparisons are flag-only, non-`⊥` comparisons require exact values and clean worklists,
   and support checks enforce no retracted bit plus support masks within the surviving authored-premise
   set, without requiring exact support-mask equality. Verified hand cases cover one-of-two pins,
   overlapping cones, shared-premise removal, empty-cone/no-narrow asserts, rejected asserts, culprit and
   non-culprit retracts on `⊥`, and a `pendingBot` wave that must finish outside the culprit cone. The
   randomized differential runs 200 edits each on 6×6 gravity4 and 5×5 3-color. Post-retract `Assert` and
   `Collapse`-driven search still work. 500×500 timing in [benchmarks.md](benchmarks.md): gravity small
   cone cone-local 1.991 ms/edit vs replay 3.351 ms/edit (1.68× replay/cone); ramp512 large cone
   9920.649 ms/edit vs replay 5641.265 ms/edit (0.57×), the expected dense-frontier degenerate.

   **Review gate 2026-07-05 (tier 0): PASSED.** All locked decisions implemented as written — registry
   iff-applied (rejected-assert case tested), oracle uses flag-only ⊥ compare + exact values + clean
   worklists + retracted-bit-gone + supports ⊆ surviving-OR, and does NOT compare exact support masks
   (the trap was avoided, not tripped); `Retract` follows steps a–h including cone-only re-meld and
   trail truncation; benchmark replay side times the twin's full `Reset` inside the edit, so the 1.68×
   small-cone ratio is fair. One as-built delta beyond the brief, correct and properly declared: on
   ⊥-abort, `pendingBot` also records the *partially-fired* cell (its remaining directional pushes are
   otherwise lost — a genuine gap in the work order's "record the drained ids" wording; the executor's
   fix is right and the `verifyRetractPendingBot` trace depends on it). Editor-layer note, policy not
   engine: the large-cone 0.57× degenerate suggests a later hybrid — the cone scan already yields cone
   size, so fall back to full replay past a size threshold; zero engine change.

Order: 1 → 2 → (verify on a small grid) → 3 + 4 (generation works end-to-end) → 5 (authored edits).
Benchmark at 500×500 along the way. The earlier micro-opts (#4 emit read, #5 preallocation) stay noise at
this scale — skip.

### Item 5 work order — cone-local `Retract` (2026-07-03, delegated build)

Self-contained brief for the executing agent. The design judgment is already spent — where this brief
locks a decision, implement it as written rather than re-deriving it. The item-5 entry above is the spec;
this section adds the decisions it left open and the verification/benchmark contract.

**Scope.** One file: [propagator-wfc.fsx](../propagator-wfc.fsx) (repo root; `dotnet fsi propagator-wfc.fsx`
must exit 0 with all verifications PASS). The file has **no `Retract` today** — you build the cone-local one
directly; there is no replay-ALL legacy here to modify. The step-1 replay-ALL lives in
`propagator-mutable-core.fsx` — read it for the laws if useful, but do **not** port its re-meld-into-ALL-cells
step (that is exactly the provenance-pollution trap named above). Engine surface as it stands:
`Assert(premise, cell, mask)` / `AssertTile` (premise 0..63 → support bit `1UL <<< premise`, meet + quiesce),
premise-free `Collapse` (search spends no premise — keep it that way), trail `Mark`/`UndoTo`, `OnChange`
fired on every domain change, `Reset`, and the ⊥ early-exit with drain-queue + clear-`queued[]` machinery.

**What to build — three pieces:**

1. **Authored-assert registry.** `Assert` currently records nothing, so there is nothing to replay or
   survive. Record `(premise, cell, Array.copy mask)` in a `ResizeArray` **iff the assert is actually
   applied** — an `Assert` rejected because the store is already contradicted returns `false` and must NOT
   be recorded (the editor saw it refused; recording it would make oracle replay diverge from the live
   store). An assert that itself causes ⊥ IS recorded — retracting it is how the editor fixes the ⊥.
   Multiple asserts may share one premise (one paint stroke = one premise, many cells); `Retract p` removes
   them all. `Reset` clears the registry.

2. **⊥-abort pending set.** When `quiesce` ⊥-aborts, it currently drains the queue and unsets flags —
   losing the ids of cells that were narrowed but never fired. Those cells make the store **non-quiescent
   outside any cone** (their support need not contain the culprit premise — e.g. premise q's half-propagated
   wave dies on a ⊥ caused by premise p's earlier narrowings), and cone-local retract assumes the non-cone
   region is at fixpoint. So: on ⊥-abort, record the drained ids into a `pendingBot: ResizeArray<int>`
   (queue dedup guarantees uniqueness) instead of discarding them. `pendingBot` is cleared by `Reset` and by
   `UndoTo` (the restored pre-mark fixpoint makes it void — search ⊥s populate it too, harmlessly), and
   consumed by `Retract`.

3. **`member Retract (premise: int) : bool`** (true = store uncontradicted after, matching
   `Assert`/`Collapse`). Steps, in order:
   a. Remove all registry entries tagged `premise`.
   b. Cone scan: `cone = { c | supports.[c] &&& bit <> 0UL }` — a straight O(nCells) pass over `supports`
      (250k reads is well inside an edit-latency budget; a premise→cells index is a later optimization
      only if the benchmark says so).
   c. If `botCell ≥ 0` and the bot cell is in the cone, clear `botCell` (the culprit is being retracted).
      If the bot cell is NOT in the cone, leave it — the contradiction doesn't depend on this premise; the
      retract still runs, the store stays contradicted, and `quiesce` will immediately re-abort and re-drain
      into `pendingBot` (consistent, still-⊥ state awaiting a retract of an actual culprit).
   d. Reset every cone cell: words ← `topWord`, support ← `0UL`, and fire `onChange` (the entropy heap's
      invariant needs a push on widening too; its validate-on-pop already tolerates entropy increases).
      These resets are direct writes — NOT journaled.
   e. Re-meld surviving registry entries **whose target cell is in the cone** (`meetInto target mask
      (1UL <<< theirPremise)`). Cone-only — melding into non-cone cells is the step-1 pollution trap.
   f. Enqueue the re-derivation frontier: every cone cell AND every structural neighbor of a cone cell
      (`enqueue` dedups). Neighbors are the WRITERS into reset cells — readers-only never re-narrows a
      reset cell (spec entry above); cone cells themselves must also fire because even a ⊤ cell's
      allowed-union can constrain a neighbor. Also enqueue everything in `pendingBot`, then clear it.
   g. `quiesce ()` — may legitimately re-⊥ (other culprits remain).
   h. `tLen <- 0`. Precondition (document it on the member): `Retract` is an editor-level operation, never
      called with an outstanding search mark. Re-derivation journals its narrows via `meetInto` (harmless),
      but the cone resets in (d) are not journaled, so any surviving mark would be corrupt — truncating the
      trail makes crossing an authored edit with `UndoTo` impossible by construction.

**The correctness oracle — differential vs full replay, with one deliberate asymmetry.** Implement a
reference retract for tests: same registry removal, then full reset (all cells ⊤, supports 0, `botCell` −1,
worklist and `pendingBot` clear), then replay every surviving registry entry in insertion order
(meet + quiesce each). Run it on a **twin engine** (second instance, same parameters) so the live engine's
state survives the comparison. Comparison rule:

- If either store is contradicted: compare the `Contradiction` FLAG only (both must agree). An aborted
  store is an intentionally non-quiescent early-exit state and its cell contents are order-dependent by
  design; the two ⊥ sites may even differ.
- Otherwise: **values exactly equal** (full `words` comparison), both worklists clean, and two semantic
  support checks — the retracted bit appears in NO support, and every support mask ⊆ the OR of surviving
  premises' bits.
- **Do NOT demand exact support-mask equality.** OR-on-narrow attribution is order-dependent among
  confluent waves: whichever wave narrows a cell first gets the credit, and cone-local re-derivation
  processes a different order than from-scratch replay. Both masks are sound over-approximations of the
  true dependency set; a mismatch is not a bug. If you see one and are tempted to "fix" it by changing
  when supports are ORed, stop — that breaks the OR-on-narrow law. Soundness (never too SMALL a support)
  is what the compositional test below actually pins down: a too-small support makes a later cone miss
  cells, and the VALUE comparison after that later retract fails.

**Verification (all before any timing; the existing verifications must still pass unchanged):**

- Hand-built cases on small grids: retract one of two pins → values equal oracle; overlapping cones (two
  premises whose waves narrow shared cells) → retract either, values equal oracle, retracted bit gone;
  retract-the-culprit after a mid-wave ⊥ → contradiction cleared, values equal oracle (this exercises
  `pendingBot`); retract a NON-culprit on a ⊥ store → still contradicted, then retract the culprit → clean
  and equal; empty-cone retract (assert refused or already undone) → near-no-op, registry shrinks.
- The workhorse: **randomized compositional differential.** Seeded RNG, small grids (e.g. 6×6 gravity T=4
  and 5×5 3-color), ~200 ops per run: random `Assert` (premise from a free pool) / `Retract` (random live
  premise, returned to pool), interleaved, ⊥ states included and continued through. After EVERY op, replay
  the surviving registry on the twin and apply the comparison rule. This catches everything the named traps
  describe, compositionally.
- Post-retract usability: after a retract, further `Assert` and a `Collapse`-driven search still behave
  (worklist clean, no dead flags).

**Benchmark (after verification passes, one process, appended as a new dated section of
[benchmarks.md](benchmarks.md) with the standard env block; fsi optimize-off numbers are ratios, not
absolutes — never compare across runs):** at 500×500, head-to-head cone-local vs the twin-replay reference
on the SAME edit: (a) small cone — gravity, two column pins, retract one (cone ≈ one column); (b) large
cone — a pin whose wave narrowed a big fraction of the grid (the honest degenerate case where cone-local ≈
full replay). Best-of-3 trials, iteration counts sized like the existing harness in `main`. The ratio on
(a) is the number that justifies this item.

**Deliverables:** the modified fsx (verifications gate timing, exit 0); the benchmarks.md section; a
**BUILT + verified** block appended to item 5 above in the style of items 1–4, recording as-built deltas
(the pending set, the oracle's flag-only-⊥ and no-exact-support-equality rules, and anything you had to
decide beyond this brief). Conventions in force: no questions in any doc; match the file's existing idiom
(qualified calls, no `[<AutoOpen>]`, comment density as-is); verify before timing, always.
