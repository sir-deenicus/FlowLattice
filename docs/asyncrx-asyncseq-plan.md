# AsyncRx ↔ AsyncSeq pairing — bridge + duplex demo plan

*— Claude (Opus 4.8), 2026-06-20*

> **Status: as-built (implemented & verified 2026-06-20).** Shipped
> [`AsyncRxAsyncSeq.fsx`](../AsyncRxAsyncSeq.fsx) (the two bridges),
> [`AsyncRxAsyncSeq.Tests.fsx`](../AsyncRxAsyncSeq.Tests.fsx) (**14 checks, all
> green** via `dotnet fsi AsyncRxAsyncSeq.Tests.fsx`), and
> [`AsyncRxAsyncSeq.Examples.fsx`](../AsyncRxAsyncSeq.Examples.fsx) (the
> thermostat demo; runs clean). The design below stands; the deviations found at
> implementation time are recorded in **"As-built deltas"** just under it. Read
> that section before trusting the `ofAsyncSeq` skeleton — the real AsyncSeq
> package is BCL-`IAsyncEnumerable`-based, not the classic `MoveNext`.
>
> **Superseded by the 2026-06-21 module consolidation** (see
> [`docs/module-consolidation-plan.md`](module-consolidation-plan.md)): the two
> bridges were folded into the kernel's `AsyncObservable` and **`AsyncRxAsyncSeq.fsx`
> was deleted**; the demo/test scripts were renamed to
> [`AsyncSeqInterop.Examples.fsx`](../AsyncSeqInterop.Examples.fsx) and
> [`AsyncSeqInterop.Tests.fsx`](../AsyncSeqInterop.Tests.fsx), and the conversions are
> now `AsyncObservable.ofAsyncSeq`/`.toAsyncSeq`. The plan body below is frozen as
> written on 2026-06-20 (its file names + `module AsyncRxAsyncSeq` are historical).

## As-built deltas (2026-06-20)

1. **AsyncSeq 4.15.0 is BCL-based, not classic.** `FSharp.Control.AsyncSeq<'T>`
   is a type *abbreviation* for `System.Collections.Generic.IAsyncEnumerable<'T>`
   (confirmed by reflection — the package exports no `IAsyncEnumerator` of its
   own). So the enumerator API is `GetAsyncEnumerator(ct)` /
   `MoveNextAsync () : ValueTask<bool>` / `Current` / `DisposeAsync ()`, **not**
   the classic `MoveNext () : Async<'T option>` + synchronous `Dispose` the
   skeleton below assumes. `ofAsyncSeq` therefore (a) bridges each step with
   `Job.fromAsync (Async.AwaitTask (….AsTask()))`; (b) unwraps a single-inner
   `AggregateException` (`unwrapAggregate`, mirroring the kernel's P2-8) so a
   throwing source surfaces its real message; (c) uses `Job.tryFinallyJob` with
   an **async** disposal finalizer (BCL `DisposeAsync` is async; the kernel's
   synchronous `try/finally` can't host it). The lifecycle ordering
   (dispose-before-complete; a disposal failure becomes the single terminal) is
   preserved exactly.
2. **`toAsyncSeq` teardown uses `Hopac.start (Cancel.cancel token)`, not
   `Hopac.run`.** The cancel is an idempotent, non-blocking `IVar.tryFill`;
   firing it fire-and-forget avoids any `Hopac.run`-from-a-worker-thread hazard
   inside AsyncSeq's synchronous `finally`. Identical for teardown — the entry
   script blocks on `Subscription.Completion`, keeping the scheduler live to
   process the cancel.
3. **Both nuget refs live in `AsyncRxAsyncSeq.fsx` before the `#load`.** A
   `#r "nuget:"` nested inside a script that is itself `#load`ed another level
   down does not reliably resolve, so the kernel's Hopac reference is restated at
   the interop level (depth-1 from the entry scripts — the proven pattern).
   (Also: avoid the literal tokens `#` and `*)` inside `(* *)` comments — F#
   still lexes them and breaks the build.)
4. **Demo teardown is structural, not handler-driven.** The controller bounds its
   pull with `AsyncSeq.take 6`; when that loop ends the readings enumerator is
   disposed → `toAsyncSeq` cancels its token → R's feed stops. The final `Halt`
   is *informational* (delivered to the handler for the trace), not the thing
   that stops R. This is cleaner than the planned "handler cancels R's feed"
   (R's token lives inside `toAsyncSeq` and isn't exposed) and still proves both
   initiation directions and a clean cross-link shutdown. The verified trace
   shows R's "producing" lines landing at P's ~150ms pull cadence (backpressure),
   commands flowing P→R, then a clean unwind after `Halt`.

Goal: pair the push-shaped kernel (`AsyncObservable`, AsyncRx-native) with the
pull-shaped `FSharp.Control.AsyncSeq` so that **two independent, paradigm-native
systems can be coupled into one feedback loop where either side initiates.** The
deliverable is (1) a small bidirectional bridge — `ofAsyncSeq` / `toAsyncSeq` —
and (2) a small demo of two systems running independently but coupled through it.

This is **new capability**, not a kernel fix, so it lives in its own doc and its
own file (the core `AsyncRxHopac.fsx` stays dependency-free). Lifecycle mirrors
the other plans: this doc starts as a plan and becomes the as-built record.

## Why this is the right demo

A closed loop between a pull system and a push system is the *only* shape that
forces **both** bridge directions into use and shows **both** initiation paths in
one artifact. Pull and push are duals collapsed by a synchronous rendezvous: at
the meeting point neither side is privileged as "the initiator" — the initiator
is simply whoever's offer is ready first. So "either one initiating" is not a new
primitive; it falls out of wiring the two bridges into a loop.

## The shape

```
        ofAsyncSeq  (P initiates: P emits a command, R reacts)
      ┌───────────────────────────────────────────────┐
      │                                                 ▼
 ┌─────────┐                                       ┌─────────┐
 │    P    │  pull / AsyncSeq-native               │    R    │  push / AsyncRx-native
 │ control │  (driver loop, its own trigger)       │  feed   │  (emits on its own clock)
 └─────────┘                                       └─────────┘
      ▲                                                 │
      └───────────────────────────────────────────────┘
        toAsyncSeq  (R initiates: R emits a reading, P paces)
```

- **R → `toAsyncSeq` → P** — R is an event source emitting on its own clock
  (*R initiates*). P consumes by pulling; the `Ch` rendezvous makes R block until
  P takes, so *P paces R* (backpressure).
- **P → `ofAsyncSeq` → R** — P, on its own schedule, decides to emit a command
  (*P initiates*). R subscribes and reacts; R's `OnNext` ack paces P.

Key property: **backpressure is symmetric, initiation is asymmetric.** Both edges
are flow-controlled by a rendezvous; which side *starts* an exchange differs per
edge. That is "either one initiating," realised without a contended symmetric
channel.

## Decisions taken

1. **Two directional bridges, not one symmetric duplex channel.** The feedback
   loop above gives both initiation directions cleanly. A single symmetric
   channel where both sides race to send on the same medium (the "crossing
   sends" deadlock) is protocol-grade and **out of scope**; noted below as the
   harder generalisation.
2. **The bridge lives in a separate file**, `AsyncRxAsyncSeq.fsx`, so the kernel
   stays dependency-free. Only the interop file and the demo take the AsyncSeq
   dependency — matching how `Examples` / `Tests` are already split out.
3. **Dependency `FSharp.Control.AsyncSeq` is in.** Approved as worth it for
   ecosystem interop. The interop file adds `#load "AsyncRxHopac.fsx"` then
   `#r "nuget: FSharp.Control.AsyncSeq"` and `open FSharp.Control`.
4. **Clean two-sided termination** — one `Stop` command unwinds the whole loop
   (see "Termination cascade"). No half-open / partial-shutdown state machinery
   in a demo.

## Bridge 1 — `ofAsyncSeq : AsyncSeq<'T> -> AsyncObservable<'T>` (pull → push)

The easy direction, and a near-exact mirror of `ofSeq`
([AsyncRxHopac.fsx:359](../AsyncRxHopac.fsx)): acquire the enumerator inside the
protected scope, iterate with a **stack-safe Hopac `while`** loop (not
`return! loop ()`), **dispose before completing**, turn any
acquisition/iteration/disposal failure into **exactly one `OnError`**, and stay
**Stop-aware**. The only change from `ofSeq` is that each step is an async pull:
`IAsyncEnumerator.MoveNext () : Async<'T option>`, bridged into the Hopac job with
`Job.fromAsync`.

```fsharp
let ofAsyncSeq (xs: AsyncSeq<'T>) : AsyncObservable<'T> =
    fun ctx obs -> job {
        // ... mirror ofSeq's terminated flag + forwardError + dispose helpers ...
        let e = xs.GetEnumerator ()              // IAsyncEnumerator<'T>
        try
            let mutable go = not (ctx.IsStopped ())
            while go do
                let! next = Job.fromAsync (e.MoveNext ())   // Async<'T option> -> Job
                match next with
                | Some x ->
                    do! obs.OnNext x             // awaited => demand-driven backpressure
                    go <- not (ctx.IsStopped ())
                | None ->
                    go <- false
                    // dispose-before-complete, exactly as ofSeq orders it
        with ex -> // -> exactly one OnError
        finally
            e.Dispose ()
    }
```

Backpressure falls out for free: `do! obs.OnNext x` awaits the downstream Job
before the next `MoveNext`, so the pull is paced by the consumer. **Reuse `ofSeq`'s
exact lifecycle ordering** (dispose then complete; a disposal failure becomes the
single terminal) rather than re-deriving it.

## Bridge 2 — `toAsyncSeq : AsyncObservable<'T> -> AsyncSeq<'T>` (push → pull)

The interesting direction. Normally push→pull is the footgun: push doesn't wait
to be asked, so a naive bridge buffers, and buffering unbounded is the classic Rx
leak. **Backpressure removes the policy decision entirely:** bridge through a
Hopac `Ch` (synchronous rendezvous). The producer's `OnNext` *gives* on the
channel; the AsyncSeq generator *takes* on each pull; the give does not complete
until the take happens. Lossless, bounded, no buffer-size knob.

```fsharp
let toAsyncSeq (source: AsyncObservable<'T>) : AsyncSeq<'T> =
    asyncSeq {
        let token, ctx = Internal.rootContext ()
        let ch = Ch<Notification<'T>> ()
        // Producer: subscribe source, funnel every notification into ch, each
        // give racing Stop so disposal withdraws a blocked give. This is exactly
        // the kernel's forwardInto + giveOrStop pairing.
        do! Job.toAsync (Internal.forwardInto ctx source Next Error Completed (Internal.giveOrStop ctx ch))
        try
            let mutable go = true
            while go do
                let! n = Job.toAsync (Ch.take ch)    // pull == take (rendezvous)
                match n with
                | Next x    -> yield x
                | Completed -> go <- false
                | Error e   -> raise e               // surface fault on the seq
        finally
            // normal end OR early disposal (consumer stopped pulling): stop the
            // source so a blocked giveOrStop is withdrawn and the sub unwinds.
            Hopac.run (Cancel.cancel token)
    }
```

Why each piece:
- **`Ch<Notification<'T>>`** carries the terminal in-band, reusing the kernel's
  `Notification` ([AsyncRxHopac.fsx:84](../AsyncRxHopac.fsx)) — `Completed` ends
  the seq, `Error` raises on the seq, `Next` yields. No side completion signal
  needed.
- **`Internal.forwardInto` + `Internal.giveOrStop`**
  ([205](../AsyncRxHopac.fsx) / [187](../AsyncRxHopac.fsx)) are reused verbatim:
  `forwardInto` is the "subscribe on a job, funnel notifications, one error on
  throw" block; `giveOrStop` makes the give a rendezvous that loses to Stop.
- **`finally → Cancel.cancel token`** is the teardown edge. Early disposal (the
  consumer stops enumerating) cancels the token; the producer's blocked
  `giveOrStop` sees Stop win and unwinds — no leaked source job. AsyncSeq's
  `finally` compensation is synchronous, so `Hopac.run` of the idempotent
  `IVar.tryFill` is the right fit.

## The one integration risk — the Async ↔ Job boundary

AsyncSeq is built on `Async`; the kernel is built on `Job`. The bridge therefore
crosses runtimes at every step: `Job.fromAsync (e.MoveNext ())` in `ofAsyncSeq`,
`Job.toAsync (Ch.take ch)` in `toAsyncSeq`. Both conversions propagate exceptions
faithfully (an Async fault surfaces as a Job fault and vice-versa), so the
terminal grammar is preserved — this is a **correctness-safe but not free**
boundary: each element pays a scheduler hop between the Hopac pool and the Async
pool. Acceptable for interop; call it out so nobody mistakes the bridge for a
zero-cost adapter. Do **not** put either bridge on a hot inner path expecting
Hopac-native throughput.

## Packaging note — `internal` visibility

`toAsyncSeq` reuses `Notification`, `Internal.forwardInto`, `Internal.giveOrStop`,
`Internal.rootContext`, and `Cancel.*`, several of which are `internal`. This is
fine while the interop file is part of the **same script assembly** (it `#load`s
the kernel, so FSI compiles both into one assembly). If the kernel is ever built
as a compiled library with the interop in a *separate* assembly, these need
`InternalsVisibleTo` or promotion to public. `ofAsyncSeq` needs nothing internal
and could even live on the public surface.

## The demo — a thermostat-style feedback loop

Two systems, each a genuine driver, coupled by both bridges:

- **R — sensor feed (push / AsyncRx-native).** Emits a reading every tick on its
  own clock (`AsyncObservable.intervalMillis` → an incrementing / noisy value).
  *R initiates* by emitting. Free-running: commands only ever *stop* it, they do
  not gate value production — this is what de-cycles construction.
- **P — controller (pull / AsyncSeq-native).** An `asyncSeq` transform
  `AsyncSeq<Reading> -> AsyncSeq<Command>`: it enumerates readings (pull, at a
  deliberately slower pace via `Async.Sleep` to make backpressure *visible*),
  prints what it sees, and on its own policy (threshold crossed / after N
  readings) **yields a command** — *P initiates* — ending with a final `Stop`.

P must be an honest loop with its own decision logic; the `asyncSeq` transform
*is* that loop. If P were a bare `AsyncSeq` it could not initiate, and the demo
would silently degenerate into "R drives everything."

**Wiring (de-cycled — the only back-edge is lifecycle, not data):**

1. `readings  : AsyncObservable<Reading>`  — free-running ticker.
2. `readingsSeq = toAsyncSeq readings`      — R→P (R-initiated, P-paced).
3. `commandsSeq = controller readingsSeq`   — P's policy loop, yields commands.
4. `commandsObs = ofAsyncSeq commandsSeq`   — P→R (P-initiated).
5. Subscribe `commandsObs`; the handler applies commands to R — on `Stop`, cancel
   R's feed. Await both `Subscription.Completion`s for deterministic teardown.

**Termination cascade** (one Stop unwinds everything, in order): controller
yields `Stop` → `commandsObs` delivers it → handler cancels R's feed → `readings`
completes → `toAsyncSeq` gives `Completed` → `readingsSeq` ends → controller's
enumeration ends → `commandsSeq` ends → `commandsObs` completes. Both Completions
fire; nothing is left blocked.

**What it proves, in one run:** both bridges exercised; both initiation
directions; backpressure on both edges (P's slow pull throttles R; R's ack paces
P's command delivery); and a clean cross-link shutdown.

## Verification

Bridge unit checks (new `AsyncRxAsyncSeq.Tests.fsx`, same harness style as
`AsyncRxHopac.Tests.fsx` — count assertions, exit non-zero on failure):

- `ofAsyncSeq`: forwards all elements in order; empty seq → just `OnCompleted`;
  a throwing `MoveNext` → exactly one `OnError`, no value after; Stop mid-stream
  halts pulling and disposes the enumerator; backpressure — a slow `OnNext`
  paces `MoveNext` (no read-ahead past one element).
- `toAsyncSeq`: lossless + ordered; source `OnCompleted` → seq ends; source
  `OnError` → seq raises that exception; **bounded** — producer's give blocks
  until the consumer pulls (assert no buffering ahead of demand); **early
  disposal stops the source** — disposing the enumerator before completion fires
  the source's Stop and its `Subscription.Completion`.
- **Round-trip:** `xs |> ofSeq-as-asyncSeq |> ofAsyncSeq |> toAsyncSeq` returns
  `xs` (identity through both bridges).

Demo runs as a script (`AsyncRxAsyncSeq.Examples.fsx`) and prints an interleaved
trace showing R's emissions throttled by P and P's commands landing at R, ending
with the termination cascade.

## Files changed

- **`AsyncRxAsyncSeq.fsx`** (new) — `#load "AsyncRxHopac.fsx"`, the AsyncSeq dep,
  and the two bridges in a `module AsyncRxAsyncSeq` (or extend `AsyncRx`).
- **`AsyncRxAsyncSeq.Examples.fsx`** (new) — the thermostat demo.
- **`AsyncRxAsyncSeq.Tests.fsx`** (new) — the checks above.
- **Memory** — add an index line for this doc under `asyncrx-hopac-status.md`
  once it lands (and flip this doc to as-built).

## Risks / trade-offs

- **Async↔Job hop per element** (above) — the real cost; document, don't hide.
- **`internal` reuse** ties the interop to the same assembly (above).
- **AsyncSeq `finally` is synchronous** — teardown uses `Hopac.run` of an
  idempotent cancel; fine because the cancel is a non-blocking `IVar.tryFill`.
- **Demo construction is de-cycled deliberately.** A true data cycle (commands
  gate value production) would need the symmetric-channel protocol layer
  (out of scope). Keeping commands as *lifecycle-only* control is what keeps the
  demo small and acyclic.

## Out of scope (the harder generalisation)

A single **symmetric duplex channel** where both peers may send simultaneously —
the "crossing sends" problem — needs each side's main wait to be an
`Alt.choose [ takeInbound; giveOutbound; stop ]` so every side is at once willing
to send and receive, plus a real termination protocol (who closes, half-open
states, error propagation across the link). That is a *protocol layer*, not a
stream combinator, and would be its own plan. The first-class composable `Alt`
that makes it tractable is already in the kernel (`SubscribeContext.Stop` is the
same shape); this plan deliberately stops short of it.

## Sequencing

1. ✅ `ofAsyncSeq` (BCL `IAsyncEnumerable` pull) + its checks.
2. ✅ `toAsyncSeq` (`Ch` rendezvous) + its checks, incl. early-disposal teardown.
3. ✅ Round-trip check.
4. ✅ Thermostat demo + the interleaved-trace run.
5. ✅ Flipped this doc to as-built; memory status updated.

All 14 bridge checks pass (`dotnet fsi AsyncRxAsyncSeq.Tests.fsx`); the demo runs
clean (`dotnet fsi AsyncRxAsyncSeq.Examples.fsx`).
