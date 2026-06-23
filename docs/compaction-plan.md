# AsyncRx kernel — compaction plan

*— Claude (Opus 4.8), 2026-06-18*

Scope: reduce *structural duplication* in `AsyncRxHopac.fsx` without touching
behaviour. Every change below is mechanical and preserves the terminal grammar,
Stop plumbing, and serialized-callback contract. The point is fewer copies of
each invariant, not denser code.

## Status - 2026-06-18

Implemented by Codex:

- Added `Internal.forwardInto` and replaced the repeated source-subscribe /
  mailbox-funnel blocks in `switchLatest`, `debounce`, `zip`, `merge`, `amb`,
  and `firstValue`.
- Added `AsyncObservable.relayNext` and used it for the ordinary pass-through
  transform observers (`map`, `mapJob`, `filter`, `choose`, `scan`).
- Replaced the `amb` / `firstValue` per-source 4-tuples with a private
  `Choice.SourceEntry` record plus small start/cancel helpers.
- Left the deeper `amb` / `firstValue` shared setup refactor alone for now; the
  race loops are still the important readable part.

Verification:

- `dotnet fsi --use:.\AsyncRxHopac.fsx --exec`
- `dotnet fsi .\AsyncRxHopac.Tests.fsx` (71 checks)
- `dotnet fsi .\AsyncRxHopac.Examples.fsx`

Re-run the existing verification harness after each step; none of these should
change a single one of the checks.

## 1. `forwardInto` — collapse the subscribe-and-funnel block

The single highest-value change. This exact shape — start a job, subscribe a
source, forward each notification into the operator's mailbox, and turn a thrown
source into one `Error` — appears **8 times**:

| Operator | Site |
|---|---|
| `merge` | [AsyncRxHopac.fsx:882](../AsyncRxHopac.fsx) |
| `zip` (left) | [AsyncRxHopac.fsx:773](../AsyncRxHopac.fsx) |
| `zip` (right) | [AsyncRxHopac.fsx:790](../AsyncRxHopac.fsx) |
| `amb` | [AsyncRxHopac.fsx:958](../AsyncRxHopac.fsx) |
| `firstValue` | [AsyncRxHopac.fsx:1074](../AsyncRxHopac.fsx) |
| `switchLatest` (outer) | [AsyncRxHopac.fsx:507](../AsyncRxHopac.fsx) |
| `switchLatest` (inner) | [AsyncRxHopac.fsx:528](../AsyncRxHopac.fsx) |
| `debounce` | [AsyncRxHopac.fsx:639](../AsyncRxHopac.fsx) |

The repeated block:

```fsharp
Job.start <| job {
    try
        do! source innerCtx {
            OnNext      = fun x  -> send (Next x)
            OnError     = fun e  -> send (Error e)
            OnCompleted = fun () -> send Completed
        }
    with e ->
        do! send (Error e)
}
```

Only the three constructors that wrap a notification into the operator's message
type vary. One `internal` helper (alongside `giveOrStop`/`takeOrStop` in
`module internal Internal`) covers all 8:

```fsharp
/// Subscribe `source` on its own job, forwarding each notification into the
/// operator's mailbox via `onNext`/`onError`/`onCompleted`. A thrown source
/// becomes exactly one `onError`, matching the per-operator terminal funnel.
let forwardInto
    (ctx: SubscribeContext)
    (source: AsyncObservable<'T>)
    (onNext: 'T -> 'M) (onError: exn -> 'M) (onCompleted: 'M)
    (send: 'M -> Job<unit>)
    : Job<unit> =
    Job.start <| job {
        try
            do! source ctx {
                OnNext      = fun x  -> send (onNext x)
                OnError     = fun e  -> send (onError e)
                OnCompleted = fun () -> send onCompleted
            }
        with e ->
            do! send (onError e)
    }
```

Call sites become one line. For the `Notification`-based operators the
constructors *are* the mapping, so it reads directly:

```fsharp
// merge
for source in sources do
    do! Internal.forwardInto innerCtx source Next Error Completed send

// switchLatest inner — generation tagging stays explicit but compact
do! Internal.forwardInto innerCtx inner
        (fun x -> InnerNext (generation, x))
        (fun e -> InnerError (generation, e))
        (InnerCompleted generation)
        send
```

Payoff: ~80 fewer lines, and the "crash ⇒ exactly one `Error`" invariant lives
in one place instead of 8 copies that must stay in sync.

## 2. `relayNext` — forwarding observer for the synchronous transforms

`map` ([318](../AsyncRxHopac.fsx)), `mapJob` ([337](../AsyncRxHopac.fsx)),
`filter` ([353](../AsyncRxHopac.fsx)), `choose` ([365](../AsyncRxHopac.fsx)),
and `scan` ([463](../AsyncRxHopac.fsx)) each rebuild a full observer record only
to override `OnNext` and pass `OnError`/`OnCompleted` straight through.

A private helper in `module AsyncObservable`:

```fsharp
let private relayNext (onNext: 'T -> Job<unit>) (obs: AsyncObserver<'U>) : AsyncObserver<'T> =
    { OnNext = onNext; OnError = obs.OnError; OnCompleted = obs.OnCompleted }
```

```fsharp
let map f source =
    fun ctx obs -> source ctx (relayNext (fun x -> obs.OnNext (f x)) obs)

let filter pred source =
    fun ctx obs ->
        source ctx (relayNext (fun x -> if pred x then obs.OnNext x else Job.unit ()) obs)
```

The element-type change (`'U` observer → `'T` observer) is safe because
`OnError`/`OnCompleted` do not mention the element type. Payoff: drops the two
pass-through lines from each of the five operators and the visual noise of the
record literal.

## 3. Minor

- **`amb`/`firstValue` entries tuple → record.** Both build a `cancel, ctx, ch,
  source` 4-tuple and then destructure it as `(cancel, _, _, _)` everywhere
  ([amb:944](../AsyncRxHopac.fsx), [firstValue:1060](../AsyncRxHopac.fsx)). A
  small `private` record makes `e.Cancel` read better than
  `(fun (cancel, _, _, _) -> cancel)`:

  ```fsharp
  type private Clause<'T> =
      { Cancel: Cancel.Token
        Ctx: SubscribeContext
        Ch: Ch<Notification<'T>>
        Source: AsyncObservable<'T> }
  ```

- **`amb`/`firstValue` shared setup.** [`amb`](../AsyncRxHopac.fsx) (937) and
  [`firstValue`](../AsyncRxHopac.fsx) (1053) share ~40 lines of setup — entries,
  `send`, the start loop, and `cancelAll`/`cancelExcept`. Only the race differs
  (`amb` = first *notification* wins; `firstValue` = first *value* wins,
  dropping empty-completers). A shared `setupClauses` helper would factor the
  setup. Weigh this one carefully: the race is the interesting part and should
  stay legible, so factor only the plumbing, not the selection logic.

## Leave alone (intrinsic, not verbosity)

- The `mutable terminated`/`completed` flags and the `Cancel.cancel … ;
  obs.OnError e` sequences encode the terminal grammar; collapsing them risks
  the invariant.
- The `takeOrStop` → `Choice2Of2 () | Choice1Of2 msg` dispatch in each consumer
  loop looks repetitive, but every loop branches differently; abstracting it
  only adds indirection.
- The comment blocks (P2 rationale, stack-safety notes) are load-bearing.

## Net

Items 1 and 2 are the real wins: roughly 90 fewer lines and fewer copies of each
invariant. Item 3 is polish. All are behaviour-preserving and should leave the
verification suite green.
