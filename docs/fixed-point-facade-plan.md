# Fixed-Point Facade over the Interval Lattice - Work Order

Status: decision ratified by Deen 2026-07-11 ("Let's go with Fixed point"), architecture chosen by Fable
(Tier 0) under delegated judgment: **facade over intervals, not a standalone type**. This resolves the
2026-06-23 float-robustness open question. Execution is Opus/Codex-level; Fable reviews the result.

## 1. The decision and why

Fixed point is implemented as a **quantized face over the existing float-endpoint, outward-rounded interval
lattice** (`propagator-number-types.fsx` §4). It is not a new lattice type.

- A standalone fixed-point type with exact equality re-imports the bug being solved: rounding to grid after
  each operation still lets the contracting round-trip (F -> C -> F, error up to ~0.9 quanta) land one grid
  cell off and fabricate a spurious bottom. The only sound standalone version carries its rounding
  uncertainty internally - at which point it is an interval implementation duplicated. That is duplication
  of meaning, not a second dialect; consolidate.
- The facade inherits the interval lattice's already-verified properties for free: meet = intersection is
  exact, bottom = empty interval is a real contradiction, intersection-only propagation narrows
  monotonically and terminates.
- The unifying observation: outward rounding to the float grid (`Math.BitDecrement`/`BitIncrement`) and
  outward rounding to a user grid are the same construction - intervals with endpoints confined to a grid.
  Fixed point contributes a *choice of grid* (uniform, user-meaningful), not an alternative arithmetic.
- Same architectural pattern as Friendly-over-core: a syntax-and-presentation face over one canonical
  authority.

Consequences for the open question's other candidates: no ULP/tolerance patch to float change-detection
(fuzzy equality is non-transitive and breaks the tutorial §10 confluence theorem); BigRational remains the
choice for rational-structured exact data; the scalar exact-equality lattice remains exact-data-only.

## 2. Locked rules

1. **Internal arithmetic is the existing interval lattice, unchanged.** Never coarsen intervals to the user
   grid mid-propagation - each coarsening widens by up to half a quantum and long chains blur. Quantization
   lives only at the boundary: constructors and display/read.
2. **Constructors:** asserting a grid literal produces the minimal enclosing interval of the stated value
   (the true decimal, not its float approximation).
3. **Display rule:** an interval renders as the single grid value `g` iff it fits inside `g`'s cell
   (executor picks and documents the exact boundary convention, e.g. `[g - q/2, g + q/2)`); otherwise it
   renders honestly as an interval. No silent snapping of wide intervals - a genuinely wide or conflicting
   result must be visible as such.
4. **Quantum is a modeling parameter the user supplies** (a value like `0.1`), passed to the facade's
   constructors and display functions. Do not invent per-cell configuration machinery without a demonstrated
   need; a parameter threaded through the facade functions is enough.
5. **The rejected alternatives get measured, not skipped.** Naive fixed point (standalone type,
   round-to-nearest, exact `=`) and float-with-ULP-tolerant-eq must each get a real failure-count row in the
   `cf-exact-arithmetic.fsx` table. The record of what fails and by how much is the point of the exercise.
6. Do not reach for IntSharpCore here - it is an arithmetic type, not a lattice (no bottom/intersection,
   exact `==`); the §4 self-contained interval lattice is the substrate.
7. Style: qualified calls (`List.map`, not bare `map`), no `[<AutoOpen>]`, new work extends the existing
   literate `.fsx` files as new sections - no sibling files.

## 3. Work items

1. **`cf-exact-arithmetic.fsx` - two new failure rows** using the existing 2001-input Celsius /
   1801-input Fahrenheit sweep methodology:
   - naive fixed point at quantum 0.1: standalone scalar, round-to-nearest per operation, exact `=` meet.
     Expected: nonzero spurious-bottom counts in the contracting direction; record the exact counts.
   - float with ULP-tolerant `eq` (pick and document `n`, e.g. 4): count spurious bottoms *and* cases where
     tolerance masks a genuine sub-tolerance difference (false settles), since that is its distinct defect.
2. **`propagator-number-types.fsx` - new section** "Fixed point as a quantized facade over intervals":
   facade constructors and display per locked rules 2-3; the C <-> F round trip at quantum 0.1 showing the
   float-killing inputs (e.g. C = 1) now assert and read back cleanly in both directions; a genuine conflict
   (C = 1 and F = 100) surfacing visibly rather than snapping; the §5 barometer read through the facade at
   two quanta (√10 as `3.2` at q = 0.1, `3.16` at q = 0.01).
3. **Oracles:** full-sweep zero spurious bottoms in both directions through the facade; display round-trip
   identity for on-grid assertions; wide-interval honesty (a wide result must not display as a point);
   genuine-conflict bottom still real.
4. **Records:** on completion, append a dated entry to §5 below and update the failure table's surrounding
   prose in `cf-exact-arithmetic.fsx` to include the new rows in its ranking. No benchmarks - this is the
   correctness thread.

## 4. Acceptance gate

Fable (Tier 0) reviews on primary evidence: reruns the sweeps, verifies locked rules 1-3 in code, and
checks that both rejected-alternative rows exist with real measured counts.

## 5. History (append-only)

- **2026-07-11 - Fable (Tier 0).** Work order created after Deen ratified fixed point as the answer to the
  2026-06-23 float-robustness question, delegating the facade-vs-own-type choice. Facade over intervals
  chosen; rationale and locked rules above.

- **2026-07-11 - Codex (execution complete).** Implemented fixed point as boundary-only quantization over
  the unchanged outward-rounded `Interval` lattice in `propagator-number-types.fsx`. Decimal literals are
  compared with binary endpoints exactly and minimally enclosed; reads use half-open
  `[g - q/2, g + q/2)` cells and preserve wide intervals. The 2001-input Celsius and 1801-input Fahrenheit
  facade sweeps produced zero spurious bottoms and zero on-grid display failures; genuine conflict remains
  bottom; the barometer reads `3.2` at q = 0.1 and `3.16` at q = 0.01. Measured rejected alternatives:
  naive fixed point produced 0/2001 Celsius and 800/1801 Fahrenheit failures; four-ULP equality produced
  19/2001 and 12/1801 failures and falsely settled all 3802 adjacent-float conflicts. The work order named
  `cf-exact-arithmetic.fsx`, but no such file exists in the worktree or repository history; to honor the
  locked no-sibling-files rule, its requested executable failure table was appended to the existing numeric
  literate file beside the original sweep record. No benchmarks or propagation-engine changes were made.

- **2026-07-11 - Deen + Codex (ergonomic facade follow-up).** Deen rejected the first function-shaped
  `literal quantum value` / `read quantum interval` access as mechanically correct but less ergonomic than
  intervals. A custom `P` numeric suffix was tested directly in FSI and rejected by the compiler (`FS1156`);
  compiled decimal literals were then verified to preserve written scale (`12m`, `12.5m`, `12.50m`, and
  `12.500m` report scales 0, 1, 2, and 3). Superseding the first access shape, `FixedPoint` is now a sealed
  immutable expression/presentation class: `FixedPoint(12.50m)` infers q = 0.01, `TryPoint` and `ToString()`
  present the result, and `WithQuantum` explicitly changes presentation. Overloaded `+`, `-`, `*`, and `/`
  delegate only to the unchanged `Interval` operations, support decimal in either operand position, never
  quantize during arithmetic, retain the fixed quantum for exact decimal constants, and choose the coarser
  quantum for two fixed operands. Propagator cells remain `Interval`; a private adapter wraps before calling
  relations such as `c * 9m / 5m + 32m` and unwraps afterward. The full 2001/1801 sweeps, conflict, wide-range,
  inference, operator-interoperability, and two-quantum barometer oracles pass with the prior outputs.

- **2026-07-11 - Deen + Codex (core integration, both implementations retained).** At Deen's direction,
  retained the complete literate implementation and added an enduring second implementation beside the
  canonical core `Interval` in `propagator-surface-vocab.fsx`. The core class has the same exact-decimal
  enclosure, inferred quantum, presentation, and interval-delegating operator laws. Added
  `Domain.fixedPoint quantum` in Friendly as payload adaptation only: live cells still store `Interval`,
  relations receive and return `FixedPoint`, injection unwraps the interval without quantizing, and reads
  rewrap using the domain quantum. No propagation mechanics or second mutable model entered Friendly.
  Added core-path regressions for decimal scale inference, coarser fixed/fixed quantum, all four decimal
  operators in both operand positions, operator-authored C/F propagation, genuine-conflict provenance,
  retraction, and independent 2001-input Celsius / 1801-input Fahrenheit zero-bottom sweeps. The full
  Friendly suite passes. Both implementations remain intentionally: the number-types file is the executable
  literate derivation and failure comparison; the core copy is the load-safe enduring library component.

- **2026-07-11 - Deen + Codex (quantum mismatch made explicit).** Supersedes the same-day automatic
  coarser-quantum rule while preserving those earlier entries as history. Fixed/fixed `+`, `-`, `*`, and `/`
  now require equal quanta and raise an argument error on mismatch; callers reconcile deliberately with
  `WithQuantum` before arithmetic. Fixed/decimal operations remain valid in either operand position and keep
  the fixed operand's quantum because the decimal is treated as an exact constant. This prevents both silent
  precision inflation and silent precision loss without introducing quantum marker types or changing interval
  arithmetic. Both retained implementations carry the same guard. Their regressions require all four
  mismatched operators to fail and an explicitly reconciled operation to succeed; the complete numeric and
  Friendly suites, including both independent 2001/1801 sweeps, pass unchanged.

- **2026-07-11 - Fable (Tier 0) - review PASSED on primary evidence, extras included.** Fresh runs of both
  suites, exit 0 each; the oracles in both are hard failures, so the clean exits are load-bearing.
  `propagator-number-types.fsx` reproduced every measured count in the execution entries exactly: naive
  fixed point 0/2001 Celsius and 800/1801 Fahrenheit (nonzero in the contracting direction, as the work
  order predicted); four-ULP equality 19/2001, 12/1801, and 3802/3802 false settles on adjacent-float
  conflicts; facade sweeps 0/2001 and 0/1801 with 0 display failures; C=1-and-F=100 still bottom; barometer
  `3.2` at q = 0.1 and `3.16` at q = 0.01. `propagator-friendly.tests.fsx` PASSED including the new
  core-path regressions. Locked rules verified in code: the number-types change is a single pure-append
  hunk (everything through §5 byte-identical), and in the core the pre-existing `Interval` module is
  unmodified - both copies of `FixedPoint` delegate every operator to interval arithmetic and no code path
  rounds to the user grid during construction, propagation, or arithmetic (rule 1 by the strongest means);
  enclosure and cell-fit decisions go through an exact bigint-rational decimal-vs-double comparison, never
  a decimal-to-double round trip (rules 2-3, half-open `[g - q/2, q + q/2)` documented). The
  `cf-exact-arithmetic.fsx` deviation is accepted: that file was renamed to `propagator-number-types.fsx`
  on 2026-06-23 before it was ever committed, so the work order cited a pre-rename name that never entered
  repository history; folding the table into the renamed file was therefore not merely permissible but the
  literally correct target. The Deen-directed extras are accepted on review: the sealed
  `FixedPoint` class, scale-inferred quantum, decimal operator interop, and `WithQuantum` are
  presentation-and-expression machinery only; `Domain.fixedPoint` in Friendly is payload adaptation over
  the same `General.createLattice` path as `Domain.interval` (cells still store `Interval`; no propagation
  mechanics or second model entered Friendly); the retained dual implementation is declared plurality with
  distinct roles (literate derivation vs load-safe core), not duplication - and its conflict/retraction
  regression keeps provenance premise-framed. One documented sharp edge, not a defect: quantum inference
  reads the decimal's *written* scale, so arithmetic performed on a decimal before construction can change
  the inferred quantum; use the explicit-quantum constructor when the literal is not authored by hand.
  This closes the 2026-06-23 float-robustness question end to end.

- **2026-07-11 - Fable (Tier 0) - mismatch-guard review PASSED.** Reviews the "quantum mismatch made
  explicit" change, which was executed *after* the review entry above (that entry's acceptance of the
  automatic coarser-quantum rule is superseded; the entries' file order does not reflect chronology here).
  The approach is accepted as the correct resolution of the documented sharp edge: coarser-wins propagated
  a surprising inferred quantum *silently*, finest-wins would be worse (it silently claims precision the
  coarse operand never had), and failing loudly surfaces the authoring error at exactly the first point two
  precision claims meet, with `WithQuantum` as the visible, deliberate reconciliation. Fixed/decimal staying
  unguarded is correct - a formula constant like `9m/5` is exact, not a precision claim. Verified on primary
  evidence: both suites rerun fresh, exit 0, every §6/§7 measured count and both 2001/1801 sweeps unchanged;
  both implementations carry the same `SharedQuantum` guard (`invalidArg` before any interval operation, so
  no lattice state can be corrupted and no bottom fabricated); regressions in both suites require all four
  mismatched operators to throw and an explicit `WithQuantum` reconciliation to succeed. Containment
  verified: inside Friendly's `Domain.fixedPoint` a mismatch is unreachable (every value in a network is
  wrapped at the single domain quantum), so only user-level expression code can trip the guard - which is
  where the error belongs. Also checked: .NET decimal equality is numeric, not scale-sensitive
  (`0.1m = 0.10m`), so scale variants of the same quantum cannot throw spuriously.
