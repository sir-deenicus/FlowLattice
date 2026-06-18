(*
    AsyncRxHopac.fs

    A compact AsyncRx-style kernel for F# built on Hopac.

    Core ideas:
      - Async push: OnNext returns Job<unit>, so downstream async work can apply backpressure.
      - Hopac-native cancellation: broadcast cancellation via IVar<unit>.
      - Rx-like operators: map, filter, choose, scan, take, merge, amb, debounce, switchLatest.
      - Combinators by algebraic role: Merge (interleave); Choice (amb/orElse first-to-emit,
        firstValue clause selection); and the ordinary AsyncObservable transforms/products
        (map/filter/bind/zip/bothOnce). The clauses { } DSL selects the first *matching*
        clause (joinad-style) via Choice.firstValue over AsyncObservable.guard.
      - Small ergonomic DSL: asyncRx { ... } and clauses { ... }.

    Package dependency:
      - Hopac

    Typical package reference:
      <PackageReference Include="Hopac" Version="*" />
*)

#r "nuget: Hopac, 0.5.1"

open System
open System.Threading
open System.Threading.Tasks
open Hopac
open Hopac.Infixes

[<AutoOpen>]
module Core =

    type AsyncObserver<'T> =
        { OnNext      : 'T -> Job<unit>
          OnError     : exn -> Job<unit>
          OnCompleted : unit -> Job<unit> }

    type Subscription =
        { /// Stop the subscription (idempotent): cancels the root context so the
          /// source's cooperative Stop checks unwind it.
          Stop : unit -> Job<unit>
          /// Fires once the subscription's source job has finished — after a
          /// terminal (`OnError`/`OnCompleted`), after Stop-driven unwinding, or
          /// after a thrown source/observer (routed through the boundary gate).
          /// Lets callers await deterministic teardown instead of polling/timeouts.
          Completion : Alt<unit> }

    type SubscribeContext =
        { Stop      : Alt<unit>
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

module Cancel =

    // Cancellation is represented purely by the IVar latch. A real
    // CancellationToken is created only at boundaries that need one (see
    // TaskInterop.ofTaskFactory), with an owned, bounded lifetime.
    type Token =
        private
            { Latch : IVar<unit> }

    let create () : Token =
        { Latch = IVar<unit>() }

    let asAlt (token: Token) : Alt<unit> =
        IVar.read token.Latch

    let isCancellationRequested (token: Token) : bool =
        IVar.Now.isFull token.Latch

    let cancel (token: Token) : Job<unit> =
        IVar.tryFill token.Latch ()

    let childContext (parent: SubscribeContext) : Token * SubscribeContext =
        let token = create ()

        let ctx =
            { Stop =
                parent.Stop
                <|>
                asAlt token

              IsStopped =
                fun () ->
                    parent.IsStopped()
                    || isCancellationRequested token }

        token, ctx

module internal Internal =

    let rootContext () : Cancel.Token * SubscribeContext =
        let token = Cancel.create ()

        token,
        { Stop = Cancel.asAlt token
          IsStopped = fun () -> Cancel.isCancellationRequested token }

    let neverContext : SubscribeContext =
        { Stop = Alt.never ()
          IsStopped = fun () -> false }

    let completed () : Job<unit> =
        Job.unit ()

    let result (x: 'T) : Job<'T> =
        job { return x }

    let runUnlessStopped (ctx: SubscribeContext) (work: Job<unit>) : Job<unit> =
        job {
            if not (ctx.IsStopped()) then
                do! work
        }

    /// Send `value` on `ch`, racing the give against subscription Stop. If the
    /// subscription is already stopped, or Stop wins the race, the value is
    /// dropped. Producers run as `Job.start`ed source subscriptions that observe
    /// Stop through their own `ctx`/source checks, so no caller needs a
    /// sent/dropped result — the earlier `Job<bool>` return was discarded at
    /// every call site.
    let giveOrStop (ctx: SubscribeContext) (ch: Ch<'T>) (value: 'T) : Job<unit> =
        if ctx.IsStopped() then
            Job.unit ()
        else
            ctx.Stop <|> Ch.give ch value

    let takeOrStop (ctx: SubscribeContext) (ch: Ch<'T>) : Job<Choice<'T, unit>> =
        if ctx.IsStopped() then
            result (Choice2Of2 ())
        else
            (ctx.Stop ^-> fun () -> Choice2Of2 ())
            <|>
            (Ch.take ch ^-> fun x -> Choice1Of2 x)

    let cancelMany (tokens: Cancel.Token list) : Job<unit> =
        job {
            for token in tokens do
                do! Cancel.cancel token
        }

    /// Wrap an observer so it obeys the terminal grammar even if the source does
    /// not: at most one terminal (`OnError`/`OnCompleted`) is forwarded, and no
    /// `OnNext` is forwarded once a terminal has been seen. Relies on the
    /// serialized-callback contract, so a plain mutable flag is sufficient.
    let terminalGate (obs: AsyncObserver<'T>) : AsyncObserver<'T> =
        let mutable terminated = false

        { OnNext =
            fun x ->
                if terminated then Job.unit ()
                else obs.OnNext x

          OnError =
            fun e ->
                if terminated then Job.unit ()
                else
                    terminated <- true
                    obs.OnError e

          OnCompleted =
            fun () ->
                if terminated then Job.unit ()
                else
                    terminated <- true
                    obs.OnCompleted() }

module AsyncObservable =

    let empty<'T> : AsyncObservable<'T> =
        fun ctx obs ->
            Internal.runUnlessStopped ctx (obs.OnCompleted())

    let singleton (x: 'T) : AsyncObservable<'T> =
        fun ctx obs -> job {
            if not (ctx.IsStopped()) then
                do! obs.OnNext x

            if not (ctx.IsStopped()) then
                do! obs.OnCompleted()
        }

    let fail<'T> (e: exn) : AsyncObservable<'T> =
        fun ctx obs ->
            Internal.runUnlessStopped ctx (obs.OnError e)

    let never<'T> : AsyncObservable<'T> =
        fun ctx _ ->
            ctx.Stop

    let ofSeq (xs: seq<'T>) : AsyncObservable<'T> =
        // Mirrors the hardened `AsyncRxCE.For` enumerator lifecycle: acquire the
        // enumerator inside the protected scope, iterate with a stack-safe Hopac
        // `while` loop (not `return! loop ()`, which overflows for fully
        // synchronous downstreams), dispose *before* completing, and turn any
        // acquisition/iteration/disposal failure into exactly one OnError.
        fun ctx obs -> job {
            let mutable terminated = false
            let mutable enumerator : Collections.Generic.IEnumerator<'T> option = None

            let forwardError (ex: exn) = job {
                if not terminated then
                    terminated <- true
                    do! obs.OnError ex
            }

            // Dispose the enumerator at most once, surfacing any failure so the
            // caller can decide whether it becomes the (single) terminal.
            let dispose () =
                match enumerator with
                | None -> None
                | Some en ->
                    enumerator <- None
                    try en.Dispose(); None
                    with ex -> Some ex

            try
                try
                    // Acquire inside the protected scope: a throwing
                    // GetEnumerator() becomes one OnError instead of escaping.
                    enumerator <- Some (xs.GetEnumerator())
                    let en = enumerator.Value
                    let mutable finished = false

                    // Hopac's job `while` loop is iterative, so this stays
                    // stack-safe even when the downstream consumes synchronously.
                    while not finished && not terminated && not (ctx.IsStopped()) do
                        if en.MoveNext() then
                            do! obs.OnNext en.Current
                        else
                            finished <- true

                    // Normal exhaustion: dispose *before* completing, and turn a
                    // disposal failure into the single terminal error.
                    if finished && not terminated && not (ctx.IsStopped()) then
                        match dispose () with
                        | Some ex -> do! forwardError ex
                        | None ->
                            terminated <- true
                            do! obs.OnCompleted()
                with ex ->
                    do! forwardError ex
            finally
                // Error / stop / acquisition-failure paths: ensure disposal,
                // swallowing any failure (the terminal is already decided).
                dispose () |> ignore
        }

    let intervalMillis (periodMs: int) : AsyncObservable<int> =
        fun ctx obs ->
            let rec loop i =
                job {
                    let! next =
                        (ctx.Stop ^-> fun () -> Choice2Of2 ())
                        <|>
                        (timeOutMillis periodMs ^-> fun () -> Choice1Of2 ())

                    match next with
                    | Choice2Of2 () ->
                        return ()

                    | Choice1Of2 () ->
                        do! obs.OnNext i
                        return! loop (i + 1)
                }

            loop 0

    /// Conditional source (the joinad/clause "guard"): emits `()` then completes
    /// when the condition holds; otherwise never emits or completes, so it cannot
    /// win a `Choice` race — i.e. a failed clause.
    let guard (condition: bool) : AsyncObservable<unit> =
        if condition then singleton ()
        else never

    /// Like `guard`, but the condition is computed in a `Job`.
    let guardJob (condition: Job<bool>) : AsyncObservable<unit> =
        fun ctx obs -> job {
            if not (ctx.IsStopped()) then
                let! ok = condition

                if ok then
                    do! obs.OnNext ()
                    do! Internal.runUnlessStopped ctx (obs.OnCompleted())
                else
                    do! ctx.Stop
        }

    let map (f: 'T -> 'U) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs ->
            source ctx {
                OnNext = fun x ->
                    obs.OnNext (f x)

                OnError = obs.OnError

                OnCompleted = obs.OnCompleted
            }

    // Stop-check policy (P2-3): synchronous transforms (`map`, `filter`,
    // `choose`, `scan`) carry no Stop checks — they have no suspension point, so
    // under the serialized-callback contract Stop cannot interleave inside a
    // callback, and the source is responsible for ceasing emission once stopped.
    // `mapJob` is the exception because `f` is awaited: Stop may fire *during*
    // that await, so it re-checks before starting `f` (skip work) and after it
    // resolves (skip a post-stop emit). Rule: guard internal await points, not
    // synchronous hand-offs. (Stop suppresses work; terminal gating is separate.)
    let mapJob (f: 'T -> Job<'U>) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs ->
            source ctx {
                OnNext = fun x -> job {
                    if not (ctx.IsStopped()) then
                        let! y = f x

                        if not (ctx.IsStopped()) then
                            do! obs.OnNext y
                }

                OnError = obs.OnError

                OnCompleted = obs.OnCompleted
            }

    let filter (predicate: 'T -> bool) (source: AsyncObservable<'T>) : AsyncObservable<'T> =
        fun ctx obs ->
            source ctx {
                OnNext = fun x ->
                    if predicate x then obs.OnNext x
                    else Job.unit ()

                OnError = obs.OnError

                OnCompleted = obs.OnCompleted
            }

    let choose (chooser: 'T -> 'U option) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs ->
            source ctx {
                OnNext = fun x ->
                    match chooser x with
                    | Some y -> obs.OnNext y
                    | None -> Job.unit ()

                OnError = obs.OnError

                OnCompleted = obs.OnCompleted
            }

    let bind (f: 'T -> AsyncObservable<'U>) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs -> job {
            let childCancel, childCtx =
                Cancel.childContext ctx

            // Observer callbacks here are serialized: the outer source drives OnNext,
            // which awaits the inner subscription to completion before the next
            // notification, so a plain mutable flag guards the terminal transition.
            let mutable terminated = false

            let terminate (final: Job<unit>) =
                job {
                    if not terminated then
                        terminated <- true
                        do! Cancel.cancel childCancel
                        do! final
                }

            try
                do! source childCtx {
                    OnNext = fun x ->
                        if terminated then
                            Job.unit ()
                        else
                            f x childCtx {
                                OnNext = fun y ->
                                    if terminated then Job.unit ()
                                    else obs.OnNext y

                                OnError = fun e ->
                                    terminate (obs.OnError e)

                                // The outer source alone owns normal completion.
                                OnCompleted = fun () ->
                                    Job.unit ()
                            }

                    OnError = fun e ->
                        terminate (obs.OnError e)

                    OnCompleted = fun () ->
                        terminate (obs.OnCompleted())
                }
            with e ->
                do! terminate (obs.OnError e)
        }

    let take (count: int) (source: AsyncObservable<'T>) : AsyncObservable<'T> =
        fun outerCtx obs -> job {
            if count <= 0 then
                do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())
            else
                let localCancel, innerCtx =
                    Cancel.childContext outerCtx

                let mutable seen = 0
                let mutable completed = false

                do! source innerCtx {
                    OnNext = fun x -> job {
                        if not completed && seen < count then
                            seen <- seen + 1
                            do! obs.OnNext x

                            if seen >= count then
                                completed <- true
                                do! Cancel.cancel localCancel
                                do! obs.OnCompleted()
                    }

                    OnError = fun e -> job {
                        if not completed then
                            completed <- true
                            do! Cancel.cancel localCancel
                            do! obs.OnError e
                    }

                    OnCompleted = fun () -> job {
                        if not completed then
                            completed <- true
                            do! obs.OnCompleted()
                    }
                }
        }

    let scan
        (folder: 'State -> 'T -> 'State)
        (initial: 'State)
        (source: AsyncObservable<'T>)
        : AsyncObservable<'State> =

        fun ctx obs -> job {
            let mutable state = initial

            do! source ctx {
                OnNext = fun x -> job {
                    state <- folder state x
                    do! obs.OnNext state
                }

                OnError = obs.OnError

                OnCompleted = obs.OnCompleted
            }
        }

    type private SwitchMsg<'T> =
        | OuterNext of AsyncObservable<'T>
        | OuterError of exn
        | OuterCompleted
        | InnerNext of int * 'T
        | InnerError of int * exn
        | InnerCompleted of int

    let switchLatest
        (source: AsyncObservable<AsyncObservable<'T>>)
        : AsyncObservable<'T> =

        fun outerCtx obs -> job {
            let operatorCancel, operatorCtx =
                Cancel.childContext outerCtx

            let events =
                Ch<SwitchMsg<'T>>()

            let send msg =
                Internal.giveOrStop operatorCtx events msg

            do!
                Job.start <| job {
                    try
                        do! source operatorCtx {
                            OnNext = fun inner ->
                                send (OuterNext inner)

                            OnError = fun e ->
                                send (OuterError e)

                            OnCompleted = fun () ->
                                send OuterCompleted
                        }
                    with e ->
                        do! send (OuterError e)
                }

            let startInner generation (inner: AsyncObservable<'T>) =
                job {
                    let innerCancel, innerCtx =
                        Cancel.childContext operatorCtx

                    do!
                        Job.start <| job {
                            try
                                do! inner innerCtx {
                                    OnNext = fun x ->
                                        send (InnerNext (generation, x))

                                    OnError = fun e ->
                                        send (InnerError (generation, e))

                                    OnCompleted = fun () ->
                                        send (InnerCompleted generation)
                                }
                            with e ->
                                do! send (InnerError (generation, e))
                        }

                    return innerCancel
                }

            let rec loop generation currentInner outerDone innerActive =
                job {
                    let! next =
                        Internal.takeOrStop outerCtx events

                    match next with
                    | Choice2Of2 () ->
                        do! Cancel.cancel operatorCancel

                    | Choice1Of2 msg ->
                        match msg with
                        | OuterNext inner ->
                            match currentInner with
                            | Some cancel -> do! Cancel.cancel cancel
                            | None -> ()

                            let generation' =
                                generation + 1

                            let! innerCancel =
                                startInner generation' inner

                            return! loop generation' (Some innerCancel) outerDone true

                        | OuterError e ->
                            do! Cancel.cancel operatorCancel
                            do! obs.OnError e

                        | OuterCompleted ->
                            if innerActive then
                                return! loop generation currentInner true innerActive
                            else
                                do! Cancel.cancel operatorCancel
                                do! obs.OnCompleted()

                        | InnerNext (g, x) when g = generation && innerActive ->
                            do! obs.OnNext x
                            return! loop generation currentInner outerDone innerActive

                        | InnerError (g, e) when g = generation && innerActive ->
                            do! Cancel.cancel operatorCancel
                            do! obs.OnError e

                        | InnerCompleted g when g = generation && innerActive ->
                            match currentInner with
                            | Some cancel -> do! Cancel.cancel cancel
                            | None -> ()

                            if outerDone then
                                do! Cancel.cancel operatorCancel
                                do! obs.OnCompleted()
                            else
                                return! loop generation None outerDone false

                        | _ ->
                            // Stale message from a previously cancelled inner stream.
                            return! loop generation currentInner outerDone innerActive
                }

            do! loop 0 None false false
        }

    type private DebounceWait<'T> =
        | DebounceSource of Notification<'T>
        | DebounceDue
        | DebounceStopped

    let debounce
        (dueMs: int)
        (source: AsyncObservable<'T>)
        : AsyncObservable<'T> =

        fun outerCtx obs -> job {
            if dueMs < 0 then
                do!
                    obs.OnError (
                        ArgumentOutOfRangeException(
                            "dueMs",
                            "Debounce duration must be non-negative."
                        )
                    )
            else
                let localCancel, innerCtx =
                    Cancel.childContext outerCtx

                let inbox =
                    Ch<Notification<'T>>()

                let send msg =
                    Internal.giveOrStop innerCtx inbox msg

                do!
                    Job.start <| job {
                        try
                            do! source innerCtx {
                                OnNext = fun x ->
                                    send (Next x)

                                OnError = fun e ->
                                    send (Error e)

                                OnCompleted = fun () ->
                                    send Completed
                            }
                        with e ->
                            do! send (Error e)
                    }

                let rec loop pending =
                    job {
                        let sourceAlt =
                            Ch.take inbox ^-> fun msg -> DebounceSource msg

                        let stopAlt =
                            outerCtx.Stop ^-> fun () -> DebounceStopped

                        let alts =
                            match pending with
                            | Some _ ->
                                // Timer churn (P2-4): each iteration with a pending
                                // value arms a fresh `timeOutMillis`. A superseded
                                // timer is not cancelled and still elapses, but it
                                // belongs to an already-withdrawn `Alt.choose`, so it
                                // wakes nothing — harmless. Only under very high-
                                // frequency input is the wasted timer traffic worth
                                // replacing with a generation-tagged/cancellable timer.
                                [ stopAlt
                                  sourceAlt
                                  timeOutMillis dueMs ^-> fun () -> DebounceDue ]

                            | None ->
                                [ stopAlt
                                  sourceAlt ]

                        let! event =
                            Alt.choose alts

                        match event with
                        | DebounceStopped ->
                            do! Cancel.cancel localCancel

                        | DebounceSource (Next x) ->
                            return! loop (Some x)

                        | DebounceSource (Error e) ->
                            do! Cancel.cancel localCancel
                            do! obs.OnError e

                        | DebounceSource Completed ->
                            do! Cancel.cancel localCancel

                            match pending with
                            | Some x ->
                                do! obs.OnNext x
                            | None ->
                                ()

                            do! obs.OnCompleted()

                        | DebounceDue ->
                            match pending with
                            | Some x ->
                                do! obs.OnNext x
                                return! loop None

                            | None ->
                                return! loop None
                    }

                do! loop None
        }

    /// First value only, then complete (≡ `take 1`).
    let first (source: AsyncObservable<'T>) : AsyncObservable<'T> =
        take 1 source

    /// First value satisfying `predicate`, then complete.
    let firstWhere
        (predicate: 'T -> bool)
        (source: AsyncObservable<'T>)
        : AsyncObservable<'T> =

        source
        |> filter predicate
        |> take 1

    type private ZipMsg<'T, 'U> =
        | LeftNext of 'T
        | RightNext of 'U
        | LeftError of exn
        | RightError of exn
        | LeftCompleted
        | RightCompleted

    /// Applicative stream product: pairs the i-th `left` value with the i-th
    /// `right` value. `AsyncRxCE.MergeSources` surfaces this as `and!`. Distinct
    /// from `Merge` (interleave) and `Choice` (first-to-emit).
    ///
    /// Unbounded buffering (P2-1): each side is queued until its counterpart
    /// arrives, so if one source outruns the other the faster side's queue grows
    /// without bound for the lifetime of the zip. This is fine for balanced or
    /// finite sources; for unbalanced/unbounded sources, bound the faster side
    /// upstream (e.g. `take`) or throttle it. No backpressure/bound is applied
    /// here by design.
    let zip
        (left: AsyncObservable<'T>)
        (right: AsyncObservable<'U>)
        : AsyncObservable<'T * 'U> =

        fun outerCtx obs -> job {
            let localCancel, innerCtx =
                Cancel.childContext outerCtx

            let inbox =
                Ch<ZipMsg<'T, 'U>>()

            let leftQueue =
                Collections.Generic.Queue<'T>()

            let rightQueue =
                Collections.Generic.Queue<'U>()

            let send msg =
                Internal.giveOrStop innerCtx inbox msg

            do!
                Job.start <| job {
                    try
                        do! left innerCtx {
                            OnNext = fun x ->
                                send (LeftNext x)

                            OnError = fun e ->
                                send (LeftError e)

                            OnCompleted = fun () ->
                                send LeftCompleted
                        }
                    with e ->
                        do! send (LeftError e)
                }

            do!
                Job.start <| job {
                    try
                        do! right innerCtx {
                            OnNext = fun y ->
                                send (RightNext y)

                            OnError = fun e ->
                                send (RightError e)

                            OnCompleted = fun () ->
                                send RightCompleted
                        }
                    with e ->
                        do! send (RightError e)
                }

            let rec emitAvailable leftDone rightDone =
                job {
                    if leftQueue.Count > 0 && rightQueue.Count > 0 then
                        let x = leftQueue.Dequeue()
                        let y = rightQueue.Dequeue()

                        do! obs.OnNext (x, y)
                        return! emitAvailable leftDone rightDone

                    elif leftQueue.Count = 0 && leftDone then
                        do! Cancel.cancel localCancel
                        do! obs.OnCompleted()

                    elif rightQueue.Count = 0 && rightDone then
                        do! Cancel.cancel localCancel
                        do! obs.OnCompleted()

                    else
                        let! msg =
                            Internal.takeOrStop outerCtx inbox

                        match msg with
                        | Choice2Of2 () ->
                            do! Cancel.cancel localCancel

                        | Choice1Of2 (LeftNext x) ->
                            leftQueue.Enqueue x
                            return! emitAvailable leftDone rightDone

                        | Choice1Of2 (RightNext y) ->
                            rightQueue.Enqueue y
                            return! emitAvailable leftDone rightDone

                        | Choice1Of2 (LeftError e)
                        | Choice1Of2 (RightError e) ->
                            do! Cancel.cancel localCancel
                            do! obs.OnError e

                        | Choice1Of2 LeftCompleted ->
                            return! emitAvailable true rightDone

                        | Choice1Of2 RightCompleted ->
                            return! emitAvailable leftDone true
                }

            do! emitAvailable false false
        }

    /// Wait for one value from each source, emit one pair, then complete.
    let bothOnce
        (left: AsyncObservable<'T>)
        (right: AsyncObservable<'U>)
        : AsyncObservable<'T * 'U> =

        zip (first left) (first right)
        |> first

module Merge =

    let merge (sources: AsyncObservable<'T> list) : AsyncObservable<'T> =
        fun outerCtx obs -> job {
            match sources with
            | [] ->
                do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())

            | _ ->
                let localCancel, innerCtx =
                    Cancel.childContext outerCtx

                let out =
                    Ch<Notification<'T>>()

                let send msg =
                    Internal.giveOrStop innerCtx out msg

                let startSource (source: AsyncObservable<'T>) =
                    Job.start <| job {
                        try
                            do! source innerCtx {
                                OnNext = fun x ->
                                    send (Next x)

                                OnError = fun e ->
                                    send (Error e)

                                OnCompleted = fun () ->
                                    send Completed
                            }
                        with e ->
                            do! send (Error e)
                    }

                for source in sources do
                    do! startSource source

                let rec loop remaining =
                    job {
                        if remaining = 0 then
                            do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())
                        else
                            let! msg =
                                Internal.takeOrStop outerCtx out

                            match msg with
                            | Choice2Of2 () ->
                                do! Cancel.cancel localCancel

                            | Choice1Of2 (Next x) ->
                                do! obs.OnNext x
                                return! loop remaining

                            | Choice1Of2 (Error e) ->
                                do! Cancel.cancel localCancel
                                do! obs.OnError e

                            | Choice1Of2 Completed ->
                                return! loop (remaining - 1)
                    }

                do! loop sources.Length
        }

    let merge2 a b =
        merge [ a; b ]

module Choice =

    /// Rx-style amb/race:
    /// all sources are subscribed, the first source to produce any notification wins,
    /// and losing sources are cancelled.
    let amb (sources: AsyncObservable<'T> list) : AsyncObservable<'T> =
        fun outerCtx obs -> job {
            match sources with
            | [] ->
                do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())

            | _ ->
                let entries =
                    sources
                    |> List.map (fun source ->
                        let childCancel, childCtx =
                            Cancel.childContext outerCtx

                        let ch =
                            Ch<Notification<'T>>()

                        childCancel, childCtx, ch, source)

                let send childCtx ch msg =
                    Internal.giveOrStop childCtx ch msg

                for _, childCtx, ch, source in entries do
                    do!
                        Job.start <| job {
                            try
                                do! source childCtx {
                                    OnNext = fun x ->
                                        send childCtx ch (Next x)

                                    OnError = fun e ->
                                        send childCtx ch (Error e)

                                    OnCompleted = fun () ->
                                        send childCtx ch Completed
                                }
                            with e ->
                                do! send childCtx ch (Error e)
                        }

                let indexedTakes =
                    entries
                    |> List.mapi (fun i (_, _, ch, _) ->
                        Ch.take ch ^-> fun msg -> i, msg)

                let! first =
                    (outerCtx.Stop ^-> fun () -> Choice2Of2 ())
                    <|>
                    (Alt.choose indexedTakes ^-> fun x -> Choice1Of2 x)

                match first with
                | Choice2Of2 () ->
                    let cancels =
                        entries
                        |> List.map (fun (cancel, _, _, _) -> cancel)

                    do! Internal.cancelMany cancels

                | Choice1Of2 (winnerIndex, firstMsg) ->
                    let loserCancels =
                        entries
                        |> List.indexed
                        |> List.choose (fun (i, (cancel, _, _, _)) ->
                            if i = winnerIndex then None else Some cancel)

                    do! Internal.cancelMany loserCancels

                    let winnerCancel, _, winnerCh, _ =
                        entries.[winnerIndex]

                    let rec forward msg =
                        job {
                            match msg with
                            | Next x ->
                                do! obs.OnNext x

                                let! next =
                                    (outerCtx.Stop ^-> fun () -> Choice2Of2 ())
                                    <|>
                                    (Ch.take winnerCh ^-> fun x -> Choice1Of2 x)

                                match next with
                                | Choice2Of2 () ->
                                    do! Cancel.cancel winnerCancel

                                | Choice1Of2 msg' ->
                                    return! forward msg'

                            | Error e ->
                                do! Cancel.cancel winnerCancel
                                do! obs.OnError e

                            | Completed ->
                                do! Cancel.cancel winnerCancel
                                do! obs.OnCompleted()
                        }

                    do! forward firstMsg
        }

    /// Binary choice: race `left` against `right`, first to emit wins
    /// (≡ `amb [left; right]`).
    let orElse
        (left: AsyncObservable<'T>)
        (right: AsyncObservable<'T>)
        : AsyncObservable<'T> =

        amb [ left; right ]

    /// Clause selection: subscribe all sources and select the first to emit a
    /// *value* (or to error). Unlike `amb`, a source that completes *without*
    /// emitting (a non-matching clause built from `AsyncObservable.guard` /
    /// `AsyncObservable.choose`) is discarded instead of winning the race, so its
    /// completion cannot beat a slower clause that actually produces a value.
    /// The combined stream completes empty only if *every* source completes
    /// empty. Once a source emits, it wins: the rest are cancelled and the
    /// winner's remaining stream is forwarded.
    let firstValue (sources: AsyncObservable<'T> list) : AsyncObservable<'T> =
        fun outerCtx obs -> job {
            match sources with
            | [] ->
                do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())

            | _ ->
                let entries =
                    sources
                    |> List.map (fun source ->
                        let childCancel, childCtx =
                            Cancel.childContext outerCtx

                        let ch =
                            Ch<Notification<'T>>()

                        childCancel, childCtx, ch, source)

                let send childCtx ch msg =
                    Internal.giveOrStop childCtx ch msg

                for _, childCtx, ch, source in entries do
                    do!
                        Job.start <| job {
                            try
                                do! source childCtx {
                                    OnNext = fun x ->
                                        send childCtx ch (Next x)

                                    OnError = fun e ->
                                        send childCtx ch (Error e)

                                    OnCompleted = fun () ->
                                        send childCtx ch Completed
                                }
                            with e ->
                                do! send childCtx ch (Error e)
                        }

                let cancelOf i =
                    let cancel, _, _, _ = entries.[i]
                    cancel

                let channelOf i =
                    let _, _, ch, _ = entries.[i]
                    ch

                let cancelAll () =
                    entries
                    |> List.map (fun (cancel, _, _, _) -> cancel)
                    |> Internal.cancelMany

                let cancelExcept winnerIndex =
                    entries
                    |> List.indexed
                    |> List.choose (fun (i, (cancel, _, _, _)) ->
                        if i = winnerIndex then None else Some cancel)
                    |> Internal.cancelMany

                // Forward the winner's remaining stream after its first value.
                let rec forward winnerIndex =
                    job {
                        let! next =
                            (outerCtx.Stop ^-> fun () -> Choice2Of2 ())
                            <|>
                            (Ch.take (channelOf winnerIndex) ^-> fun x -> Choice1Of2 x)

                        match next with
                        | Choice2Of2 () ->
                            do! Cancel.cancel (cancelOf winnerIndex)

                        | Choice1Of2 (Next x) ->
                            do! obs.OnNext x
                            return! forward winnerIndex

                        | Choice1Of2 (Error e) ->
                            do! Cancel.cancel (cancelOf winnerIndex)
                            do! obs.OnError e

                        | Choice1Of2 Completed ->
                            do! Cancel.cancel (cancelOf winnerIndex)
                            do! obs.OnCompleted()
                    }

                // Race the still-active clauses. An empty-completer is dropped
                // from `active` and the race continues; the others stay armed.
                let rec select active =
                    job {
                        match active with
                        | [] ->
                            // Every clause completed without emitting.
                            do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())

                        | _ ->
                            let indexedTakes =
                                active
                                |> List.map (fun i ->
                                    Ch.take (channelOf i) ^-> fun msg -> i, msg)

                            let! winner =
                                (outerCtx.Stop ^-> fun () -> Choice2Of2 ())
                                <|>
                                (Alt.choose indexedTakes ^-> fun x -> Choice1Of2 x)

                            match winner with
                            | Choice2Of2 () ->
                                do! cancelAll ()

                            | Choice1Of2 (i, Next x) ->
                                do! cancelExcept i
                                do! obs.OnNext x
                                do! forward i

                            | Choice1Of2 (i, Error e) ->
                                do! cancelAll ()
                                do! obs.OnError e

                            | Choice1Of2 (i, Completed) ->
                                do! Cancel.cancel (cancelOf i)
                                return! select (List.filter (fun j -> j <> i) active)
                    }

                do! select [ 0 .. entries.Length - 1 ]
        }

module AsyncRxCE =

    type AsyncRxBuilder() =

        member _.Return(x: 'T) : AsyncObservable<'T> =
            AsyncObservable.singleton x

        member _.ReturnFrom(source: AsyncObservable<'T>) : AsyncObservable<'T> =
            source

        member _.Zero() : AsyncObservable<unit> =
            AsyncObservable.empty

        member _.Delay(f: unit -> AsyncObservable<'T>) : AsyncObservable<'T> =
            fun ctx obs ->
                f () ctx obs

        member _.Bind
            (
                source: AsyncObservable<'T>,
                f: 'T -> AsyncObservable<'U>
            ) : AsyncObservable<'U> =

            AsyncObservable.bind f source

        // Statement sequencing: ignore the values of `first`, and start `second`
        // exactly once when `first` completes normally (never after an error).
        member _.Combine
            (
                first: AsyncObservable<unit>,
                second: AsyncObservable<'T>
            ) : AsyncObservable<'T> =

            fun ctx obs -> job {
                // One terminal gate over the whole combined observable, so a
                // misbehaving `first` *or* `second` (double error, complete-then-
                // error) still yields exactly one downstream terminal.
                let gated = Internal.terminalGate obs

                // Operator-local control over whether `second` may start: it must
                // start at most once, and never after `first` errors. This is
                // distinct from the terminal gate — starting `second` is a side
                // effect, not a notification, so the gate cannot govern it.
                let mutable firstTerminated = false

                try
                    do! first ctx {
                        OnNext = fun () ->
                            Job.unit ()

                        OnError = fun e -> job {
                            firstTerminated <- true
                            do! gated.OnError e
                        }

                        OnCompleted = fun () ->
                            if firstTerminated then
                                Job.unit ()
                            else
                                firstTerminated <- true
                                // `second` flows through the same gate.
                                second ctx gated
                    }
                with e ->
                    // A pre-terminal throw from `first` *or* `second` becomes the
                    // single downstream error; the gate discards it if a terminal
                    // was already delivered (e.g. `second` threw after completing).
                    do! gated.OnError e
            }

        member _.While
            (
                guard: unit -> bool,
                body: AsyncObservable<unit>
            ) : AsyncObservable<unit> =

            // Direct, stack-safe loop: the guard is first evaluated at subscription
            // (inside the returned function), each body runs to completion before the
            // next guard check, and Hopac's job `while` keeps this iterative.
            fun ctx obs -> job {
                let mutable errored = false

                try
                    while not errored && not (ctx.IsStopped()) && guard () do
                        do! body ctx {
                            OnNext = fun () ->
                                Job.unit ()

                            OnError = fun ex -> job {
                                errored <- true
                                do! obs.OnError ex
                            }

                            OnCompleted = fun () ->
                                Job.unit ()
                        }

                    if not errored then
                        do! Internal.runUnlessStopped ctx (obs.OnCompleted())
                with ex ->
                    if not errored then
                        do! obs.OnError ex
            }

        // Lazy, stack-safe enumeration: the enumerator is acquired at subscription,
        // pulled one element at a time, and disposed on completion/error/stop.
        member _.For
            (
                xs: seq<'T>,
                body: 'T -> AsyncObservable<unit>
            ) : AsyncObservable<unit> =

            fun ctx obs -> job {
                let mutable terminated = false
                let mutable enumerator : Collections.Generic.IEnumerator<'T> option = None

                let forwardError (ex: exn) = job {
                    if not terminated then
                        terminated <- true
                        do! obs.OnError ex
                }

                // Dispose the enumerator at most once, reporting any failure so
                // the caller can decide whether it is the (single) terminal.
                let dispose () =
                    match enumerator with
                    | None -> None
                    | Some en ->
                        enumerator <- None
                        try en.Dispose(); None
                        with ex -> Some ex

                try
                    try
                        // Acquisition is inside the protected scope, so a throwing
                        // GetEnumerator() becomes one OnError instead of escaping.
                        enumerator <- Some (xs.GetEnumerator())
                        let en = enumerator.Value
                        let mutable finished = false

                        // Hopac's job `while` loop is iterative, so this stays
                        // stack-safe even when bodies complete synchronously.
                        while not finished && not terminated && not (ctx.IsStopped()) do
                            if en.MoveNext() then
                                do! body en.Current ctx {
                                    OnNext = fun () ->
                                        Job.unit ()

                                    OnError = fun ex ->
                                        forwardError ex

                                    OnCompleted = fun () ->
                                        Job.unit ()
                                }
                            else
                                finished <- true

                        // Normal exhaustion: dispose *before* completing, and turn
                        // a disposal failure into the single terminal error.
                        if finished && not terminated && not (ctx.IsStopped()) then
                            match dispose () with
                            | Some ex -> do! forwardError ex
                            | None ->
                                terminated <- true
                                do! obs.OnCompleted()
                    with ex ->
                        do! forwardError ex
                finally
                    // Error / stop / acquisition-failure paths: ensure disposal,
                    // swallowing any failure (the terminal is already decided).
                    dispose () |> ignore
            }

        /// Enables F# applicative `and!`.
        ///
        /// asyncRx {
        ///     let! x = a
        ///     and! y = b
        ///     return x, y
        /// }
        member _.MergeSources
            (
                left: AsyncObservable<'T>,
                right: AsyncObservable<'U>
            ) : AsyncObservable<'T * 'U> =

            AsyncObservable.zip left right

        member _.BindReturn
            (
                source: AsyncObservable<'T>,
                f: 'T -> 'U
            ) : AsyncObservable<'U> =

            AsyncObservable.map f source

        member _.Run(source: AsyncObservable<'T>) : AsyncObservable<'T> =
            source

    let asyncRx =
        AsyncRxBuilder()

module ClauseCE =

    type ClausesBuilder() =

        member _.Yield(x: AsyncObservable<'T>) =
            [ x ]

        member _.YieldFrom(xs: AsyncObservable<'T> list) =
            xs

        member _.Zero() =
            []

        member _.Combine(xs, ys) =
            xs @ ys

        member _.Delay(f: unit -> AsyncObservable<'T> list) =
            f ()

        /// Selects the first clause to emit a *value*. A non-matching clause
        /// (built with `AsyncObservable.guard` / `AsyncObservable.choose`) completes without
        /// emitting; `Choice.firstValue` discards such empty-completers rather
        /// than letting their completion win the race (which plain `amb` would),
        /// so a slower clause that actually matches still gets selected.
        member _.Run(xs: AsyncObservable<'T> list) =
            Choice.firstValue xs

    let clauses =
        ClausesBuilder()

module Subscribe =

    let subscribeJob
        (source: AsyncObservable<'T>)
        (onNext: 'T -> Job<unit>)
        (onError: exn -> Job<unit>)
        (onCompleted: unit -> Job<unit>)
        : Job<Subscription> =

        job {
            let cancelToken, ctx =
                Internal.rootContext ()

            // Final-defense terminal gate: protects this subscriber from a
            // source that violates the terminal grammar (see the contract on
            // `AsyncObservable`). The catch routes through the same gate so a
            // post-terminal throw cannot surface as a second error.
            let gated =
                Internal.terminalGate {
                    OnNext = onNext
                    OnError = onError
                    OnCompleted = onCompleted
                }

            // Filled when the source job finishes (terminal, Stop-unwind, or a
            // thrown source/observer), so callers can await deterministic teardown.
            let completed = IVar<unit>()

            do!
                Job.start (
                    Job.tryFinallyJob
                        (job {
                            try
                                do! source ctx gated
                            with e ->
                                do! gated.OnError e
                         })
                        (IVar.tryFill completed ()))

            return {
                Stop = fun () -> Cancel.cancel cancelToken
                Completion = IVar.read completed
            }
        }

    let runJob
        (source: AsyncObservable<'T>)
        (onNext: 'T -> Job<unit>)
        (onError: exn -> Job<unit>)
        (onCompleted: unit -> Job<unit>)
        : Job<unit> =

        // Same boundary terminal gate as `subscribeJob` (see the contract on
        // `AsyncObservable`); also catches a source that throws after a terminal.
        let gated =
            Internal.terminalGate {
                OnNext = onNext
                OnError = onError
                OnCompleted = onCompleted
            }

        job {
            try
                do! source Internal.neverContext gated
            with e ->
                do! gated.OnError e
        }

    let runBlocking (source: AsyncObservable<'T>) (onNext: 'T -> unit) : unit =
        Hopac.run <|
            runJob
                source
                (fun x -> job { onNext x })
                (fun e -> job { raise e })
                (fun () -> Job.unit ())

    let subscribe
        (source: AsyncObservable<'T>)
        (onNext: 'T -> unit)
        : Subscription =

        Hopac.run <|
            subscribeJob
                source
                (fun x -> job { onNext x })
                (fun e -> job { raise e })
                (fun () -> Job.unit ())

module TaskInterop =

    open Hopac.Extensions

    /// Cold, cancellable Task boundary.
    ///
    /// The Task is created at subscription time.
    /// Cancellation is exposed to the factory via CancellationToken.
    let ofTaskFactory
        (makeTask: CancellationToken -> Task<'T>)
        : AsyncObservable<'T> =

        fun ctx obs -> job {
            let linked =
                CancellationTokenSource.CreateLinkedTokenSource(CancellationToken.None)

            try
                try
                    let task =
                        makeTask linked.Token

                    // Bridge task completion into an IVar so it can be raced against
                    // Stop as an Alt. The bridge observes the task's outcome (success
                    // or fault), so a cancelled task never raises unobserved.
                    let outcome = IVar<Choice<'T, exn>>()

                    do!
                        Job.start <| job {
                            try
                                let! x = Job.awaitTask task
                                do! IVar.fill outcome (Choice1Of2 x)
                            with e ->
                                do! IVar.fill outcome (Choice2Of2 e)
                        }

                    let! winner =
                        (ctx.Stop ^-> fun () -> Choice2Of2 ())
                        <|>
                        (IVar.read outcome ^-> Choice1Of2)

                    match winner with
                    | Choice2Of2 () ->
                        // Subscription stopped: cancel the task and terminate silently.
                        // Disposal happens in the finally, after Cancel, so the bridge
                        // (which never touches `linked`) cannot race a disposed CTS.
                        linked.Cancel()

                    | Choice1Of2 (Choice1Of2 x) ->
                        if not (ctx.IsStopped()) then
                            do! obs.OnNext x
                            do! Internal.runUnlessStopped ctx (obs.OnCompleted())

                    | Choice1Of2 (Choice2Of2 e) ->
                        // A genuine task fault/cancellation (subscription not stopped).
                        // `Job.awaitTask` surfaces a faulted Task as its wrapping
                        // AggregateException; unwrap a single inner exception so the
                        // underlying fault/message surfaces instead of the wrapper.
                        let unwrapped =
                            match e with
                            | :? AggregateException as agg when agg.InnerExceptions.Count = 1 ->
                                agg.InnerExceptions.[0]
                            | _ -> e

                        let err =
                            match unwrapped with
                            | :? OperationCanceledException ->
                                OperationCanceledException("Task was cancelled.") :> exn
                            | _ -> unwrapped

                        do! Internal.runUnlessStopped ctx (obs.OnError err)
                with e ->
                    do! Internal.runUnlessStopped ctx (obs.OnError e)
            finally
                linked.Dispose()
        }

    /// Cold Task boundary without a useful CancellationToken.
    let ofTask (makeTask: unit -> Task<'T>) : AsyncObservable<'T> =
        ofTaskFactory (fun _ -> makeTask())

    /// Hot/eager Task boundary. Use only when the Task intentionally already exists.
    let ofHotTask (task: Task<'T>) : AsyncObservable<'T> =
        ofTask (fun () -> task)

    let toTaskList (source: AsyncObservable<'T>) : Task<'T list> =
        let results = ResizeArray<'T>()

        Hopac.startAsTask <| job {
            do!
                Subscribe.runJob
                    source
                    (fun x -> job { results.Add x })
                    (fun e -> job { raise e })
                    (fun () -> Job.unit ())

            return List.ofSeq results
        }

module AsyncRx =

    open AsyncRxCE

    let asyncRx =
        AsyncRxCE.asyncRx

    let value x =
        AsyncObservable.singleton x

    let empty<'T> =
        AsyncObservable.empty<'T>

    let never<'T> =
        AsyncObservable.never<'T>

    let fail<'T> e =
        AsyncObservable.fail<'T> e

    let ofSeq xs =
        AsyncObservable.ofSeq xs

    let interval ms =
        AsyncObservable.intervalMillis ms

    let map f xs =
        AsyncObservable.map f xs

    let mapJob f xs =
        AsyncObservable.mapJob f xs

    let filter f xs =
        AsyncObservable.filter f xs

    let choose f xs =
        AsyncObservable.choose f xs

    let take n xs =
        AsyncObservable.take n xs

    let scan f init xs =
        AsyncObservable.scan f init xs

    let debounce ms xs =
        AsyncObservable.debounce ms xs

    let switchLatest xs =
        AsyncObservable.switchLatest xs

    let switch xs =
        AsyncObservable.switchLatest xs

    let merge xs =
        Merge.merge xs

    let amb xs =
        Choice.amb xs

    let race xs =
        Choice.amb xs

    let zip a b =
        AsyncObservable.zip a b

    let bothOnce a b =
        AsyncObservable.bothOnce a b

    let orElse a b =
        Choice.orElse a b

    let case source chooser =
        AsyncObservable.choose chooser source

    let first source =
        AsyncObservable.first source

    let firstWhere pred source =
        AsyncObservable.firstWhere pred source


