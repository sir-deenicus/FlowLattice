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
