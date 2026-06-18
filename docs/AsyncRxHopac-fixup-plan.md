# AsyncRxHopac.fsx - Correctness and Hardening Plan

Actionable plan to bring the file from "compiles" to "correct." Items are ordered
by behavioral risk. P0 fixes observable grammar and computation-expression
semantics, P1 fixes cancellation/resource lifetime, and P2 covers boundedness,
consistency, and naming.

## Current implementation status

P0, P1, and P2 are signed off (see "Review sign-off" below), Codex's one
non-blocking follow-up (the `ofSeq` Stop-path regression) is in, and the
post-P2 polish batch (clause-selection fix, `AsyncRx` rename, synonym prune,
do-notation sample, `Applicative`→`Operators` fold) is done. The follow-up
module cleanup folds `Operators` into `AsyncObservable`, removes the ambiguous
`Observable` module name, and keeps the `AsyncRx.switch` facade alias. The expanded suite
(`../AsyncRxHopac.Tests.fsx`, **71 checks**) passes and exits 0. See
"P2 implementation — 2026-06-15", "Post-P2 polish batch — 2026-06-17", and the
latest 2026-06-18 sign-off below.

| Item | Status |
|------|--------|
| P0-1 terminal-safe `bind` | ✅ done; active-inner Stop regression added |
| P0-2 completion-based `Combine` | ✅ done; `second`-throw now routed through the gate; regressions added |
| P0-3 stack-safe, completion-based `While` | ✅ done |
| P0-4 regression harness | ✅ done; expanded suite now has 71 checks incl. `second`-throw, malformed-source, `For`/`ofSeq` lifecycle, Stop, clause selection, facade alias coverage |
| P0-5 observable source contract | ✅ done; documented + reusable `Internal.terminalGate` at the boundary |
| P1-1 Task cancellation-bridge lifetime | ✅ done; race Stop vs completion; no detached stop watcher |
| P1-2 remove core `CancellationTokenSource` | ✅ done; Hopac pinned to `0.5.1` |
| P1-3 lazy / stack-safe `For` | ✅ done; enumerator acquired in-scope, disposed-before-complete, failures → one error |
| P2 polish | ✅ done; all 9 items addressed — see "P2 implementation — 2026-06-15" |
| Post-P2 polish batch | ✅ done; clause-selection fix, `AsyncRx` rename, synonym prune, sample, `Applicative`→`Operators` fold (70 checks) — see "Post-P2 polish batch — 2026-06-17" |
| Module naming cleanup | ✅ done; `Observable` + `Operators` folded into canonical `AsyncObservable` module (71 checks) |

Verify the current baseline with:
`dotnet fsi AsyncRxHopac.Tests.fsx` (exits non-zero on any recorded failure).

## Decisions & planned next changes — 2026-06-15 (pre-compaction)

Captured before a context compaction so the next session could resume cold.
**All four approved items below — and the open module-name decision — are now
implemented** (2026-06-17, 70 checks). See "Post-P2 polish batch — 2026-06-17"
for the as-built record. Kept here for the decision trail.

### Done this session
- All nine P2 items (see "P2 implementation — 2026-06-15").
- Dissolved the `Joinad` facade into role-precise modules (see "Module
  restructure (P2-5, expanded)").
- Added Codex's non-blocking `ofSeq` Stop-path regression (60 → 63 checks).

### Approved, implement next (in this order) — ✅ all done 2026-06-17

1. **Clause-selection fix.** `clauses { }` selects via `Choice.amb` = first
   *notification* wins. A non-matching `case` clause emits nothing but still
   *completes* (its source completes), so that completion can win the race and
   yield an empty completion instead of a matching value (timing-dependent, so it
   hides). Add a selector that races on the first **value** (`OnNext`), discards
   branches that complete without emitting, and completes-empty only if *all* do
   (working name `Choice.firstValue`). Point `ClauseCE.Run` at it. Add
   regressions, incl. the empty-completer-must-not-win case.
2. **Rename `Rx` → `AsyncRx`, `rx` → `asyncRx`.** Bare "Rx" implies classic pull
   Reactive Extensions; this is async *push* on Hopac. Drop the `rx` alias (it
   only aliases `AsyncRxCE.asyncRx`, so this also removes a synonym). Builder stays
   defined once in `AsyncRxCE.asyncRx`; the facade surfaces it.
3. **Prune redundant synonyms from core modules**, keeping friendly names only in
   the `AsyncRx` facade. Rule: *core = one canonical name per op; facade = the
   friendly surface.* Remove `AsyncObservable.value` (≡ `singleton`), `Choice.race`
   (≡ `amb`), `AsyncObservable.switch` (≡ `switchLatest`) from core; keep
   `AsyncRx.value`/`race`/`switch`.
4. **Add an `asyncRx { }` do-notation sample** to `Examples` exercising the
   builder surface the current samples skip: `let!` bind chains, `do!`, `for`,
   `while`, statement-`Combine` sequencing, `return`. (Today only `let!`/`and!`
   appear, in `productMergeDemo`.)

### Open decision — product-combinator module name — ✅ resolved 2026-06-17

The `zip` + `bothOnce` module was `Applicative`, which is too broad/inaccurate (it
is not the applicative instance — `pure` is `singleton`). `Zip` and `Product` were
also rejected: too generic, they do not communicate that these are AsyncRx/stream
operations. **Resolved: fold `zip`/`bothOnce` into `Operators`** (they are stream
operators, sit beside `map`/`switchLatest`/`debounce`, "an AsyncRx operator" is
then unambiguous, and the tiny module disappears). The `Applicative` module is
deleted; `AsyncRxCE.MergeSources` and the `AsyncRx` facade now reference
`AsyncObservable.zip`/`AsyncObservable.bothOnce`.

### Resolved — "Joinad view" module: skip it

A `Joinad` re-export *view* (like the facade) has essentially no value as a code
module — it implements nothing, duplicates documentation, and risks re-introducing
the facade confusion the dissolution just removed (and until step 1 lands it would
advertise the buggy `amb`-based clause selection). Keep the joinad lineage as
**documentation** only (already noted on the product module and `ClauseCE`).
Revisit only if a concrete need appears.

## Post-P2 polish batch — 2026-06-17

The four approved items plus the resolved module-name decision, as built.
Suite: 63 → **70 checks**, all passing, exit 0.

### Clause-selection fix (`Choice.firstValue`)
- Added `Choice.firstValue : AsyncObservable<'T> list -> AsyncObservable<'T>`.
  It subscribes all clauses, then races their notifications but treats a bare
  `Completed` (a clause that finished *without* emitting) as a non-winner: that
  index is dropped from the active set and the race continues over the rest. The
  first `Next` wins (losers cancelled, winner's remaining stream forwarded via the
  same `forward` shape as `amb`); the first `Error` wins (all cancelled, error
  forwarded); the combined stream completes empty only when *every* clause has
  empty-completed (`active = []`).
- `ClauseCE.Run` now calls `Choice.firstValue` (was `Choice.amb`); doc updated.
- Regressions (7): empty-completer must not preempt a slower match (the latent
  bug — fails under `amb`), all-empty → single completion, faster value wins /
  slower cancelled, error wins, plus two end-to-end through `clauses { }`.

### `Rx` → `AsyncRx`, `rx` → `asyncRx`
- Facade module renamed `Rx` → `AsyncRx`. The `rx` alias is dropped; the facade
  now exposes `asyncRx` (= `AsyncRxCE.asyncRx`). `Examples` opens `AsyncRx` and
  uses `asyncRx { }`; the `MergeSources` doc example updated to `asyncRx`.

### Core synonym prune (canonical in core, friendly in facade)
- Removed `AsyncObservable.value` (≡ `singleton`), `Choice.race` (≡ `amb`),
  `AsyncObservable.switch` (≡ `switchLatest`). The friendly names survive only on the
  `AsyncRx` facade (`value`/`race`/`switch`… ). No internal callers existed.

### `asyncRx { }` do-notation sample
- Added `Examples.asyncRxBuilderDemo`, exercising `for` (enumeration),
  `let!`/`and!` (bind + applicative product), `do!` (statement bind), `while`,
  statement `Combine`, and `return` — the surface the existing samples skip.

### `Applicative` → `Operators` fold
- The `Applicative` module is deleted. `zip` (+ its private `ZipMsg`) and
  `bothOnce` moved verbatim into `Operators` (beside `first`/`firstWhere`).
  `AsyncRxCE.MergeSources` (`open Operators`) and the `AsyncRx` facade reference
  `AsyncObservable.zip`/`AsyncObservable.bothOnce`; the test's Stop-path zip check uses
  `AsyncObservable.zip`.

### `Observable` / `Operators` → `AsyncObservable` fold — 2026-06-18
- Supersedes the intermediate `Operators` module: ordinary source constructors
  (`empty`, `singleton`, `fail`, `never`, `ofSeq`, `intervalMillis`, `guard`) and
  one-stream/product combinators (`map`, `mapJob`, `filter`, `choose`, `bind`,
  `take`, `scan`, `debounce`, `switchLatest`, `first`, `firstWhere`, `zip`,
  `bothOnce`) now live together under `AsyncObservable`.
- The old `Observable` module name was too easy to confuse with classic
  `System.IObservable` / Rx naming. The module now shares the precise
  `AsyncObservable` name with the public function type, which F# supports and
  which makes call sites such as `AsyncObservable.map` read as operations on
  this exact abstraction.
- `Merge` and `Choice` intentionally remain separate: their names encode
  concurrency/selection policy (`merge`, `amb`, `firstValue`) rather than ordinary
  stream transformation.

### Latest review sign-off — 2026-06-18

Codex reviewed the current post-P2 polish state and grants implementation
approval for the latest behavioral surface. Verification:

- `dotnet fsi AsyncRxHopac.Tests.fsx` passes all **71 checks** and exits 0.
- `dotnet fsi --use:AsyncRxHopac.fsx --exec` type-checks successfully.

Review notes:

- **Module naming cleanup addressed:** `Operators` is folded into
  `AsyncObservable`, and the ambiguous `Observable` module name is gone.
- **API polish addressed:** the friendly `AsyncRx.switch` alias now survives on
  the facade after pruning `AsyncObservable.switch`, with a small regression check
  covering the alias.
- **Documentation hygiene:** the older "Review sign-off" section below is a
  historical 2026-06-15 sign-off for the 60-check P0/P1/P2 state. This
  2026-06-18 section is the current sign-off for the 71-check post-P2 polish
  state.

Signed: Codex  
Date: 2026-06-18

## P2 implementation — 2026-06-15

All nine P2 items are implemented and verified; the suite grew from 49 to
**60 checks** (`../AsyncRxHopac.Tests.fsx`), all passing, exit 0.

### Code changes

- **P2-7 / P2-9 — `ofSeq` stack-safety + enumerator lifecycle.** Rewrote `ofSeq`
  to mirror the hardened `For`: enumerator acquired inside the protected scope, a
  stack-safe Hopac `job { while }` loop (replacing `return! loop ()`, which
  overflowed for fully synchronous downstreams), disposal *before* `OnCompleted`,
  normal-path disposal failure → the single terminal error, idempotent dispose,
  and a `finally` that disposes (swallowing failure) on the
  error/stop/acquisition-failure paths. Verified by a 100k synchronous probe and
  the same GetEnumerator/MoveNext/Dispose/normal-completion lifecycle checks `For`
  has.
- **P2-8 — unwrap single-inner `AggregateException`** at the Task boundary
  (`ofTaskFactory`). `Job.awaitTask` surfaces a faulted Task as its wrapping
  `AggregateException`; a single inner exception is now unwrapped before the
  `OperationCanceledException` check, so the underlying fault/message surfaces.
  The fault regression now asserts `[ Error "kaboom" ]` instead of only the shape.
- **P2-2 — `giveOrStop` simplified to `Job<unit>`.** Every caller discarded the
  former `Job<bool>`; the signature is now `Job<unit>` (`ctx.Stop <|> Ch.give …`,
  or `Job.unit ()` when already stopped) with a doc comment making the
  drop-on-stop semantics explicit. Updated all five `send` helpers
  (`switchLatest`, `debounce`, `merge`, `amb`, `zip`).
- **P2-6 — `Subscription.Completion`.** Added a `Completion : Alt<unit>` field
  that fires once the source job finishes (terminal, Stop-unwind, or a thrown
  source/observer). `subscribeJob` fills an `IVar<unit>` via `Job.tryFinallyJob`,
  so completion is deterministic even when the observer raises. A `recordUntilDone`
  test helper awaits it (no timeouts). This is explicitly source-job completion,
  not a guarantee that arbitrary external work has honored cancellation already;
  for example, a Task factory may continue briefly after cancellation is requested.

### Documentation-only items (per the plan's own guidance)

- **P2-1 — `zip` unbounded buffering** documented on `zip`: queues grow without
  bound if one side outruns the other; bound the faster side upstream. No
  backpressure added (semantics-changing) by design.
- **P2-3 — Stop-check policy** documented on `mapJob`: guard internal await
  points, not synchronous hand-offs. `map`/`filter`/`choose`/`scan` need no Stop
  check (no suspension; source ceases emission); `mapJob` rechecks around its
  awaited `f`. The apparent inconsistency *is* the policy, now stated.
- **P2-4 — `debounce` timer churn** documented: each pending iteration arms a
  fresh `timeOutMillis`; a superseded timer belongs to an already-withdrawn
  `Alt.choose` and wakes nothing (harmless). Replace with a generation-tagged
  timer only if high-frequency input shows material pressure.
- **P2-5 — `Joinad` naming** initially addressed with a precise module-level doc,
  then (with the prototype owner's go-ahead) taken further — the `Joinad` facade
  was **dissolved**. See "Module restructure (P2-5, expanded)" below.

### Tests added

11 new checks: deterministic completion (finite / empty / error), the `ofSeq`
lifecycle suite (GetEnumerator / MoveNext / normal-path Dispose failures, each
with a disposed-flag assertion, plus values-then-completion), and a 100k-element
`ofSeq` stack-safety probe. The task-fault check was strengthened (asserts the
unwrapped message, not just the shape). Net 49 → 60 (one existing check
rewritten, not added).

### Module restructure (P2-5, expanded) — 2026-06-15

The `Joinad` module was a facade: of its members only `zip` (plus the
condition-source `guardJob`) were genuinely implemented there; the rest collapsed
to `Choice.amb` (`chooseFirst`/`orElse`/`matchAny`), `Operators` (`caseOf` ≡
`choose`, `first`, `firstWhere`), `zip` (`both`), or `id` (`clause`). With the
prototype owner's approval it was dissolved so each combinator lives once, in the
module matching its algebraic role:

- `zip` + `bothOnce` → renamed module **`Applicative`** (positional product → `and!`).
- `guard` / `guardJob` → **`Observable`** (conditional sources).
- `orElse` → **`Choice`** (binary race over `amb`).
- `first` / `firstWhere` → **`Operators`** (query).
- Dropped as redundant: `chooseFirst`, `matchAny` (≡ `amb`), `both` (≡ `zip`),
  `caseOf` (≡ `choose`), `clause` (≡ `id`). `clauses { }` now calls `Choice.amb`
  directly; `Rx.case` calls `AsyncObservable.choose`.

Behavior is unchanged (suite still 60/60); this is purely structural. The three
binary stream combinators now read by role: `Merge` (interleave), `Choice`
(first-to-emit), `Applicative` (positional product).

## Independent sign-off review - 2026-06-15

The implementation type-checks and all **46 current checks pass**, but it is not
ready for implementation sign-off.

### P0 blocker - `Combine` swallows exceptions thrown by `second`

At [`../AsyncRxHopac.fsx#L1081-L1092`](../AsyncRxHopac.fsx#L1081-L1092),
`firstTerminated` is set to `true` before `second ctx gated` runs. If `second`
then throws before sending a terminal notification, control reaches the enclosing
catch with `firstTerminated = true`, so the catch does nothing and the exception
is swallowed:

- A `second` that throws immediately produces `[]`; expected
  `[Error "second-throw"]`.
- A `second` that emits `7` and then throws produces `[Next 7]`; expected
  `[Next 7; Error "second-after-value"]`.
- A throw from `first` is still handled correctly as `[Error "first-throw"]`.

The fix should route exceptions caught at
[`../AsyncRxHopac.fsx#L1089-L1092`](../AsyncRxHopac.fsx#L1089-L1092) through
`gated.OnError` even after `first` has completed. The shared terminal gate will
discard a genuinely post-terminal throw from `second`, while a pre-terminal
throw becomes the required single downstream error. Keep the
`firstTerminated` check only for controlling whether `second` may start.

Current `Combine` regressions at
[`../AsyncRxHopac.Tests.fsx#L253-L274`](../AsyncRxHopac.Tests.fsx#L253-L274)
cover duplicate errors from `first` and completion-then-error from `second`, but
not a direct exception thrown by `second`. Add both cases above and assert the
complete event sequences.

**Sign-off condition.** Re-run the full suite after the fix. P0-2 and P0-4 may
be closed when the two new regressions pass and no existing check regresses.

### Sign-off blocker resolved — 2026-06-15

Fixed. The root cause was exactly as diagnosed: `firstTerminated <- true` ran
before `second ctx gated`, and the enclosing catch was guarded by
`if not firstTerminated`, so a pre-terminal throw from `second` was dropped.

- **Fix** ([`../AsyncRxHopac.fsx#L1090-L1094`](../AsyncRxHopac.fsx#L1090-L1094)):
  the catch is now an unconditional `do! gated.OnError e`. A pre-terminal throw
  from `first` *or* `second` becomes the single downstream error; the gate
  discards a genuinely post-terminal throw (e.g. `second` threw after completing).
  `firstTerminated` is kept solely to control whether `second` may start (at most
  once, never after `first` errors) — `first`'s `OnError` still sets it, but no
  longer guards the catch.
- **Regressions added** ([`../AsyncRxHopac.Tests.fsx#L276-L300`](../AsyncRxHopac.Tests.fsx#L276-L300)):
  `second` throws before any terminal → `[Error "second-throw"]`; `second` emits
  `7` then throws → `[Next 7; Error "second-after-value"]`; and `first` throws →
  `[Error "first-throw"]`. The first two return `[]` / `[Next 7]` under the old
  code, so they fail on the bug and pass on the fix.
- **Suite:** 46 → **49 checks**, all pass, exit 0.

## Superseded self-review status

P0 and P1 are **implemented and verified**; P2 remains optional polish.

| Item | Status |
|------|--------|
| P0-1 terminal-safe `bind` | ✅ done |
| P0-2 completion-based `Combine` | ✅ done |
| P0-3 stack-safe, completion-based `While` | ✅ done |
| P0-4 regression harness | ✅ `../AsyncRxHopac.Tests.fsx` (24 checks, all pass) |
| P1-1 Task cancellation-bridge lifetime | ✅ done (race Stop vs completion; no detached watcher) |
| P1-2 remove core `CancellationTokenSource` | ✅ done; Hopac pinned to `0.5.1` |
| P1-3 lazy / stack-safe `For` | ✅ done |
| P2 polish | ⏳ pending (optional) |

Verify with: `dotnet fsi AsyncRxHopac.Tests.fsx` (exits non-zero on any failure).

## Independent review correction - 2026-06-15

The script type-checks and all 24 current checks pass. Targeted lifecycle and
terminal-grammar probes found the following remaining work:

1. **`Combine` is not fully terminal-safe.** A first source that reports two
   errors produces two downstream errors. A second source that completes and then
   errors produces `Completed` followed by `Error`. Use one terminal gate for the
   entire combined observable, including notifications from `second`.
2. **`For` does not fully own enumerator failures.** `GetEnumerator()` is outside
   its error handler, and normal completion is sent before `Dispose()`. Enumerator
   acquisition can therefore escape `runJob`, while a disposal failure can produce
   `Completed` followed by `Error`.
3. **The observable source contract is implicit.** Decide whether custom
   `AsyncObservable` implementations must serialize callbacks and obey
   exactly-one-terminal grammar, or whether the library enforces that grammar at
   its subscription boundary. Because `AsyncObservable` is a public function type,
   the preferred design is a reusable terminal gate at the boundary, with
   operators still responsible for not creating post-terminal work themselves.
4. **The regression suite is incomplete relative to P0-4.** Add Stop tests for
   `interval`, active-inner `bind`, `merge`, and `zip`; add `For` acquisition,
   disposal, and Stop tests; add duplicate/post-terminal source probes.

`ofSeq` remains scheduled for P2, but its rewrite must also dispose its enumerator
before normal completion and convert acquisition, iteration, or disposal failures
into one error rather than completion followed by error.

---

## Review-correction implementation — 2026-06-15

All four corrections from the independent review are implemented and verified.
The suite grew from 24 to **46 checks** (`../AsyncRxHopac.Tests.fsx`), all passing,
exit 0.

### What changed

- **P0-5 — source contract + boundary gate.** Documented the contract on the
  `AsyncObservable` type ([../AsyncRxHopac.fsx#L43](../AsyncRxHopac.fsx#L43)):
  callbacks are **serialized** (settled by reading `merge`/`zip`, which funnel
  every inner notification through a single channel + consumer loop, so downstream
  observers never see concurrent callbacks), and the terminal grammar is zero+
  `OnNext` then at most one terminal. Because callbacks are serialized, the gate is
  a plain mutable flag, not a synchronized primitive. Added a reusable
  `Internal.terminalGate` and applied it at the subscription boundary in both
  `Subscribe.subscribeJob` and `Subscribe.runJob`; each boundary's `try/with` now
  routes a post-terminal throw through the same gate, so it cannot surface as a
  second error.
- **P0-2 — `Combine` terminal gate over both sources.** `Combine` now wraps the
  downstream observer in its own `terminalGate`, so a misbehaving `first` *or*
  `second` (double error, complete-then-error) yields exactly one downstream
  terminal. A separate operator-local `firstTerminated` flag guarantees `second`
  is subscribed at most once and never after `first` errors (the gate cannot
  enforce that, since starting `second` is a side effect, not a notification).
- **P1-3 — `For` enumerator lifecycle.** `GetEnumerator()` now runs inside the
  protected scope (a throwing acquisition becomes one `OnError` instead of
  escaping `runJob`). On normal exhaustion the enumerator is disposed *before*
  `OnCompleted`, and a normal-path disposal failure becomes the single terminal
  error (no `Completed` then `Error`). A `finally` guarantees disposal on the
  error/stop/acquisition-failure paths and swallows disposal failures there (the
  terminal is already decided). Disposal is idempotent (the enumerator field is
  cleared once disposed). The `job { while }` loop keeps it stack-safe.
- **P0-4 — expanded regressions.** Added: malformed-source boundary probes
  (double error → one; complete-then-error → completion only; value-after-complete
  dropped; throw-after-error → one error); `Combine` duplicate/late-terminal
  probes (first double-error → one error and `second` not started;
  second-completes-then-errors → value + one completion); `For` lifecycle via a
  `ProbeEnumerator` (GetEnumerator failure, MoveNext failure, normal-path Dispose
  failure, and disposal-observed on normal completion / body error / Stop); and
  Stop-terminates-cleanly checks for `interval`, active-inner `bind`, `merge`, and
  `zip` (each asserts zero terminals, bounded so a deadlock fails visibly).

### Notes / deviations

- The earlier `For` stack-safety probe used `AsyncObservable.intervalMillis` as a
  unit body; the Stop test instead uses `AsyncObservable.never` (a unit-typed source
  that suspends until Stop), since `For` requires `AsyncObservable<unit>` bodies.
- Operator-local guards are kept in `Combine` and `For` per the review's
  guidance: the boundary gate is the last line of defense, not a replacement for
  correct operator lifecycle.
- `ofSeq` still carries the P2 work (item 7/9): iterative rewrite **plus** the same
  enumerator acquire-in-scope / dispose-before-complete / failure-to-one-error
  lifecycle now used by `For`.

---

## Update — 2026-06-15 (post-implementation self-review)

### Summary

All P0 + P1 items implemented; the regression suite (`../AsyncRxHopac.Tests.fsx`,
**24 checks**) passes and exits 0. A self-review after the first pass found and fixed a
real stack-safety defect in my own `For`/`While`, and turned up one pre-existing issue
(`ofSeq`) that is out of plan scope.

### Shape of the actual choices

- **`bind` (P0-1).** A bind-owned **child context** + a single mutable `terminated`
  flag. Inner `OnNext` is forwarded only while live, inner `OnCompleted` is swallowed
  (outer alone completes), and the first error/completion cancels the child and forwards
  exactly one terminal. The mutable flag is sound because bind introduces no concurrency:
  the inner runs *inside* (awaited by) the outer's `OnNext`, so outer and inner callbacks
  are serialized. Documented inline.
- **`Combine` (P0-2).** Direct completion-based sequencing (ignore `first`'s values, start
  `second` once on `first`'s completion, never after error). Bounded recursion (statement
  count), so its `try/with` nesting is not a stack concern.
- **`While` (P0-3).** **Changed from the approved sketch.** The plan proposed
  `Combine(body, Delay loop)`; that is correct but *not stack-safe* for tight synchronous
  loops (each iteration nests another `Combine` `try/with`). Replaced with a direct
  Hopac `job { while … }` loop — simpler, stack-safe, and independent of `Combine`.
- **`For` (P1-3).** First implemented with `do! body …; return! step ()`. That **also**
  overflowed (see Failures). Rewritten as a Hopac `job { while … }` loop over the
  enumerator, disposed in `finally`.
- **`ofTaskFactory` (P1-1).** Race `ctx.Stop` against task completion via an `IVar`
  bridge job (which observes the task's outcome, so a cancelled task never raises
  unobserved). No detached watcher; `linked` is `Cancel()`-ed on stop and `Dispose()`-d in
  `finally`, so nothing can touch a disposed CTS.
- **`Cancel.Token` (P1-2).** Reduced to just the `IVar` latch; `IsStopped` uses
  `IVar.Now.isFull`; `cancel` is idempotent `IVar.tryFill`. Dead `cancellationToken`
  removed. `#r "nuget: Hopac, 0.5.1"` pinned.

### Failures / surprises encountered (and resolution)

1. **`For` was not stack-safe.** A probe (200k synchronous elements) hit
   `StackOverflowException` (exit 253), top frame my `step@…`. Cause: `do! …; return! step ()`
   is a synchronous bind chain Hopac runs on the CLR stack when nothing suspends.
   **Fixed** with a `job { while … }` loop. My original P1-3 "stack-safe" claim was wrong;
   now verified with a 100k run-to-completion regression check.
2. **`While` inherited the same defect** once `For` was fixed (overflow moved to the
   `Combine`-recursion `While`). **Fixed** by the direct-loop rewrite above; 100k check added.
3. **Task fault message.** `Job.awaitTask` surfaces the Task's `AggregateException`
   wrapper (`"One or more errors occurred. (kaboom)"`), so the fault test now asserts the
   *shape* (exactly one error, no value/completion), not the message.
4. **A test bug, not a library bug.** The first `while: empty-body` test used a guard whose
   counter nothing incremented → genuine infinite loop → overflow. Rewritten to a
   self-terminating guard.

### Discovered, out of scope (pre-existing)

- **`ofSeq` is not stack-safe** for fully synchronous downstreams. It uses the same
  `return! loop ()` pattern; a probe (300k, sync `map`) overflowed and the
  `StackOverflowException` was *uncatchable* (process exit 253). `intervalMillis` shares the
  pattern but is safe in practice because each iteration suspends on a timer. Recommended
  fix: the same `job { while … }` rewrite. Folded into P2 (not changed here).

### Verification artifacts

- `../AsyncRxHopac.Tests.fsx`: 24 assertion checks (bind ×4, combine ×4, while ×6,
  for ×4, stack-safety ×2, task ×4) — all pass, exit 0.

---

One deviation from the draft acceptance: `Job.awaitTask` surfaces the Task's
`AggregateException` wrapper, so the fault test asserts the *shape* (exactly one error)
rather than the exact message. Unwrapping a single-inner `AggregateException` for nicer
error messages is folded into P2.

## Baseline (already done)

The original compilation-error classes are fixed and the file type-checks:

- [x] `^=>` -> `^->` (14 sites): continuations return plain values, so use
  `Alt.afterFun`.
- [x] `switchLatest`: annotate
  `startInner generation (inner: AsyncObservable<'T>)`.
- [x] Remove dead `open Observable` statements from `Joinad`, `AsyncRxCE`, and
  `Rx`.
- [x] `Job.fromTask task` -> `Job.awaitTask task`; `Job.toTask` ->
  `Hopac.startAsTask`.

## P0-1 - Make `bind` terminal-safe

**Problem.** `AsyncObservable.bind` gives each inner observable the downstream observer
directly. Every inner completion therefore completes the downstream stream.
For a multi-value outer source this produces values after completion and multiple
`OnCompleted` calls.

Simply swallowing inner completion fixes the happy path, but it is not sufficient.
If an inner calls `OnError`, the outer source can continue and produce
`OnNext` followed by `OnCompleted`. A probe of the original proposed fix produced:

```text
Error("boom"), Next(2), Completed
```

That violates the observable terminal grammar.

**Fix.**

- Subscribe the outer source through a child context owned by `bind`.
- Forward inner `OnNext` notifications only while the bind has not terminated.
- Swallow inner `OnCompleted`; the outer source alone owns normal completion.
- On either inner or outer error, atomically/gatedly enter the terminal state,
  cancel the bind child context, and forward exactly one `OnError`.
- On outer completion, enter the terminal state and forward exactly one
  `OnCompleted`.
- Catch exceptions raised by either source and route them through the same
  terminal-error path.

The initial implementation may use a mutable terminal flag if the library
explicitly guarantees serialized observer callbacks. If concurrent callbacks are
allowed, serialize terminal transitions through a Hopac primitive rather than
relying on an unsynchronized Boolean.

**Acceptance.**

- `rx { let! x = value 1 in return x }` emits `1`, then exactly one completion.
- `ofSeq [1; 2; 3] |> bind (fun x -> ofSeq [x; x * 10])` emits
  `1; 10; 2; 20; 3; 30`, then exactly one completion.
- An inner failure emits exactly one error, no later values, and no completion.
- An outer failure emits exactly one error.
- Calling `Stop()` during an active inner prevents later notifications.

## P0-2 - Make `Combine` sequence on completion

**Problem.** `AsyncRxBuilder.Combine` currently uses
`bind (fun () -> second) first`, which runs `second` once per value from `first`.
Statement sequencing must instead ignore values from `first` and start `second`
once, after `first` completes. Since `Zero() = empty` emits no value, the current
implementation also causes `Combine(empty, second)` to drop `second`.

**Fix.** Implement completion-based sequencing directly:

```fsharp
member _.Combine
    (
        first: AsyncObservable<unit>,
        second: AsyncObservable<'T>
    ) : AsyncObservable<'T> =
    fun ctx obs ->
        first ctx {
            OnNext = fun () -> Job.unit ()
            OnError = obs.OnError
            OnCompleted = fun () -> second ctx obs
        }
```

Apply one terminal guard to the complete result, not only to the transition from
`first` to `second`:

- A malformed or racing `first` cannot start `second` after an error or start it
  more than once.
- Duplicate errors or completion from `first` are ignored after the first
  terminal.
- Notifications from `second` pass through the same gate, so `second` cannot
  produce duplicate terminals or `Error` after `Completed`.
- An exception thrown after either source has already reported a terminal is not
  forwarded as a second error.

**Acceptance.**

- `Combine(empty, value 5)` emits `5`, then exactly one completion.
- A unit stream that emits several values starts `second` only once, after its
  completion.
- If `first` errors, `second` is never subscribed.
- A two-statement expression runs the first statement to completion, then emits
  the second statement's result.
- Duplicate terminal notifications from either source are reduced to exactly one
  downstream terminal.
- A source that completes and then errors produces completion only.

## P0-3 - Fix `While` independently of `Combine`

**Problem.** Fixing `Combine` does not fix `While`. `While` calls `bind` directly,
so an empty-completing body never starts the next iteration. It also calls
`loop()` while constructing the observable, which evaluates the guard too early.
A probe with a three-iteration guard and an empty body checked the guard only once.

**Fix.**

- Defer the first guard check until subscription.
- Run each body to completion while ignoring its unit values.
- Re-check the guard and start the next iteration from the body's
  `OnCompleted`, not its `OnNext`.
- Stop immediately on body error or subscription cancellation.

This can be expressed using the corrected `Combine` plus `Delay`:

```fsharp
member this.While
    (
        guard: unit -> bool,
        body: AsyncObservable<unit>
    ) : AsyncObservable<unit> =
    let rec loop () =
        if guard () then
            this.Combine(body, this.Delay(loop))
        else
            AsyncObservable.empty

    this.Delay(loop)
```

Verify that this does not eagerly recurse while constructing the computation.

**Acceptance.**

- A guard that is true three times executes the body three times and checks the
  guard a fourth time before completing.
- An empty-completing body still advances the loop.
- A body that emits multiple unit values advances only once per completion.
- A body error terminates the loop without another guard check.
- An initially false guard is not evaluated until subscription.

## P0-4 - Add executable regression checks

The `Examples` module is useful as a demonstration but is not sufficient for
terminal-count or race verification. Add an assertion-based regression module or
a separate `.fsx` test script that records notifications as:

```fsharp
type Event<'T> =
    | Next of 'T
    | Error of exn
    | Completed
```

Tests must assert complete event sequences, not only print output. Include a
timeout around cancellation/race tests so a deadlock fails visibly.

Required coverage:

- The P0-1 `bind` cases.
- The P0-2 `Combine` cases.
- The P0-3 `While` cases.
- `For` over at least three values.
- Stop during `interval`, `bind`, `merge`, `zip`, and task interop.
- Exactly one terminal notification for every terminating test.
- Duplicate and post-terminal notifications from a deliberately malformed source.
- `For` failures from `GetEnumerator`, `MoveNext`, the body, and `Dispose`.
- Enumerator disposal on normal completion, error, and Stop.

The current 24-check script covers the primary happy paths, stack safety, and task
interop, but does not yet cover all Stop, malformed-source, or enumerator-lifecycle
cases above. Keep P0-4 open until those checks are present and passing.

## P0-5 - Define and enforce the observable source contract

**Problem.** `AsyncObservable<'T>` is a public function type, so callers can create
sources that report duplicate terminals, report values after a terminal, throw
after reporting an error, or invoke observer callbacks concurrently. Several
operators currently assume a serialized, grammar-correct source.

**Fix.**

- Document whether source callbacks must be serialized.
- Document the grammar: zero or more `OnNext`, followed by at most one
  `OnError` or `OnCompleted`, with no later notifications.
- Prefer a reusable terminal-gated observer at the subscription boundary so
  downstream consumers are protected even when a custom source violates the
  contract.
- If concurrent callbacks are supported, serialize transitions with a Hopac
  primitive or another explicit synchronization mechanism. A mutable Boolean is
  sufficient only where callback serialization is guaranteed.
- Keep operator-local guards where an operator owns multiple sources or cleanup
  work; the boundary gate is a final defense, not a replacement for correct
  operator lifecycle.

**Acceptance.**

- A source that errors twice produces one downstream error.
- A source that completes and then errors produces one downstream completion.
- A source that reports an error and then throws does not produce a second error.
- The documented serialization rule matches the implementation and tests.

## P1-1 - Fix the Task cancellation bridge lifetime

**Problem.** `TaskInterop.ofTaskFactory` starts a detached job that waits on
`ctx.Stop` and then calls `linked.Cancel()`. The enclosing job disposes `linked`
when the task finishes, but the detached watcher can remain blocked. Calling
`Stop()` after task completion then makes the watcher call `Cancel()` on a
disposed `CancellationTokenSource`, producing an unhandled
`ObjectDisposedException`.

**Fix.**

- Do not leave a detached stop watcher alive past the CTS lifetime.
- Race `ctx.Stop` against task completion in the owning job.
- If stop wins, cancel the CTS and observe/await task termination as appropriate.
- If the task wins, dispose the CTS only after the stop branch can no longer use
  it.
- Route task cancellation caused by subscription stop to silent termination,
  not downstream `OnError`.
- Preserve a genuine task-originated `OperationCanceledException` as an error
  when the subscription itself was not stopped.

**Acceptance.**

- Stop during a running task requests cancellation through the factory token.
- Stop after normal task completion produces no unhandled exception.
- Normal completion emits one value and one completion.
- Task fault emits exactly one error.
- Subscription cancellation emits no value, error, or completion after stop.

## P1-2 - Remove `CancellationTokenSource` from the core context

**Problem.** `Cancel.Token` carries a `CancellationTokenSource` that is canceled
but never disposed, with one allocation per child context. It is used only as a
thread-safe Boolean. The `IVar<unit>` latch already represents cancellation, and
`Cancel.cancellationToken` has no callers.

**Fix.** Keep only the latch:

```fsharp
type Token = private { Latch: IVar<unit> }

let create () : Token =
    { Latch = IVar<unit>() }

let asAlt (token: Token) : Alt<unit> =
    IVar.read token.Latch

let isCancellationRequested (token: Token) : bool =
    IVar.Now.isFull token.Latch

let cancel (token: Token) : Job<unit> =
    IVar.tryFill token.Latch ()
```

`IVar.Now.isFull` is present in Hopac 0.5.1. Delete `cancellationToken`, and make
`childContext.IsStopped` call `isCancellationRequested token`.

Pin the package reference to the tested Hopac version rather than using `*`, or
explicitly test all supported versions:

```fsharp
#r "nuget: Hopac, 0.5.1"
```

Real `CancellationToken` values should be created only at boundaries such as
`ofTaskFactory`, with an owned and fully bounded lifetime.

**Acceptance.**

- No remaining `.Cts` or `Cancel.cancellationToken` references.
- Repeated `Cancel.cancel` calls remain harmless.
- The cancellation regression suite passes.
- The script type-checks against the pinned Hopac version.

## P1-3 - Make `For` lazy and stack-safe

**Problem.** `For` currently uses eager `Seq.fold`, constructing the complete
observable chain before subscription. Infinite sequences never start, large
sequences create deeply nested observables, and enumeration resources are not
owned by the subscription lifetime.

**Fix.** Implement `For` with an enumerator acquired at subscription time:

- Acquire the enumerator inside the protected error/lifecycle scope.
- Dispose the enumerator when the loop completes, errors, or is stopped.
- On normal exhaustion, dispose successfully before forwarding `OnCompleted`.
- If normal-path disposal fails, forward one `OnError` instead of completing.
- If a body or enumeration error has already won, cleanup failure must not create
  a second terminal.
- Pull one element at a time.
- Run each body to completion before pulling the next element.
- Ignore unit values from the body.
- Route enumeration and body exceptions through one terminal-error path.

If infinite sequences are intentionally unsupported, state that contract
explicitly and add a practical size limit test. The preferred behavior is lazy
enumeration.

**Acceptance.**

- A finite sequence executes in order and completes once.
- A lazy sequence is not enumerated before subscription.
- `GetEnumerator()` failure produces exactly one error and does not escape
  `runJob`.
- `MoveNext()` failure produces exactly one error.
- `Stop()` prevents further calls to `MoveNext`.
- The enumerator is disposed on completion, error, and stop.
- Normal-path `Dispose()` failure produces one error and no completion.
- A large finite sequence does not overflow the stack.

## P2 - Polish and hardening

1. **`zip` unbounded buffering.** `leftQueue` and `rightQueue` can grow without
   bound when one source outruns the other. Document this clearly or add a
   configurable bound/backpressure policy.

2. **`giveOrStop` discards its Boolean.** Callers currently ignore whether send
   lost to cancellation. Either use the result to terminate producer loops or
   simplify the function to `Job<unit>` and make cancellation semantics explicit.

3. **Stop-check consistency.** `mapJob` re-checks `IsStopped`, while `map`,
   `filter`, `choose`, and `scan` rely on their source. Choose one policy and
   apply it consistently. Keep cancellation checks distinct from terminal
   grammar: stopping suppresses work, while error/completion gates suppress
   illegal post-terminal notifications.

4. **`debounce` timer churn.** Each loop creates a new `timeOutMillis`; abandoned
   timers still fire. Leave a comment unless profiling shows material pressure,
   then replace it with a generation-based or explicitly cancellable timer.

5. **Naming.** `Joinad` currently combines applicative product (`zip`/`and!`) and
   racing choice (`amb`/`orElse`) but does not implement full joinad syntax.
   Rename it or add a precise module-level description.

6. **Subscription lifecycle.** Consider exposing a completion job on
   `Subscription`, or make `Stop()` await complete child shutdown. This would make
   resource-lifetime tests and boundary cleanup deterministic.

7. **`ofSeq` stack safety (discovered 2026-06-15).** `ofSeq` uses `return! loop ()`
   and overflows the stack for fully synchronous downstreams (probe: 300k + sync
   `map` → uncatchable `StackOverflowException`). Apply the same `job { while … }`
   rewrite used for `For`/`While`. `intervalMillis` shares the pattern but is safe in
   practice (each iteration suspends on a timer).

8. **Unwrap single-inner `AggregateException`** at the Task boundary so faults surface
   the underlying exception/message instead of the Task wrapper.

9. **`ofSeq` resource ordering.** Fold this into item 7 when implementing it:
   acquire the enumerator inside the protected scope, dispose it before normal
   completion, and route acquisition, iteration, or disposal failure through one
   terminal-error path. A disposal failure must not produce `Error` after
   `Completed`.

## Remaining implementation order

All P0, P1, and P2 steps are complete and independently verified. No
implementation item remains in this plan.

1. ✅ Add failing regressions for duplicate/post-terminal sources and enumerator
   acquisition/disposal.
2. ✅ Add one terminal gate across all of `Combine`, including `second`.
3. ✅ Correct `For` enumerator acquisition, disposal, and terminal ordering.
4. ✅ Define and enforce the observable source contract (P0-5).
5. ✅ Add the missing Stop regressions for `interval`, active-inner `bind`,
   `merge`, `zip`, and `For`.
6. ✅ Re-run the complete suite and update the implementation-status table.
7. ✅ Fix `Combine` so an exception thrown by `second` before its terminal is
   routed through the shared terminal gate (`AsyncRxHopac.fsx:1153-1157`).
8. ✅ Add immediate-throw and value-then-throw regressions for `second`
   (`AsyncRxHopac.Tests.fsx:293-317`) and re-run the then-current 49 checks.
9. ✅ Address P2, including the `ofSeq` iterative/resource-lifecycle rewrite.
   All nine items done — see "P2 implementation — 2026-06-15" (60 checks).

After each step, run:

```text
dotnet fsi --use:AsyncRxHopac.fsx --exec
dotnet fsi <regression-test-script>.fsx
```

## Review sign-off

Implementation sign-off is **granted** for P0, P1, and P2.

Independent verification on 2026-06-15:

- `dotnet fsi --use:AsyncRxHopac.fsx --exec` type-checks successfully.
- `dotnet fsi AsyncRxHopac.Tests.fsx` passes all **60 checks** and exits 0.
- Focused probes confirm:
  - `second` throws immediately -> `[Error "second-throw"]`
  - `second` emits then throws -> `[Next 7; Error "second-after-value"]`
  - `first` throws -> `[Error "first-throw"]`
  - `second` completes then throws -> `[Completed]`
  - `first` errors then throws -> `[Error "first-error"]`
  - stopping an active synchronous `ofSeq` disposes its enumerator and its
    `Subscription.Completion` fires
  - a throwing Task factory produces one error and its subscription completion fires
  - `zip` processes 100,000 synchronous pairs without stack overflow

The reopened P0-2 and P0-4 conditions are satisfied, and all nine P2 items are
implemented according to their stated acceptance choices. The remaining caveats
are documented design tradeoffs: `zip` buffering is unbounded, superseded
`debounce` timers are not explicitly cancelled, and `Subscription.Completion`
tracks the source job rather than guaranteeing completion of arbitrary external
work after cancellation.

### Non-blocking follow-up

- Persist the focused Stop-path probe as a regression near
  `AsyncRxHopac.Tests.fsx:405`: stopping an active synchronous `ofSeq` must
  dispose its enumerator, emit no terminal notification, and fire
  `Subscription.Completion`. This behavior passed independent review, but the
  60-check suite does not currently protect it.

  **Addressed (2026-06-15):** regression added in the Stop-path section near line
  405 — an active synchronous `ofSeq` paced by a suspending `mapJob`, stopped
  mid-iteration, now asserts enumerator disposal, zero terminals, and that
  `Subscription.Completion` fires (bounded by a 1 s timeout so a regression fails
  visibly rather than hanging). Suite: 60 → **63 checks**, all pass, exit 0.

Signed: Codex  
Date: 2026-06-15
