namespace AsyncRxHopac

open Hopac

module Core =

    type AsyncObserver<'T> =
        { OnNext      : 'T -> Job<unit>
          OnError     : exn -> Job<unit>
          OnCompleted : unit -> Job<unit> }

    /// A pending event choice. AsyncRx exposes this named alias for Hopac `Alt`
    /// so public examples can talk about event choices without leaning on
    /// symbolic Hopac operators.
    type EventChoice<'T> =
        Alt<'T>

    type Subscription =
        { /// Stop the subscription (idempotent): cancels the root context so the
          /// source's cooperative Stop checks unwind it.
          Stop : unit -> Job<unit>
          /// Fires once the subscription's source job has finished — after a
          /// terminal (`OnError`/`OnCompleted`), after Stop-driven unwinding, or
          /// after a thrown source/observer (routed through the boundary gate).
          /// Lets callers await deterministic teardown instead of polling/timeouts.
          Completion : EventChoice<unit> }

    type SubscribeContext =
        { Stop      : EventChoice<unit>
          IsStopped : unit -> bool }

    // Observable source contract
    // ---------------------------
    // An `AsyncObservable` invokes its observer under two rules:
    //
    //   1. Serialized callbacks. `OnNext`/`OnError`/`OnCompleted` are never
    //      invoked concurrently for one subscription. Multi-source operators
    //      (`merge`, `zip`, `amb`, `switchLatest`) uphold this by funnelling
    //      every inner notification through a single channel + consumer loop,
    //      so a downstream observer only ever sees one callback at a time.
    //      Because of this, operator/boundary terminal state can be a plain
    //      mutable flag rather than a synchronized primitive.
    //
    //   2. Terminal grammar: zero or more `OnNext`, then *at most one* of
    //      `OnError` / `OnCompleted`, and nothing afterwards.
    //
    // `AsyncObservable<'T>` is a public function type, so a hand-written source
    // can violate rule 2 (double terminal, value-after-terminal, throw-after-
    // error). The subscription boundary therefore wraps the observer in
    // `Internal.terminalGate` as a final defense. Combinators that own more than
    // one source or cleanup work still apply their own terminal guard — the
    // boundary gate is the last line, not a substitute for correct operator
    // lifecycle.
    type AsyncObservable<'T> =
        SubscribeContext -> AsyncObserver<'T> -> Job<unit>

    type internal Notification<'T> =
        | Next of 'T
        | Error of exn
        | Completed
