# Why AsyncRx?

AsyncRx is a reactive-concurrency model for asynchronous push streams in F#.
It is built on Hopac because reactive events, concurrent producers, asynchronous
consumers, cancellation, and races are one problem here, not separate layers.

The defining contract is:

```fsharp
type AsyncObserver<'T> =
    { OnNext      : 'T -> Job<unit>
      OnError     : exn -> Job<unit>
      OnCompleted : unit -> Job<unit> }

type AsyncObservable<'T> =
    SubscribeContext -> AsyncObserver<'T> -> Job<unit>
```

The important difference is `OnNext : 'T -> Job<unit>`. Delivery is not a
synchronous callback that happens to launch asynchronous work. The asynchronous
work is part of delivery and can be awaited by the source or operator.

## The problem it addresses

Many event-driven systems naturally have all of these properties:

- values arrive as push events;
- several producers run concurrently;
- handling a value may be asynchronous;
- handling order and serialization matter;
- cancellation must race safely with I/O, timers, and incoming events;
- streams may complete, fail, switch, merge, or supersede one another.

Treating this as ordinary Rx often separates notification from asynchronous
handling. Treating it as an asynchronous sequence reverses the natural direction
by making the consumer pull. Treating it as raw channels leaves every application
to rebuild the stream protocol and operators.

The difficulty is not any single property — it is that they hold *at once*. At any
moment a timer, an incoming event, and a cancellation request are all things that
might happen next; the program must commit to exactly one, await the asynchronous
handling that choice triggers before moving on, and stay cancellable throughout.
Built from callbacks or tasks this becomes a thicket of queues, completion sources,
linked cancellation tokens, and ad-hoc schedulers — rebuilt for each operator. The
hard thing AsyncRx makes easy is exactly that: *wait for whichever event happens
next, await downstream handling before the source advances, and let cancellation
win the race* — written as one composable expression.

AsyncRx gives this problem its own abstraction.

## A concrete hard case

Suppose a controller is watching live device readings. On every reading it must
persist or publish the value asynchronously before accepting the next one; if the
device goes quiet for 500 ms it must emit a heartbeat; and while the loop is
waiting, cancellation must race directly against both the device and the timer.

As raw callbacks or tasks, this tends to become a small scheduler: one callback
for device input, one timer callback, a queue to serialize observer calls, a
`TaskCompletionSource` or channel to wake the loop, linked cancellation tokens,
and careful rules for what happens if cancellation arrives while a write is in
flight.

With Hopac and AsyncRx, the race is the shape of the code:

```fsharp
open Hopac
open AsyncRxHopac

type DeviceEvent =
    | Reading of int
    | Heartbeat

let readingsWithHeartbeat (readings: Ch<int>) : AsyncObservable<DeviceEvent> =
    AsyncObservable.repeatEventChoice (fun _ ->
        EventChoice.choose [
            EventChoice.take readings Reading
            EventChoice.afterMillis 500 Heartbeat
        ])
```

The source says only which event can happen next. `EventChoice.choose` builds a
pending first-to-fire event choice from named alternatives, and
`repeatEventChoice` owns the reusable stream machinery: race that choice against
`Stop`, emit each chosen value, await `OnNext`, and then repeat. If the observer
writes to a database, sends an HTTP request, or updates a serialized state
machine, that work is part of delivery. The source does not advance to the next
reading or heartbeat until the chosen notification has been handled.

This is the ergonomic boundary AsyncRx should preserve: application and operator
code should use the noun-based `EventChoice` API, such as `EventChoice.choose`,
`EventChoice.take`, and `EventChoice.afterMillis`. Hopac's symbolic operators
should stay hidden behind named helpers; they should not be the project idiom.

## What kind of state machine?

AsyncRx is not mainly for pure, synchronous state transitions such as "given an
input value, compute the next value." It is for event-driven asynchronous state
machines, where each state has several possible things that might happen next:
an external event, a timeout, an operation completing, an error, or cancellation.

For example, a connection manager might have this shape:

```text
Disconnected
  connect requested -> Connecting
  Stop              -> Stopped

Connecting
  connected         -> Running
  connect failed    -> BackingOff
  timeout           -> BackingOff
  Stop              -> Stopped

Running
  reading arrives   -> persist reading, then Running
  config changes    -> reconfigure, then Running
  heartbeat due     -> send heartbeat, then Running
  connection lost   -> BackingOff
  Stop              -> Stopped

BackingOff
  retry timer fires -> Connecting
  Stop              -> Stopped
```

The useful part is that the transition trigger may be a push stream, a timer, a
task result, or cancellation, and the transition action may itself be
asynchronous. The machine can wait for the first relevant trigger, perform the
transition's asynchronous work, and only then wait for the next trigger.

In this library, the pieces map directly to that shape:

- `unfoldEventChoice` drives a state machine whose next transition is selected by
  an `EventChoice<'T>`;
- `EventChoice<'T>` expresses "which event-like thing happens next?";
- `OnNext : 'T -> Job<unit>` lets a transition await downstream handling;
- `clauses { }` expresses "take the first matching transition";
- `switchLatest` handles sub-machines or operations superseded by newer input;
- `Stop` remains one of the events the machine can race at each wait point.

## Why not just Rx.NET?

Rx.NET models asynchronous *arrival* well, but observer notification is
synchronous:

```fsharp
IObserver<'T>.OnNext : 'T -> unit
```

An `OnNext` handler can start a `Task`, but that task is outside the notification
contract. The producer does not naturally await it. Preserving order, propagating
failure, limiting concurrency, and cancelling that work then requires additional
operators, queues, schedulers, or conventions.

AsyncRx puts asynchronous handling inside the contract:

```fsharp
OnNext : 'T -> Job<unit>
```

This provides:

- asynchronous acknowledgement of each notification;
- natural in-order processing for a sequential source;
- direct composition with cancellation, channels, timeouts, and races;
- no `async void`-style observer boundary;
- F# computation-expression sequencing over streams.

This does **not** mean AsyncRx is universally better than Rx.NET. Rx.NET has a
large operator set, mature scheduler semantics, extensive documentation, and
broad .NET interoperability. Use Rx.NET when those are the dominant requirements.

Use AsyncRx when asynchronous consumption and concurrent coordination are part of
the stream's semantics rather than implementation details behind `OnNext`.

## Why not `AsyncSeq`?

`AsyncSeq` is pull-based. The consumer requests the next value and therefore
provides inherent demand control. It is an excellent fit for:

- asynchronous iteration;
- pagination and database reads;
- files and network responses;
- transformations driven by one consumer.

AsyncRx is push-based. Sources initiate notifications. It is a better fit for:

- UI, device, and domain events;
- timers and external callbacks;
- hot or independently running producers;
- merging or racing concurrent sources;
- switching to the latest source;
- state machines driven by whichever event occurs next.

Neither model subsumes the other cleanly. Converting a naturally pushed system
into a pull sequence usually introduces a queue. Converting a naturally pulled
resource into a push stream may discard useful demand semantics.

## Why Hopac?

AsyncRx exposes pending selectable operations as `EventChoice<'T>`. The public
idiom is named and noun-based:

```fsharp
type Next<'T> =
    | Stopped
    | Event of 'T
    | TimedOut

EventChoice.choose [
    EventChoice.stop ctx Stopped
    EventChoice.take events Event
    EventChoice.afterMillis timeout TimedOut
]
```

This says "wait for whichever of these alternatives happens first" without using
Hopac's symbolic operators. Underneath, `EventChoice<'T>` is Hopac `Alt<'T>`.

Hopac supplies the concurrency algebra that makes this possible:

- `Job<'T>` describes asynchronous work;
- `Ch<'T>` provides synchronous channel rendezvous;
- `Alt<'T>` is the underlying representation of `EventChoice<'T>`;
- `IVar<'T>` provides a single-assignment synchronization point.

Together these are the **Communicating Sequential Processes (CSP)** model:
independent processes coordinate by passing messages over channels instead of
sharing mutable state, and *selective communication* lets one offer several
possible communications and proceed with the first to fire.

Hopac's decisive refinement of classical CSP is that this underlying `Alt<'T>` is
**first-class**. That means an AsyncRx `EventChoice<'T>` is a value — built,
transformed, passed to other code, and combined before it commits — not a fixed
`select` block. This is what makes races and cancellation part of an operator's
*structure* rather than bookkeeping bolted around it, and it hands the hard part
of concurrent event coordination to the runtime instead of re-implementing it in
every operator.

AsyncRx is therefore not a replacement for Hopac. Hopac is the runtime and
coordination toolkit; AsyncRx is a stream protocol and operator layer built on it.

## Relation to Go channels

Hopac channels and Go channels share CSP-style communication:

- producers and consumers synchronize through typed channels;
- sending and receiving may suspend;
- communication can replace shared-state coordination;
- several possible operations can be raced.

The rough correspondence is:

| Go | Hopac / AsyncRx |
|---|---|
| goroutine | `Job.start` / concurrent `Job` |
| channel | `Ch<'T>` |
| `select` | composed `EventChoice<'T>` / Hopac `Alt<'T>` values |
| channel-based application protocol | `AsyncObservable<'T>` |

A raw channel carries values. It does not by itself define:

- error and completion notifications;
- legal terminal ordering;
- subscription cancellation;
- observer serialization;
- operators such as `debounce`, `switchLatest`, `merge`, or `zip`.

AsyncRx adds that protocol above Hopac channels. Unlike Go channels, which may be
buffered or unbuffered, Hopac `Ch<'T>` is a synchronous rendezvous channel;
buffering is explicit and belongs to the operator or application.

## Why `Job`, not F# `Async`?

F# `Async<'T>` is a capable general-purpose workflow abstraction, especially for
.NET asynchronous APIs. It does not provide Hopac's native model of transactional
choice and channel rendezvous.

Building these operators on `Async` would require more explicit machinery for:

- racing cancellation, channel input, and timers;
- arbitrating several producers;
- representing selectable operations as values;
- coordinating completion without detached workflows;
- implementing synchronous hand-off and backpressure.

The reason to use `Job` is not syntax or benchmark folklore. It is that `Job`,
`Alt`, `Ch`, and `IVar` form the concurrency model the operators need.

## Why `Job`, not `Task`?

`Task<'T>` is the standard .NET representation of one eventual result. It is the
right boundary for most .NET I/O APIs, but a weaker internal algebra for
fine-grained stream coordination:

- tasks are commonly eager;
- cancellation is advisory and token-based;
- `Task.WhenAny` races results but does not provide channel rendezvous;
- repeated coordination tends to require queues, `TaskCompletionSource`, linked
  tokens, and custom arbitration.

AsyncRx should accept and expose tasks at interoperability boundaries, then use
Hopac internally. `ofTaskFactory` follows that design: a task is created at
subscription time, its cancellation token is scoped to that subscription, and
its outcome enters the stream protocol.

## What acknowledgement guarantees

Awaiting `OnNext` gives a sequential source per-notification acknowledgement: the
source does not advance until downstream handling completes.

That is not a claim of universal bounded backpressure.

Multi-source operators have their own policies:

- `merge` serializes downstream delivery through an internal channel;
- `amb` selects one source and cancels the losers;
- `firstValue` (and the `clauses` DSL) selects the first source to emit a *value*,
  discards clauses that complete without emitting, and cancels the rest;
- `switchLatest` cancels the superseded inner source;
- `zip` queues unmatched values and can grow without bound if one side outruns
  the other;
- cancellation is cooperative, so external work may take time to stop.

Each operator must document whether it blocks, buffers, drops, switches, or
cancels. "Asynchronous acknowledgement" is the primitive; boundedness is an
operator-level semantic decision.

## Core semantic commitments

For each subscription, AsyncRx commits to:

1. **Serialized callbacks.** `OnNext`, `OnError`, and `OnCompleted` are not
   invoked concurrently.
2. **Terminal grammar.** Zero or more `OnNext` calls are followed by at most one
   `OnError` or `OnCompleted`, with no later notifications.
3. **Cooperative cancellation.** `Stop` suppresses further work and propagates
   through owned child contexts.
4. **Observable source completion.** `Subscription.Completion` fires when the
   subscription's source job has exited.
5. **Owned resource lifetimes.** Sources such as `ofSeq` acquire resources at
   subscription time and dispose them on completion, failure, or Stop.

`Subscription.Completion` means the source job has finished. It does not promise
that arbitrary external work has already honored a cancellation request.

## Composition over streams

Two computation expressions cover the common ways a program combines streams.

**`asyncRx { }` — sequential and applicative.** `let!` binds a value, `and!` pairs
sources applicatively (a `zip` product), and `for` / `while` / `do!` sequence
effects. This is *combine values from several sources*. Because `and!` is `zip`,
multi-value sources inherit `zip`'s queueing policy: unmatched values are buffered
until their counterpart arrives.

```fsharp
asyncRx {
    let! x = a
    and! y = b
    return x + y
}
```

**`clauses { }` — selection.** Each clause is a stream, guarded by a match
(`whenValue` / `guardOn` / `case`) or left unguarded (`always`); the block resolves
to the first clause to emit a **value**. This is *take whichever matches first* —
the shape of an event-driven state machine, where the hard part is normally
arbitrating the race and tearing down the clauses that lost. `always` is not an
ordered fallback; it enters the same race as every other clause:

```fsharp
clauses {
    whenValue a ((=) true) "a was true"
    whenValue b ((=) true) "b was true"
    always (asyncRx { ... })
}
```

`clauses` is a first-value race, not ordered pattern matching: every clause is
subscribed, and a clause that completes **without** emitting (a non-matching
`case`, a false `guardOn`) is discarded rather than allowed to win — so a slower
clause that actually matches still gets selected. That is the difference from plain
`amb`, where the first *notification*, including a completion, wins.

## Where AsyncRx fits

AsyncRx is a strong fit for:

- reactive F# systems already using Hopac;
- concurrent state machines;
- device, control, telemetry, and automation pipelines;
- event handlers that perform asynchronous work;
- protocols involving timeouts, races, and cancellation;
- applications combining several live event sources;
- workflows where a newer stream supersedes an older one.

It is a weaker fit for:

- straightforward pull-based data processing;
- applications primarily invested in the Rx.NET ecosystem;
- public .NET APIs that need conventional `IObservable` interoperability;
- systems requiring strict bounded-memory behavior without explicit policies;
- cases where a plain `Task`, `Async`, or channel is sufficient.

## Positioning

AsyncRx should not be presented as "Rx.NET, but smaller" or "`AsyncSeq` with
events." Its design center is:

> **Reactive, concurrent, asynchronous push streams for F#, built on Hopac.**

Its distinctive value is the combination of:

1. push-based event delivery;
2. asynchronous observer acknowledgement;
3. compositional races and cancellation;
4. serialized stream semantics;
5. F# computation expressions for sequential, applicative, and selection
   composition.

The library succeeds when these semantics are precise, tested, and visible in
each operator. 
