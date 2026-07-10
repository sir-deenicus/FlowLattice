# Benchmarks

How we track engine performance as it evolves. Each run is a **dated section**: an environment
block (numbers are only comparable *within* the same conditions) followed by a results table.

**Metric.** Microseconds per *full solve* — build engine + assert givens + propagate to
quiescence, from scratch each iteration. `best` = fastest trial's mean (least GC noise); `mean` =
average across trials.

**Comparability caveat.** `dotnet fsi` defaults optimizations **OFF**. Absolute µs are
conservative; the trustworthy signal is the *ratio between representations within one run*. Never
compare an fsi number against a future `--optimize+` or compiled-Release/BenchmarkDotNet number —
that is why every run records its runtime + flags + machine + harness.

---

## 2026-06-23 — Part 1 engine: `Set<int>` vs `uint16` bitset (baseline)

- **Source:** [tutorial-propagation-part1.fsx](../tutorial-propagation-part1.fsx) (extracted from [tutorial-propagation-part1.md](tutorial-propagation-part1.md))
- **Scenario:** 4×4 Sudoku, 8 givens, naked + hidden singles, no guessing
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default)
- **Machine:** Intel Core i7-8750H (Coffee Lake), throttled to 65% — absolute µs run low/variable; the ratio is the signal
- **Harness:** best-of-5 trials × 8000 iters, 2000-iter warmup, `GC.Collect` between trials
- **Commit:** `62063e7`

| representation       | best µs/solve | mean µs/solve | vs `Set` |
|----------------------|--------------:|--------------:|---------:|
| `Set<int>` (clear)   |        6968.5 |        7663.0 |    1.00× |
| `uint16` (bitset)    |        1746.9 |        2328.0 | **3.99× faster** |

**Reading.** The bitset rep is ~4× faster even on a 4×4. But both are *slow in absolute terms*
for so tiny a puzzle — dominated by the **engine around** the representation (repeated quiescence;
`Set<Premise>` stamps and per-cell `Dictionary` allocation), the Part 1 §11 "reads better than it
runs" loose end. This row is the baseline the **mutable-core** work
([mutable-core-plan.md](mutable-core-plan.md)) should beat: array-backed cells + a tight worklist,
without changing a single law.

---

## 2026-06-24 — array-backed mutable core vs Part-1 baseline (head-to-head, one process)

- **Source:** [propagator-mutable-core.fsx](../propagator-mutable-core.fsx) — carries BOTH engines: the Part-1 engine copied verbatim (baseline) and the new `module M` array-backed engine.
- **Scenario:** the identical 4×4 Sudoku, 8 givens, naked + hidden singles, no guessing — each engine in `Set<int>` and `uint16` reps (4 solvers).
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default)
- **Machine:** Intel Core i7-8750H (Coffee Lake), throttled to 65% — absolute µs run low/variable; the ratio is the signal
- **Harness:** best-of-5 trials × 2000 iters, 2000-iter warmup, `GC.Collect` between trials. All four timed in **one process** (so the comparison is in-run, per the caveat above).
- **Correctness:** all four solvers matched the known solution and M's C↔F retraction behaved, verified **before** timing (the harness refuses to time a wrong solver).
- **Commit:** `6c7969b` + the new (untracked) `propagator-mutable-core.fsx`

| representation        | best µs/solve | mean µs/solve | vs same-rep baseline |
|-----------------------|--------------:|--------------:|---------------------:|
| baseline `Set<int>`   |        5334.6 |        6070.9 |                1.00× |
| baseline `uint16`     |        1872.1 |        2085.6 |                1.00× |
| **M `Set<int>`**      |     **857.3** |         943.8 |     **6.22× faster** |
| **M `uint16`**        |     **127.0** |         137.0 |    **14.74× faster** |

Best config (M `uint16`, 127.0 µs) is **41.99×** the original baseline `Set<int>` in this same run.

**Reading.** The mutable core beats the Part-1 engine at *both* representations **without changing a law** (same `meet`/`top`, same `Assert`/`Retract`, same propagate-to-quiescence; the four-solver differential + the ported C↔F retraction all pass). What changed is the machinery the Part-1 §11 "reads better than it runs" note flagged: cells became `int` indices into structure-of-arrays (no per-cell record, **no per-cell `Dictionary`**); support became a `uint64` premise bitmask (union = `|||`, **no `Set<Premise>` allocation**); the worklist gained a `queued` dedup flag (each propagator enqueued at most once, killing the **repeated quiescence**); propagators `emit` through one reused callback (**no per-fire list allocation**); and change-detection uses the lattice's own `equals`.

The `uint16` speedup (**14.74×**) is much larger than the `Set` speedup (**6.22×**), and that gap is the most informative number here: for `Set<int>` the *value* is still a heap-allocated immutable set, so it caps the win; for `uint16` the value is a register, so removing the surrounding allocation — and in particular the baseline's **generic `<>` boxing the `uint16`** (the change-detector ran under `when 'a : equality`), now a primitive compare via the lattice's `equals` — exposes nearly the full machinery savings. This is the array-backed-cells-plus-tight-worklist win the 2026-06-23 baseline reading predicted.

> Cross-run note: this run's *baseline* numbers (Set 5334.6 / bit 1872.1 best µs) differ from the 2026-06-23 row (6968.5 / 1746.9) — a different session under the same throttle, exactly why we never compare across runs. The trustworthy comparison is M-vs-baseline measured **in the same process**, above.

---

## 2026-07-03 — WFC store + tiled adjacency propagator, first 500×500 measurements

- **Source:** [propagator-wfc.fsx](../propagator-wfc.fsx) — the specialized WFC store (work items 1+2 of [mutable-core-plan.md](mutable-core-plan.md)): cell-major multiword bitset store (runtime `W = ⌈tiles/64⌉`), worklist of cell ids, adjacency propagator `B ∩= ⋁_{t∈A} allowed[t][dir]`, ⊥ early-exit in quiescence.
- **Scenario:** 500×500 grid (250k cells). Each run = `Reset` (refill ⊤) + one `Assert` + propagate to quiescence. Ramp tileset (`|a−b| ≤ 1`): a corner/center pin launches a dense-⊤ wave — the **worst case** for the union loop (cost ∝ popcount of the firing cell); gravity tileset (T=2): a bottom pin forces one 499-cell column — the local-wave case. **No baseline row exists** (nothing prior does WFC); these are first measurements to compare future variants against *in-process*.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default)
- **Machine:** Intel Core i7-8750H (Coffee Lake), throttled to 65% — absolute ms run low/variable; treat rows as first anchors, not truth
- **Harness:** best-of-3 trials × per-row iters (1–20), small warmup, `GC.Collect` between trials; all rows in **one process**
- **Correctness:** verified before timing — ramp closed-form domains (incl. the 64-bit word seam at T=100), OR-on-narrow supports, assert-site ⊥, mid-propagation ⊥ with early-exit + clean worklist + `Reset` reuse, direction-asymmetric gravity wiring; plus 500×500 spot checks per bench engine. The harness refuses to time a wrong engine.
- **Commit:** `6c7969b` + the new (untracked) `propagator-wfc.fsx`

| row | T (tiles) | W | cells narrowed | best ms/run | mean ms/run |
|-----|----------:|--:|---------------:|------------:|------------:|
| reset-only        | 512 | 8 | — (refill 2M words)  |    6.7 |    7.7 |
| ramp512 corner    | 512 | 8 | ~131k, dense domains | 5324.7 | 5684.7 |
| ramp128 center    | 128 | 2 | ~32k, dense domains  |  134.1 |  138.0 |
| reset-only        |   2 | 1 | — (refill 250k words)|    1.8 |    2.2 |
| gravity column    |   2 | 1 | 499 (one column)     |    1.9 |    2.0 |

**Reading.** The engine's cost is proportional to the **wave**, not the grid: the gravity row minus its reset-only row puts a 499-cell forced column at **~0.12 ms** — the locality that interactive editing (work item 5) banks on. The ramp rows are the deliberate dense-⊤ worst case: every firing cell still carries hundreds of live tiles, so the per-fire union loop does `popcount × W` word-ORs, and at T=512 that compounds to ~5.3 s for the 131k-cell wave (~1.4G word-ORs under fsi optimize-off). Real WFC firing cells are near-collapsed (the plan's "cheap when A is near-collapsed"), so driver-era numbers should sit far below the ramp rows — but if dense-domain waves show up hot once the min-entropy driver (item 4) runs, this is the trigger the plan set for benchmarking the **AC-4 support-counter** fallback, in-process against these same scenarios.

## 2026-07-03 (second run) — trail + search driver: first full-map generations

- **Source:** [propagator-wfc.fsx](../propagator-wfc.fsx) — items 3+4 of [mutable-core-plan.md](mutable-core-plan.md): engine grew the trail (journal `(cell, oldWords, oldSupport)` per narrow, `Mark`/`UndoTo`, premise-free `Collapse`, `OnChange` hook); search driver = `module WfcSearch` on **Hansei.TreeSearch** (`#r Hansei.Core.dll`) — lazy-LazyList list-of-successes, min-entropy via a validate-on-pop binary heap subscribed to `OnChange`, seeded shuffle tile order.
- **Scenario:** top group = the same store/propagator rows as the 2026-07-03 section, re-measured **in this process** (the engine now journals every narrow, so those rows include trail cost — compare them only against the generation rows below, never against the earlier section). Bottom group = full-map generation: `Reset` + heap rebuild + lazily force the FIRST solution (`LazyList.tryHead`), seeded rng ⇒ identical work per iteration, forcing on a big-stack thread.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default)
- **Machine:** Intel Core i7-8750H (Coffee Lake), throttled to 65% — ratios are the signal
- **Harness:** best-of-3 trials × per-row iters (1–20), warmup, `GC.Collect` between trials; all rows one process
- **Correctness:** all 10 scenarios verified before timing — the items-1+2 five plus: trail snapshot-exact restore (words AND supports, nested marks, ⊥ unwind to a usable store), staircase 1×3/1×4 closed-form solution AND failure counts (1 sol + exactly 2 failed guesses / unsat + exactly 3), gravity 3×3 exhaustive = 4³ = 64 distinct valid solutions under the heap picker with exactly 0 failures (paths ⇒ AC complete), checkerboard 3×3 = 2, LazyList memoized re-force = same array reference. Generation spot-checked valid at full size before timing.
- **Commit:** `6c7969b` + the untracked `propagator-wfc.fsx`

| row | scenario | best ms/run | mean ms/run |
|-----|----------|------------:|------------:|
| reset-only W=8      | store refill, T=512               |    5.0 |    5.6 |
| ramp512 corner      | dense-⊤ wave, ~131k cells         | 5213.7 | 5904.5 |
| ramp128 center      | dense-⊤ wave, ~32k cells          |  151.4 |  168.6 |
| reset-only W=1      | store refill, T=2                 |    2.2 |    2.5 |
| gravity column      | 499-cell local wave               |    1.8 |    2.5 |
| **gravity gen 500×500** | full map, T=2, ~few k guesses |  327.3 |  356.7 |
| **3color gen 500×500**  | full map, T=3, ~250k guesses  | 44173.2 | 47269.1 |
| **ramp32 gen 64×64**    | full map, T=32, 4k cells      |  108.1 |  132.5 |

**Reading.** A full 500×500 gravity map generates in ~0.33 s — collapse waves force whole column
segments, so only a few thousand guesses happen and the row is dominated by `Reset` + the 250k-push
heap rebuild. The 3-color row is the driver-overhead anchor: nothing forces cells (every collapse
narrows neighbors to entropy 2, still undecided), so ~250k genuine guesses run at **~180 µs/guess**
under fsi optimize-off — that cost is driver-side (heap churn: one push per narrow + validate-on-pop;
LazyList/CE node allocation per guess), NOT engine waves, and it's the row to beat if the driver ever
needs tightening. Failed guesses were 0 on all three generation tilesets (AC-friendly rules; the
staircase tests cover the unwind path with exact counts), and the dense-⊤ AC-4 trigger never fired
during generation — firing cells are near-collapsed once the driver is placing tiles, as the plan
predicted.

---

## 2026-07-05 — cone-local `Retract` vs full-replay twin

- **Source:** [propagator-wfc.fsx](../propagator-wfc.fsx) — item 5 of [mutable-core-plan.md](mutable-core-plan.md): accepted authored asserts are registered, bot-aborted work is preserved in `pendingBot`, and `Retract` resets only the premise support cone before re-firing cone cells plus structural neighbors.
- **Scenario:** 500×500 grid. The retract rows time the edit only: each iteration first rebuilds the same pre-retract state outside the stopwatch, then times either cone-local `Retract` or a twin reference that resets and replays the surviving authored registry. Small cone = gravity, two column pins, retract one. Large cone = ramp512 opposite-corner pins, retract one whose wave narrowed a large fraction of the map.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default)
- **Machine:** Intel Core i7-8750H (Coffee Lake), throttled to 65% — ratios are the signal
- **Harness:** best-of-3 trials × 1 edit for retract rows, one process, `GC.Collect` between trials. Existing reset/assert and generation rows also ran in the same process.
- **Correctness:** all prior store/search verifications plus item-5 hand/oracle cases, `pendingBot` half-wave completion, seeded randomized compositional differential (6×6 gravity4 and 5×5 3-color, 200 ops each), and post-retract Assert + search usability passed before timing.
- **Commit:** `42058ba` + working tree changes to `propagator-wfc.fsx`

| edit row | scenario | cone-local best ms/edit | cone-local mean | replay best ms/edit | replay mean | replay/cone best |
|----------|----------|------------------------:|----------------:|--------------------:|------------:|-----------------:|
| gravity-small | T=2, one-column cone | 1.991 | 2.243 | 3.351 | 3.883 | **1.68×** |
| ramp512-large | T=512, dense large cone | 9920.649 | 10033.978 | 5641.265 | 6334.822 | 0.57× |

Same-process anchor rows from this run:

| row | best ms/run | mean ms/run |
|-----|------------:|------------:|
| reset-only W=8 | 6.971 | 9.259 |
| ramp512 corner | 5378.547 | 5891.034 |
| ramp128 center | 139.695 | 144.847 |
| reset-only W=1 | 1.553 | 1.923 |
| gravity column | 1.658 | 1.813 |
| gravity gen 500×500 | 313.082 | 333.368 |
| 3color gen 500×500 | 36899.853 | 39603.271 |
| ramp32 gen 64×64 | 102.040 | 166.986 |

**Reading.** The locality win shows up on the authored-edit case item 5 was built for: a one-column gravity cone beats full replay by **1.68×** even in fsi optimize-off, with only one surviving pin to replay. The large dense ramp case is the honest degenerate: cone-local must reset and re-fire a huge dense-domain frontier, so full replay of the surviving opposite-corner pin is faster in this harness. That is acceptable for the design: the interactive-edit payoff is small cones, while large cones collapse back toward replay-scale cost.

<!-- Template for the next run — copy the section above, update the env block, add rows.
## YYYY-MM-DD — <what changed>
- **Source:** …
- **Scenario:** …
- **Runtime:** … (state optimize +/-)
- **Machine:** CPU + any throttle/power cap
- **Harness:** best-of-N × M iters, warmup, GC policy
- **Commit:** `sha`

| representation | best µs/solve | mean µs/solve | vs baseline |
-->
