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

---

## 2026-07-10 - canonical surface core: General live edits and Optimized lowering

- **Source:** [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx), timed by the opt-in
  `--benchmark` mode in [propagator-surface-vocab.tests.fsx](../propagator-surface-vocab.tests.fsx).
- **Scenario:** the same 4x4 Sudoku and eight givens as the proof slice, encoded as portable binary
  not-equal relations. Full-solve rows include lowering/build, asserting givens, propagation, and solve
  from scratch. The live-edit row reuses one rich-lattice General net and times two assertions (the
  second contradictory) plus both retractions back to top; network construction is outside that row.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default).
- **Machine:** Intel Core i7-8750H (Coffee Lake); current power/throttle state was not independently
  measured, so same-process ratios remain the signal.
- **Harness:** full solves = best-of-3 trials x 5 iterations, 3 warmups; live edit = best-of-5 x 500,
  100 warmups; `GC.Collect` plus pending-finalizer wait between trials; all rows in one process.
- **Correctness:** before timing, both lowerings returned the same known Sudoku solution. The live row
  separately proved that contradiction remained cell-local, support stayed attached to the unaffected
  derived cell, and retract restored both cells to top on the same engine.
- **Commit:** `c5d8c9d` + working-tree surface recovery.

| row | best us/run | mean us/run | same-process comparison |
|-----|------------:|------------:|------------------------:|
| General lower + solve | 49,468.5 | 60,973.5 | 1.00x |
| **Optimized lower + solve** | **3,459.5** | **4,485.1** | **14.30x faster** |
| General live 2-assert + 2-retract cycle | 44.2 | 52.8 | edit-only row |

**Reading.** The canonical Optimized face remains mechanically isolated from General: live read/edit
closures were added only to `GeneralNet`, with no dispatch or allocation added to `ArrayCore`'s emit or
quiesce loops. In this portable binary-relation workload it is 14.30x faster than the closure/Set face
in the same process. The 44.2 us live row confirms the friendly path now edits one retained engine rather
than rebuilding a model for each read or retraction.

Do not compare these absolute full-solve numbers to the 2026-06-24 table: that run used naked/hidden-single
propagators and different sampling, while this row lowers generic binary relations through `Gac.narrow`.
The valid claims here are the in-run General/Optimized ratio and the new live-edit anchor. Earlier timing
entries remain unchanged historical evidence.

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

---

## 2026-07-10 - enduring-facade split, complete surface suite

- **Source:** [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx), exercised by the
  consolidated [propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx) `--benchmark` mode.
- **Scenario:** the complete current surface suite ran first: friendly scalar/affine/interval behavior,
  all four barometer provenance stages, finite-Set retraction, friendly Sudoku, both differential Sudoku
  slices, capability/width guards, authored-order agreement, and live General edit correctness. Timing then
  covered the same portable binary-relation 4x4 Sudoku on General and Optimized plus the retained General
  two-assert/two-retract live-edit cycle.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default).
- **Machine:** Intel Core i7-8750H (Coffee Lake). Sustained benchmark timings on this machine are highly
  variable and trend slower as a run continues, so the meaningful result is the complete-suite,
  same-process relative comparison. Absolute values are environmental observations, not cross-run
  regression evidence.
- **Harness:** full solves = best-of-3 trials x 5 iterations, 3 warmups; live edit = best-of-5 x 500,
  100 warmups; `GC.Collect` plus pending-finalizer wait between trials. Benchmark order was General,
  Optimized, then live edit.
- **Correctness:** every pre-timing regression and differential oracle passed; both lowering faces returned
  the same known Sudoku solution. Neither propagation hot loop changed in the facade/test split.
- **Commit:** `c5d8c9d` + working-tree enduring-facade/test split.

| row | best us/run | mean us/run | same-process comparison |
|-----|------------:|------------:|------------------------:|
| General lower + solve | 39,897.7 | 112,256.3 | 1.00x full-solve baseline |
| **Optimized lower + solve** | **2,777.3** | **3,061.8** | **14.37x faster by best** |
| General live 2-assert + 2-retract cycle | 30.7 | 46.6 | edit-only anchor; different workload |

**Reading.** The valid full-solve claim is within this run: Optimized completed the identical authored
model 14.37x faster than General by best time. The large General best/mean spread is consistent with the
machine's known sustained-run variability and is why these absolutes must not be compared directly with
the earlier section. The live-edit row remains useful only as a same-run edit-cost anchor; it is not a
full-solve speedup comparison.

---

## 2026-07-10 addendum - consolidated benchmark harness

The first 2026-07-10 entry above names `propagator-surface-vocab.tests.fsx`, the harness that produced
those recorded numbers. Later that day its executable tests and benchmark mode moved into the consolidated
[propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx), and the original test file was removed.
This addendum supplies the current runnable link without rewriting the historical source entry; it does not
change that entry's measurements or interpretation.

---

## 2026-07-10 - fourth-slice friendly UX refit

- **Source:** [propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx), run twice in its opt-in
  `--benchmark` mode after the friendly value/view API refit.
- **Scenario:** the complete correctness suite ran before each timing block. Timed rows remained the same
  portable binary-relation 4x4 Sudoku on General then Optimized, followed by the retained General live-edit
  cycle. The facade refit itself is outside these timed core paths.
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations **OFF** (fsi default).
- **Machine:** Intel Core i7-8750H (Coffee Lake), with no controlled power or throttle state.
- **Harness:** each complete process used three warmups then best/mean of 3 trials x 5 iterations for full
  solves, followed by 100 warmups and 5 trials x 500 iterations for live edit. Order remained General,
  Optimized, live edit.
- **Correctness:** both runs passed every facade, provenance, finite, differential, authored-order,
  capability, width, and live-retraction regression before timing. No core engine, representation, search
  loop, or propagation hot loop changed in this slice.
- **Commit:** `c5d8c9d` + working-tree fourth-slice facade refit.

| run | row | best us/run | mean us/run | same-process comparison |
|-----|-----|------------:|------------:|------------------------:|
| 1 | General lower + solve | 43,613.1 | 48,089.3 | 1.00x full-solve baseline |
| 1 | **Optimized lower + solve** | **3,942.7** | **4,249.8** | **11.06x faster by best** |
| 1 | General live 2-assert + 2-retract cycle | 43.0 | 52.8 | edit-only anchor |
| 2 | General lower + solve | 31,608.1 | 37,139.0 | 1.00x full-solve baseline |
| 2 | **Optimized lower + solve** | **4,627.1** | **5,801.9** | **6.83x faster by best** |
| 2 | General live 2-assert + 2-retract cycle | 40.0 | 51.8 | edit-only anchor |

**Reading.** Optimized remained faster than General within both complete same-process runs, but the ratio
did not reproduce the earlier 14.30x/14.37x observations. Between these two immediate runs General became
faster while Optimized became slower, demonstrating substantial ratio as well as absolute-time variability
on this machine. Because the fourth slice changed only the facade and tests, not either timed lowering or
engine loop, these measurements are recorded without attributing the spread to the UX refit.

## 2026-07-10 addendum - the fourth-slice spread explained: Power saver plan (Tier-0 verification)

Independent verification of the entry above found the machine running the Windows **"Power saver" power
plan while on AC** (97% charge). During a third benchmark run on the identical working tree, the
`\Processor Information(_Total)\% Processor Performance` counter sampled at **40-57% of base frequency**
throughout (i7-8750H, 2.2 GHz base / 4.1 GHz turbo; `Win32_Processor.CurrentClockSpeed` read 1,400 MHz) —
the CPU never reached even base clock. That throttled third run nonetheless produced *faster* absolutes
than both recorded runs: General lower+solve best 29,845.7 us, Optimized best 3,016.7 us, live edit best
27.5 us, ratio 9.89x. The day's five same-code runs now bracket as General best 29.8k-49.5k us, Optimized
best 2.8k-4.6k us, ratio 6.83x-14.37x.

Conclusion: the spread — including the depressed and unstable ratios — is clock-governor variance under an
uncontrolled throttled power plan, not the facade refit (whose timed core path is unchanged, and which this
run re-verified correct before timing). Note the same-run ratio is *not* immune here: the General and
Optimized rows execute minutes apart within one process, each at whatever frequency the governor happens to
hold, so under Power saver even within-run ratios move by 2x. Convention extension for future entries:
record the active power plan (`powercfg /getactivescheme`) in the environment block, and prefer a
high-performance plan on AC for recorded runs.

---

## 2026-07-10 - generalized finite face and WFC application

- **Sources:** [propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx),
  [propagator-wfc-app.fsx](../propagator-wfc-app.fsx), and the preserved
  [propagator-wfc.fsx](../propagator-wfc.fsx).
- **Runtime:** .NET 9.0.12, `dotnet fsi`, optimizations off.
- **Environment:** Windows Power saver plan
  (`dac69671-8e57-4927-a427-73e20346dcc1`). Absolute values remain environmental observations.
- **Correctness gate:** all pre-existing facade, rich-lattice, provenance, authored-order, sparse-DDB,
  and capability rows passed, followed by grouped/expanded equivalence, 65/128/130-value domains,
  asymmetric binary tables, SplitMix64 golden output, cross-face seeded generation, snapshot replay and
  disposal, restart exhaustion, optimized live support/retraction, and the relocated premise guard.
- **Preservation:** historical WFC SHA-256 before and after was
  `836A49E4E511E23BE0A05A598403EBFE2E9512B1C1F82583B1FA8498FD357621`.

The established same-process canary retained its order and included lowering plus solve. Baseline was
captured before hot-loop changes in this same work session; final numbers are from the completed tree.

| state | row | best us/run | mean us/run | same-process comparison |
|---|---|---:|---:|---:|
| before | General lower + solve | 40,537.6 | 45,214.1 | 1.00x |
| before | Optimized lower + solve | 4,279.3 | 4,774.2 | 9.47x faster by best |
| before | General live edit cycle | 42.8 | 52.6 | edit-only anchor |
| final | General lower + solve | 32,651.1 | 36,149.4 | 1.00x |
| final | Optimized lower + solve | 1,001.6 | 2,506.6 | 32.60x faster by best |
| final | General live edit cycle | 27.9 | 38.4 | edit-only anchor |

The new WFC ladder authors two grouped directional relations over a three-tile cyclic rule. Each row
includes deferred lowering, generation, and immutable `Solution Map` materialization, then independently
walks every adjacency. It is a single correctness-gated process, not repeated statistical sampling.

| new Friendly WFC grid | elapsed ms | valid |
|---|---:|---:|
| 16x16 | 10.015 | yes |
| 32x32 | 13.456 | yes |
| 64x64 | 76.284 | yes |
| 128x128 | 152.815 | yes |
| 500x500 | 6,672.822 | yes |

The first 500x500 attempt exposed an application-scale authoring bug: optimized structural validation
linearly scanned all cells for every relation endpoint. A cell-id set removed that accidental quadratic
cost. Before the fix, two controlled attempts failed to complete within six and five minutes; after it,
the complete ladder and 500x500 row passed. The change-driven picker also tracks unresolved and bottom
counts, avoiding a drain of stale heap entries after a propagation wave.

The preserved historical script then ran as a second process with `TRIALS=1` (its retract helper retains
hard-coded best-of-3 sampling). All historical correctness rows passed. Selected results:

| preserved historical workload | best ms/run |
|---|---:|
| ramp512 corner 500x500 | 6,628.095 |
| ramp128 center 500x500 | 138.122 |
| gravity generation 500x500 | 363.597 |
| 3-color generation 500x500 | 54,743.329 |
| ramp32 generation 64x64 | 135.968 |

These are consecutive-process historical comparisons, not same-process rows. The new cyclic directional
map, historical gravity, 3-color, and ramp workloads have different propagation and search shapes; no
cross-row speedup is claimed. Their value is boundary evidence: the historical prototype remains intact,
while the new application reaches a valid 250,000-cell map through Friendly with no local propagator
engine or hidden representation API.

---

## 2026-07-11 addendum - matching gravity and 3-color WFC constraints

The preceding entry correctly refused to compare unlike cyclic, gravity, and 3-color workloads, but left
the comparative proof incomplete. The new application's `--benchmark-like-for-like` mode now reproduces
the historical gravity and 3-color constraint semantics at 500x500 with seed 42.

For gravity, `Sea` represents historical Air and `Coast` represents Ground. East/west is unconstrained;
the authored south relation is `upper = Air || lower = Ground`, and its compiled reverse arc supplies the
corresponding north propagation. The 3-color relation is inequality on every horizontal and vertical edge.
Both routes retain one already-built network, perform a validity spot check, warm up once, time generation,
and validate the final materialized map outside the stopwatch. The Friendly run used two measured trials;
gravity used two iterations per trial and 3-color one, matching the historical per-trial iteration counts.

| 500x500, seed 42 | preserved best ms | Friendly best ms | Friendly mean ms | observed best ratio |
|---|---:|---:|---:|---:|
| gravity generation | 363.597 | 5,959.679 | 6,174.909 | preserved 16.39x faster |
| 3-color generation | 54,743.329 | 4,958.214 | 5,155.247 | Friendly 11.04x faster |

All maps passed an independent adjacency walk. The comparison is constraint-, dimension-, and seed-matched,
but not mechanically identical: the preserved path materializes `int[]`, uses `System.Random` candidate
shuffles and trail-backed DFS, and was recorded with one measured trial; Friendly materializes the public
immutable `Map`, uses stable SplitMix uniform choices with bounded restart, and reports two measured trials.
The runs were separate Power-saver processes on different dates. Therefore the ratios describe these
observations and the strong workload reversal; they are not stable cross-machine speedup claims.

---

## 2026-07-11 addendum - gravity optimization execution

The accepted work order was executed under .NET 9.0.12 on the Power saver plan. Every full checkpoint ran
the complete correctness suite before timing. The locked seed-42 outputs remained:

- gravity: `632233C0779601913FB945173CCFDD1861FA5769185DD4114BE0FA4056D7A8B2`;
- 3-color: `7A20D499A9E3BFC64249653EDC76CA6F7C0BE778A8F370A455910E1CBE5945FF`.

Gravity retained 1,029 draws and zero restarts. Historical WFC remained byte-identical at
`836A49E4E511E23BE0A05A598403EBFE2E9512B1C1F82583B1FA8498FD357621`.

### Measurement boundary

This machine slows substantially during long sessions. Benchmark order was therefore fixed and each WFC
row was interpreted beside the controls in that process. The tables below are sequential checkpoints, not
strict final-tree ablations: retained candidates were not independently toggled off and rerun from the final
tree. Candidate attribution comes from structural counts, phase timers, and allocation counts together with
the checkpoint series. Instrumented profile wall times are excluded because per-union counters intentionally
made those runs much slower.

The current-surface timings with established precedent are the portable binary-relation 4x4 Sudoku with
eight givens through General and Optimized, and the General two-assert/two-retract live-edit cycle. The
Friendly, sparse, and generic-representation Sudokus and the scalar, interval, affine, barometer, provenance,
observer, and guard fixtures were correctness gates, not individually timed rows. Older Part-1 and
mutable-core Sudoku tables belong to separate unchanged historical implementations and were not rerun.

### Current finite timing continuity

All values are best / mean microseconds per run. Each process timed General Sudoku, Optimized identical
Sudoku, then General live edit.

| checkpoint | General Sudoku | Optimized Sudoku | General live edit | General/Optimized best ratio |
|---|---:|---:|---:|---:|
| immediate pre-session record | 32,651.1 / 36,149.4 | 1,001.6 / 2,506.6 | 27.9 / 38.4 | 32.60x |
| Phase 0 baseline | 33,726.2 / 39,027.3 | 1,369.2 / 2,439.1 | 36.5 / 48.6 | 24.63x |
| after universal-direction elision | 38,038.4 / 41,318.1 | 737.8 / 2,390.5 | 36.6 / 50.3 | 51.56x |
| after settled-baseline restoration | 33,107.4 / 43,872.4 | 717.4 / 2,150.7 | 38.3 / 44.5 | 46.15x |
| after scalar binary arcs | 44,864.3 / 52,817.8 | 1,036.3 / 1,941.4 | 29.5 / 43.4 | 43.29x |
| final, profiler removed | 31,483.6 / 195,034.4 | 619.8 / 1,491.8 | 32.1 / 43.8 | 50.80x |

Against the immediate pre-session record, final General Sudoku best improved 3.6%, Optimized Sudoku best
improved 38.1% (mean improved 40.5%), and the same-process best ratio rose 32.60x -> 50.80x. General live
edit was 15.1% slower by best and 14.1% slower by mean than that earlier record, but faster than this
session's Phase 0 row; it remains within the machine's observed spread. The final General mean contains a
large late-run outlier and is not treated as a regression signal because General's implementation did not
change and its best remained stable. No individual timing precedent exists for the other correctness-only
examples, so no claim is made for their isolated runtimes.

### Cyclic WFC ladder

Each row includes lowering, generation, immutable `Map` materialization, and an independent adjacency walk
outside timing. Values are milliseconds.

| grid | Phase 0 | after universal elision | after baseline cache | after scalar arcs | final late run |
|---|---:|---:|---:|---:|---:|
| 16x16 | 6.843 | 3.961 | 8.954 | 3.923 | 4.956 |
| 32x32 | 16.780 | 20.616 | 21.445 | 12.298 | 19.058 |
| 64x64 | 100.483 | 114.013 | 101.481 | 76.795 | 86.408 |
| 128x128 | 275.312 | 2,789.524 | 1,026.795 | 212.869 | 494.593 |
| 500x500 | 12,011.427 | 12,463.005 | 8,099.838 | 3,842.648 | 12,087.915 |

The 128x128 and final 500x500 spikes demonstrate why isolated cross-process absolutes were not used to
accept or reject a candidate. The scalar-arc checkpoint improved every ladder row relative to Phase 0; the
late final process then slowed all large WFC controls together despite removing the profiler and retaining
the same outputs. There is no historical cyclic workload, so none of these rows is compared to historical
gravity, 3-color, or ramp under another name.

### Matching 500x500 WFC timing

Values are best / mean milliseconds. The usual runs used two trials; gravity used two iterations per trial
and 3-color one. Candidate 0's fingerprint check used one trial and is listed separately.

| checkpoint | gravity | 3-color | evidence |
|---|---:|---:|---|
| Phase 0 baseline | 4,544.909 / 4,929.444 | 6,203.318 / 6,526.135 | 2 tables, 998,000 arcs; exact hashes |
| Candidate 0 one-trial check | 4,704.256 / 4,704.256 | 5,427.033 / 5,427.033 | lookup-only change; exact hashes |
| after universal-direction elision | 4,078.417 / 5,093.959 | 7,878.721 / 8,062.427 | gravity 1 table, 499,000 arcs; exact draw/restart sequence |
| after settled-baseline restoration | 4,078.752 / 4,141.916 | 6,044.318 / 7,898.973 | repeat rebuild removed |
| after scalar binary arcs | 1,057.193 / 1,196.598 | 1,228.796 / 1,311.783 | zero one-word arc arrays |
| final, profiler removed | 3,569.262 / 4,445.822 | 3,941.998 / 4,584.026 | exact hashes; late-session drift |

The applicable historical and pre-session values are embedded here so the comparison does not depend on
earlier sections. Ratios use best times and the completed historical rerun from this session.

| workload | prior recorded historical | completed historical rerun | immediate pre-session Friendly | Phase 0 | strongest retained checkpoint | final late Friendly | direct reading |
|---|---:|---:|---:|---:|---:|---:|---|
| gravity 500x500 | 363.597 ms | 292.197 ms | 5,959.679 ms | 4,544.909 ms | 1,057.193 ms | 3,569.262 ms | Historical rerun is 3.62x faster than the strongest Friendly checkpoint and 12.21x faster than the final late sample. Friendly nevertheless improved 5.64x at the strongest checkpoint and 1.67x in the final late sample versus its immediate pre-session record. |
| 3-color 500x500 | 54,743.329 ms | 55,390.970 ms | 4,958.214 ms | 6,203.318 ms | 1,228.796 ms | 3,941.998 ms | Friendly is 45.08x faster than the historical rerun at the strongest checkpoint and 14.05x faster in the final late sample. It improved 4.03x at the strongest checkpoint and 1.26x in the final late sample versus its immediate pre-session record. |

The workload reversal therefore remains explicit: the specialized historical implementation is still much
faster on gravity, while the generalized Friendly implementation is much faster on 3-color. The strongest
checkpoint shows the mechanical optimization's attainable result; the final late sample shows the machine's
sustained-run degradation. Neither is silently substituted for the other.

### Candidate evidence and decision

| candidate | direct before/after evidence | outcome |
|---|---|---|
| Constant-time cell lookup | Linear `Seq.tryFindIndex` removed; 100,000-cell aggregate read sweep completed in 101.539 ms. Generation fingerprints unchanged. | Retained. |
| WFC-local universal direction | Gravity tables 2 -> 1; arcs and watches 998,000 -> 499,000; explicit and elided complete maps equal; draws 1,029 -> 1,029; restarts 0 -> 0. | Retained. |
| Settled baseline | Comparable repeat-gravity reset 266.907 -> 21.648 ms initially and 29.172 ms at the combined checkpoint; reset allocation -> 0; 499,000 reset arcs removed. Fresh-twin assertion, retraction, replacement, observer, and failed-restart tests passed. | Retained. |
| Scalar one-word arc path | Repeat-gravity `fireArc` arrays 998,000 after elision (1,996,000 in original Phase 0 repeat) -> 0. Multiword 65/128/130 and asymmetric/full-row/empty-source behavior unchanged. | Retained. |
| Picker replacement | Gravity initialization about 15 ms, only 4,711 stale dequeues, and 1,029 guesses. No candidate was implemented; bulk/custom heap complexity lacked evidence. | Skipped. |
| Direct singleton extraction | Gravity extraction 260.486 -> 57.601 ms; measured output allocation about 298.5 -> 270.5 MB; immutable `Map` unchanged. | Retained. |
| Arc/watch layout | Propagation no longer dominated after scalar arcs; immutable `Map` construction was the principal residual. | Skipped. |

### Failures and completed historical run

Two additions initially failed to compile: the baseline-refresh test needed an explicit Friendly network type,
and the full-row helper needed an explicit `uint64[]` parameter. Both were type-inference corrections; after
the annotations, the relevant correctness suites passed. No retained candidate produced a fingerprint,
support, observer, restart, multiword, or adjacency mismatch.

The first final historical process reached the five-minute command timeout after 301.9 seconds. It had passed
correctness and printed propagation/retraction timing but had not reached generation, so it was treated as an
incomplete run. The rerun used a larger timeout and completed in 444.6 seconds:

| preserved historical row | best | mean where reported |
|---|---:|---:|
| reset-only W=8 | 9.780 ms | 9.780 ms |
| ramp512 corner | 5,722.003 ms | 5,722.003 ms |
| ramp128 center | 137.628 ms | 137.628 ms |
| reset-only W=1 | 2.463 ms | 2.463 ms |
| gravity column | 2.635 ms | 2.635 ms |
| gravity-small cone retract | 1.961 ms | 2.573 ms |
| gravity-small full replay | 1.334 ms | 1.689 ms |
| ramp512-large cone retract | 7,658.648 ms | 8,900.784 ms |
| ramp512-large full replay | 5,393.498 ms | 5,554.622 ms |
| gravity generation 500x500 | 292.197 ms | 292.197 ms |
| 3-color generation 500x500 | 55,390.970 ms | 55,390.970 ms |
| ramp32 generation 64x64 | 161.740 ms | 161.740 ms |

Against the adjacent final Friendly observation, the specialized historical route remained 12.21x faster
on gravity while Friendly remained 14.05x faster on 3-color. These remain separate-process observations with
different result, RNG, and search-policy contracts. Historical correctness passed, its SHA-256 remained
exact, and its git diff was empty. Ramp appears only in this preserved table because no equivalent ramp
workload was timed through the changed Friendly/optimized core; generic multiword correctness is not presented
as a like-for-like ramp performance comparison.

### Final decision

Retain the lookup dictionary, WFC-local universal-direction check, one-engine settled candidate/support
snapshot, scalar one-word arc path, full-row flags, and direct singleton extraction. Leave the picker and
layout candidates unimplemented. Stop at the immutable `Map` boundary rather than introducing WFC machinery,
a second finite engine, or a new result type. Remove all temporary profiling code, then keep both exact
fingerprints and the complete current/historical correctness suites as the continuing regression locks.

## 2026-07-11 addendum - ramp workloads through Friendly/Optimized

- **Source and boundary:** `propagator-wfc-app.fsx --benchmark-ramp`. The app-local `Grid<'tile>` authors
  integer ramp tiles through the unchanged Friendly operations. Network construction and relation-table
  compilation are outside every stopwatch. Propagation rows reassert the same named premise, which invokes
  the optimized rebuild path and measures restore + assert + propagation. Retraction setup is also outside
  the stopwatch. Generation includes the public immutable `Map`, as do the existing Friendly WFC rows.
- **Correctness gate:** the ordinary app run now checks the historical 1x100 single-pin domains, the 64-bit
  seam, two-ended forced chain, OR-on-narrow premise support, assert-site bottom, and retract equality with a
  freshly authored surviving-premise twin. The large harness checks the exact historical 500x500 corner and
  center counts, retraction against a fresh twin, and every edge of the generated 64x64 ramp32 map. Ramp32's
  stable Friendly fingerprint is
  `3AD38489B2BB9D6165F761FAF7881C838EF338286F031B60C6419A6555C2FDAD`.
- **Interpretation:** the rows below are sequential one-trial checkpoints on the variable Power saver
  machine, not independent final-tree ablations. The structural-baseline result is large enough to survive
  that limitation; scratch and set-bit attribution relies primarily on the removed operation/allocation
  shape and the full semantic suite.

| current-core checkpoint | ramp512 corner | ramp128 center | ramp512 retract | ramp32 generation |
|---|---:|---:|---:|---:|
| initial like-for-like harness | 19,614.021 ms | 2,563.402 ms | 23,636.274 ms | 186.608 ms |
| invocation-local multiword scratch | 17,427.153 ms | 2,898.273 ms | 22,929.159 ms | 238.842 ms |
| plus set-bit source iteration | 19,794.289 ms | 1,738.741 ms | 24,938.788 ms | 407.618 ms |
| plus structural-fixpoint rebuild baseline | 6,256.278 ms | 156.072 ms | 6,010.373 ms | 332.095 ms |
| final fixed-order control process | 6,469.697 ms | 182.009 ms | 6,822.673 ms | 305.038 ms |

The initial diagnosis found two separate costs. Multiword `fireArc` allocated one `uint64[]` per firing and
tested every authored value even when few bits remained. Invocation-local scratch removes those arrays while
remaining safe under nested synchronous observer propagation; word-wise set-bit iteration removes absent
value probes. More importantly, every assertion replacement and retraction rebuilt from top and enqueued all
998,000 relation arcs. Ramp128 therefore traversed the whole 500x500 graph for a radius-127 wave. The retained
rebuild now restores the already-computed, immutable structural fixpoint and lets authored assertions enqueue
only cells they actually narrow. Arbitrary relations that prune top remain correct because that initial
constraint-only fixpoint, rather than raw top, is what is restored.

The directly comparable historical values are repeated here. Ratios use the final fixed-order current row
and the completed historical rerun recorded immediately above.

| workload | historical rerun | final Friendly/Optimized | direct comparison |
|---|---:|---:|---|
| ramp512 corner 500x500 | 5,722.003 ms | 6,469.697 ms | Friendly is 1.13x slower. |
| ramp128 center 500x500 | 137.628 ms | 182.009 ms | Friendly is 1.32x slower. |
| ramp512 opposite-corner retract | 7,658.648 ms cone / 5,393.498 ms replay | 6,822.673 ms | Friendly is 1.12x faster than historical cone-local and 1.27x slower than historical replay; current Optimized retraction is structural-baseline replay, not cone-local machinery. |
| ramp32 generation 64x64 | 161.740 ms | 305.038 ms | Friendly is 1.89x slower; result materialization and seeded search contracts still differ. |

The same final process ran all current WFC controls before ramp. Cyclic 16/32/64/128/500 measured
4.456/14.578/59.317/254.079/4,167.414 ms. Matching gravity measured 1,338.347 ms and 3-color 1,324.329 ms,
with exact fingerprints `632233C0779601913FB945173CCFDD1861FA5769185DD4114BE0FA4056D7A8B2` and
`7A20D499A9E3BFC64249653EDC76CA6F7C0BE778A8F370A455910E1CBE5945FF`. The adjacent finite control process
measured General Sudoku 38,748.9 / 41,028.5 us, Optimized Sudoku 994.7 / 1,028.7 us, and General live edit
31.9 / 37.0 us, preserving a 38.95x same-run Sudoku ratio. These controls put the final ramp rows near the
strongest earlier WFC checkpoint rather than the late 12-second drift sample.
