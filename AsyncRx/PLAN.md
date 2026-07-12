# AsyncRx Library Conversion Plan

Status: stages 1–4 completed — the compiled library preserves the current
`AsyncRxHopac.fsx` design, and copied compatibility checks pass against it.

## Locked starting point

The library is converted from the current `AsyncRxHopac.fsx` implementation,
not from any earlier AsyncRx iteration. The resulting library preserves its
current public design:

- `Core` contains the core types.
- `EventChoice` is the public event-choice and cancellation substrate.
- `Internal` remains private implementation plumbing.
- `AsyncRx` is the consume/subscribe front door.
- `AsyncObservable` is the one canonical surface for sources, transforms,
  products, choice, Task interop, AsyncSeq interop, and the builder types.
- `asyncRx` and `clauses` remain available after `open AsyncRxHopac`.

The conversion must retain the existing serialized-callback, terminal-grammar,
backpressure, cancellation, resource-lifetime, and stack-safety behavior.
Earlier alias facades, split choice/merge modules, and the separate AsyncSeq
bridge are deliberately not restored.

## Target library layout

```text
AsyncRx/
  PLAN.md
  AsyncRxHopac.fsproj
  Core.fs
  EventChoice.fs
  Internal.fs
  AsyncRx.fs
  AsyncObservable.fs
  Tests/
```

The F# project compiles source in the displayed order. `AsyncObservable.fs`
contains the two computation-expression builder types and the small auto-open
bridge required to preserve the existing top-level `asyncRx` and `clauses`
consumer ergonomics.

## Stage 1 — Project scaffold

Create `AsyncRxHopac.fsproj` as an F# class library. Add source files in the
target order as their extraction stage lands, so an earlier stage never
references a file that does not yet exist. Add direct package references for
Hopac and FSharp.Control.AsyncSeq, pinning versions that are restored and
verified by the conversion.

Success condition: a correctly ordered library project restores and builds
without changing the existing scripts. **Completed 2026-07-12** with Hopac
`0.5.1` and FSharp.Control.AsyncSeq `4.15.0`.

## Stage 2 — Extract the foundational layers

Move the current `Core`, `EventChoice`, and `Internal` implementations into
their corresponding files under the `AsyncRxHopac` namespace. Preserve access
levels, especially `module internal Internal`, and avoid changing behavior or
public names while extracting.

**Extraction rule (locked for this stage):** copy each module body verbatim
from `AsyncRxHopac.fsx`. The only additions are the project file and the
namespace/import headers required because the copied modules now live in
separate compiled files. Do not rewrite, refactor, rename, reformat, or
otherwise edit their implementation.

Success condition: the library compiles through `Internal.fs`, with no
implementation copied from an earlier design. **Completed 2026-07-12.**

## Stage 3 — Extract the public runtime and operator surface

Move `AsyncRx` into `AsyncRx.fs`, followed by the complete `AsyncObservable`
surface into `AsyncObservable.fs`: sources, operators, choice/product helpers,
Task and AsyncSeq bridges, and computation-expression builders. Preserve the
current namespace and qualified consumer vocabulary.

Success condition: the compiled library exposes the same intended public
surface as the current script, including `AsyncObservable.map`,
`AsyncObservable.ofAsyncSeq`, `AsyncRx.run`, `asyncRx`, and `clauses`.
**Completed 2026-07-12.**

## Stage 4 — Copy and run compatibility checks

Copy the existing validation scripts into `AsyncRx/Tests/`, leaving every
original root-level script untouched. Update only the copied scripts' library
reference so they exercise the compiled `AsyncRxHopac` library; do not rewrite
their assertions or demonstrations. Build the library and run the copies of
all current AsyncRx validation artifacts:

- `AsyncRxHopac.Tests.fsx`
- `AsyncSeqInterop.Tests.fsx`
- `AsyncRxHopac.Examples.fsx`
- `AsyncSeqInterop.Examples.fsx`

Success condition: all copied checks and demos remain green against the
compiled library, while their original scripts remain unchanged at the
repository root. **Completed 2026-07-12.**

## Stage 5 — Convert validation artifacts deliberately

Only after Stage 4 is green, create separate compiled test and example projects
as a follow-up. Keep the two existing test suites distinct (kernel and
AsyncSeq interop), preserve their bounded waits and full notification-sequence
assertions, and do not introduce an unrelated test framework merely to replace
the current lightweight harness.

Success condition: validation is runnable through normal project commands and
continues to exercise the same behavioral contracts.
