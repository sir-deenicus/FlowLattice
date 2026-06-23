# Module consolidation plan (AsyncRxHopac)

Status: **as-built (done & verified 2026-06-21).** Decided + executed 2026-06-21.
The whole point was to cut module count ("too many things to memory") while
keeping the qualified-call idiom (`AsyncObservable.map`) — see
[[prefer-qualified-calls]] and [[style-guide]]. All four verification suites pass
(kernel tests ~70, bridge tests 14, both example demos clean).

## As-built deltas (where the build corrected the plan)

- **Self-qualification does NOT work inside `module AsyncObservable`.** The plan
  said `ClausesBuilder.Run → AsyncObservable.firstValue` (and left the builder
  bodies self-qualified). In fact `AsyncObservable.foo` *inside* the module
  resolves `AsyncObservable` to the **type** (`AsyncObservable<'T>`, in scope via
  `open Core`) and fails with "member 'foo' is not defined". Fix: the builder
  methods call sibling operators **unqualified** (`singleton`, `empty`, `bind`,
  `zip`, `map`, `choose`, `filter`, `guard`, `firstValue`). Qualified calls remain
  the rule *from outside* the module (examples/tests). The top-level
  `let asyncRx = AsyncObservable.AsyncRxBuilder()` works fine (companion pattern,
  resolved from outside).
- **Bridge example/test files renamed to `AsyncSeqInterop.*`** (2026-06-21 — the
  optional rename, initially deferred, now done): `AsyncRxAsyncSeq.Examples.fsx` →
  `AsyncSeqInterop.Examples.fsx`, `AsyncRxAsyncSeq.Tests.fsx` →
  `AsyncSeqInterop.Tests.fsx` (both re-verified green after the move). Their `#load`
  points at `AsyncRxHopac.fsx`, and the
  conversions are qualified `AsyncObservable.ofAsyncSeq`/`.toAsyncSeq`. The
  `AsyncObservable.` prefix also landed in the bridge tests' name strings (cosmetic,
  via a replace-all) — harmless.
- Everything else matched the plan below (file order, merge map, rename map).

## Decisions (all locked with the user)

1. **12 modules → 5** (4 public + 1 internal) + 2 top-level CE values.
2. **Names** (user vetoed my suggestions `Hop`/`Rx`):
   - substrate primitives = **`EventChoice`** (not `Hop`)
   - run / front door = **`AsyncRx`** (not `Rx`; "Rx already has a meaning")
   - build = `AsyncObservable`; types = `Core`; plumbing = `Internal`.
3. **`Internal` stays separate and stays `module internal Internal`.** "Internal for
   a reason, least likely to be touched." Do NOT fold it into `EventChoice`.
4. **Surface is open for extension** → `EventChoice` (+ the absorbed `Cancel`) is the
   PUBLIC toolkit for authoring new combinators. (Internal is the private layer below it.)
5. **AsyncSeq bridge folds into the kernel (choice i).** Kernel gains
   `#r "nuget: FSharp.Control.AsyncSeq"`; `AsyncRxAsyncSeq.fsx` is deleted. This is
   the original "merge AsyncRxAsyncSeq into AsyncRx" request. Accepted cost: loading
   the kernel now also pulls AsyncSeq (Hopac was already a kernel `#r`).
6. **Old `AsyncRx` facade (alias-bag) is deleted**; the name `AsyncRx` is reused for
   the run module (renamed from `Subscribe`). Verified the facade holds only aliases,
   no unique logic.
7. **`asyncRx` (CE value) vs `AsyncRx` (module)** case-proximity is fine — user: "like
   `List` vs `list`, `seq` vs `Seq`."

## Target file order (order matters — F# is definition-before-use)

1. **Header** — `#r "nuget: Hopac, 0.5.1"`, `#r "nuget: FSharp.Control.AsyncSeq"`,
   then `open` Hopac etc. + `open System.Threading` + `open FSharp.Control`
   (the last three migrate from the old bridge file).
2. **`Core`** — types, unchanged. Keep the explicit `open Core` after it.
3. **`EventChoice`** (PUBLIC) — current `EventChoice` members + all of `Cancel`
   appended (`Token`, `create`, `asEventChoice`, `isCancellationRequested`, `cancel`,
   `childContext`). `childContext` uses `choose` (defined above) — OK.
4. **`Internal`** (`module internal Internal`) — unchanged except `Cancel.X` → `EventChoice.X`.
5. **`AsyncRx`** (PUBLIC) — renamed from `Subscribe`: `subscribeJob`, `runJob`,
   `runBlocking`, `run`, `subscribe`. **Placed before `AsyncObservable`** (verified: no
   `AsyncObservable`-operator dependency). `Cancel.cancel` → `EventChoice.cancel`.
6. **`AsyncObservable`** (PUBLIC) — existing operators, then in this order:
   - `merge`, `merge2` (from `Merge`)
   - `amb`, `orElse`, `firstValue` (from `Choice`) + its private helpers
     (`SourceEntry`, `sourceEntry`, `startEntry`, `cancelEntries`, `cancelEntriesExcept`)
   - `ofTask`, `ofHotTask`, `ofTaskFactory` (from `TaskInterop`; keep its `open Hopac.Extensions`)
   - `toTaskList` (from `TaskInterop`) — calls `AsyncRx.runJob` (defined above, OK)
   - `ofAsyncSeq`, `toAsyncSeq` + private `unwrapAggregate` (from `AsyncRxAsyncSeq.fsx`)
   - builder classes `AsyncRxBuilder`, `ClausesBuilder` (from `AsyncRxCE`/`ClauseCE`).
     `ClausesBuilder.Run` → `AsyncObservable.firstValue` (same module, defined above);
     clause heads use `choose`/`filter`/`map`/`guard` (same module).
7. **Top level** — `let asyncRx = AsyncObservable.AsyncRxBuilder()` and
   `let clauses = AsyncObservable.ClausesBuilder()`, so a single `open AsyncRxHopac`
   yields the bare CE keywords without flooding operator names.

## Merge map (what dissolves into what)

| Dissolved module | Lands in | Members |
| --- | --- | --- |
| `Cancel` | `EventChoice` | Token/create/asEventChoice/isCancellationRequested/cancel/childContext |
| `Merge` | `AsyncObservable` | merge, merge2 |
| `Choice` | `AsyncObservable` | amb, orElse, firstValue (+ private helpers) |
| `TaskInterop` | `AsyncObservable` | ofTask, ofHotTask, ofTaskFactory, toTaskList |
| `AsyncRxCE` | `AsyncObservable` (class) + top level (value) | AsyncRxBuilder → class; `asyncRx` → top level |
| `ClauseCE` | `AsyncObservable` (class) + top level (value) | ClausesBuilder → class; `clauses` → top level |
| `Subscribe` | renamed → `AsyncRx` | (moved before AsyncObservable) |
| `AsyncRxAsyncSeq.fsx` | `AsyncObservable` | ofAsyncSeq, toAsyncSeq, unwrapAggregate; **delete the file** |
| old `AsyncRx` facade | — | **deleted** (pure aliases) |

## Rename map (call sites, every file)

- `Cancel.` → `EventChoice.`
- `Merge.merge` → `AsyncObservable.merge`; `Merge.merge2` → `AsyncObservable.merge2`
- `Choice.amb` → `AsyncObservable.amb`; `Choice.orElse` → `AsyncObservable.orElse`;
  `Choice.firstValue` → `AsyncObservable.firstValue`
- `Subscribe.{subscribeJob,runJob,runBlocking,run,subscribe}` → `AsyncRx.*`
- `TaskInterop.{ofTask,ofHotTask,ofTaskFactory,toTaskList}` → `AsyncObservable.*`
- `AsyncRxAsyncSeq.{ofAsyncSeq,toAsyncSeq}` → `AsyncObservable.*`
- remove `open AsyncRxHopac.AsyncRxCE` and `open AsyncRxHopac.ClauseCE`
  (`asyncRx`/`clauses` now come from `open AsyncRxHopac`)

## Files to touch

- **`AsyncRxHopac.fsx`** — the restructure above.
- **`AsyncRxAsyncSeq.fsx`** — DELETE (content folded into kernel).
- **`AsyncRxHopac.Examples.fsx`** — drop the two CE opens; `Merge.merge`→`AsyncObservable.merge`;
  `Choice.amb`→`AsyncObservable.amb`; `Subscribe.subscribeJob`/`runBlocking`→`AsyncRx.*`.
- **`AsyncRxAsyncSeq.Examples.fsx`** — `#load "AsyncRxAsyncSeq.fsx"`→`#load "AsyncRxHopac.fsx"`;
  `AsyncRxAsyncSeq.toAsyncSeq`/`ofAsyncSeq`→`AsyncObservable.*`; `Merge.merge`→`AsyncObservable.merge`;
  `Subscribe.run`→`AsyncRx.run`. Keep `open FSharp.Control`. (Optional later: rename file.)
- **`AsyncRxHopac.Tests.fsx`** — `Cancel.`→`EventChoice.`; `Merge.`/`Choice.`/`Subscribe.` renames; fix opens.
- **`AsyncRxAsyncSeq.Tests.fsx`** — `#load`→kernel; `Cancel.`→`EventChoice.`; conversions→`AsyncObservable.*`; renames.
- **`style-guide.md`** — role-module table + examples to new homes (`AsyncObservable.merge`/`.amb`/`.ofTask`/`.ofAsyncSeq`/`.toAsyncSeq`, `AsyncRx.run`/`.subscribeJob`); rewrite the "facade" section (AsyncRx is now the run module, no alias-bag); open snippets drop the CE opens.
- **Memory** — update `prefer-qualified-calls.md` example names post-impl; update `asyncrx-hopac-status.md` status; `MEMORY.md` if needed.
- **`docs/AsyncRxHopac-fixup-plan.md`** — already historical/stale (still says `Operators`); leave or annotate, low priority.

## Verification (run all four; expected: green)

- `dotnet fsi AsyncRxHopac.Tests.fsx`  (~70 checks → ALL TESTS PASSED)
- `dotnet fsi AsyncSeqInterop.Tests.fsx`  (14 checks → ALL TESTS PASSED)
- `dotnet fsi AsyncRxHopac.Examples.fsx`  (clean demo output)
- `dotnet fsi AsyncSeqInterop.Examples.fsx`  (thermostat trace clean)

## Risk / pre-checked notes

- **Ordering linchpin:** `AsyncRx` (run) before `AsyncObservable` (build), because
  `toTaskList` → `AsyncRx.runJob`. Verified the run-family touches no `AsyncObservable`
  operator (only `Internal`, `EventChoice.cancel`, `Core`). Reads "run before build" in
  the file, which is fine — it's forced by the dependency.
- **No forward-ref folding Merge/Choice in:** `AsyncObservable`'s body never references
  `Merge.`/`Choice.` today (only the facade + ClauseCE + comments do). `bind`/flatMap does
  not use `merge`.
- **`EventChoice` module + `EventChoice<'T>` type already coexist** (companion pattern) in
  the current working kernel; absorbing `Cancel` is purely additive.
- **Kernel header:** remove the "kernel stays dependency-free" claim from comments — no
  longer true once AsyncSeq is in.
