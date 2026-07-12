namespace AsyncRxHopac

open System
open System.Threading
open System.Threading.Tasks
open Hopac
open FSharp.Control
open Core

module AsyncObservable =

    type SourceStep<'State, 'T> =
        | Emit of 'T * 'State
        | Continue of 'State
        | Complete
        | Fail of exn

    type private DriverStep<'State, 'T> =
        | DriverStopped
        | DriverNext of SourceStep<'State, 'T>

    let private relayNext
        (onNext: 'T -> Job<unit>)
        (obs: AsyncObserver<'U>)
        : AsyncObserver<'T> =

        { OnNext = onNext
          OnError = obs.OnError
          OnCompleted = obs.OnCompleted }

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

    /// Build a source from a state machine whose next transition is a selectable
    /// Hopac operation. The driver owns Stop racing, looping, OnNext
    /// acknowledgement, and terminal forwarding.
    let unfoldEventChoice
        (initial: 'State)
        (next: SubscribeContext -> 'State -> EventChoice<SourceStep<'State, 'T>>)
        : AsyncObservable<'T> =

        fun ctx obs ->
            let rec loop state =
                job {
                    if ctx.IsStopped() then
                        return ()
                    else
                        let! step =
                            EventChoice.choose [
                                EventChoice.stop ctx DriverStopped
                                EventChoice.map DriverNext (next ctx state)
                            ]

                        match step with
                        | DriverStopped ->
                            return ()

                        | DriverNext (Emit (value, nextState)) ->
                            do! obs.OnNext value
                            return! loop nextState

                        | DriverNext (Continue nextState) ->
                            return! loop nextState

                        | DriverNext Complete ->
                            do! Internal.runUnlessStopped ctx (obs.OnCompleted())

                        | DriverNext (Fail ex) ->
                            do! Internal.runUnlessStopped ctx (obs.OnError ex)
                }

            job {
                try
                    do! loop initial
                with ex ->
                    do! Internal.runUnlessStopped ctx (obs.OnError ex)
            }

    /// Build a source by repeatedly selecting the next value until Stop wins.
    let repeatEventChoice
        (next: SubscribeContext -> EventChoice<'T>)
        : AsyncObservable<'T> =

        unfoldEventChoice
            ()
            (fun ctx () ->
                EventChoice.map (fun value -> Emit (value, ())) (next ctx))

    let ofSeq (xs: seq<'T>) : AsyncObservable<'T> =
        // Mirrors the hardened `AsyncRxBuilder.For` enumerator lifecycle: acquire
        // the enumerator inside the protected scope, iterate with a stack-safe
        // Hopac `while` loop (not `return! loop ()`, which overflows for fully
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
                        EventChoice.choose [
                            EventChoice.stop ctx (Choice2Of2 ())
                            EventChoice.afterMillis periodMs (Choice1Of2 ())
                        ]

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
            let onNext x =
                obs.OnNext (f x)

            source ctx (relayNext onNext obs)

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
            let onNext x =
                job {
                    if not (ctx.IsStopped()) then
                        let! y = f x

                        if not (ctx.IsStopped()) then
                            do! obs.OnNext y
                }

            source ctx (relayNext onNext obs)

    let filter (predicate: 'T -> bool) (source: AsyncObservable<'T>) : AsyncObservable<'T> =
        fun ctx obs ->
            let onNext x =
                if predicate x then obs.OnNext x
                else Job.unit ()

            source ctx (relayNext onNext obs)

    let choose (chooser: 'T -> 'U option) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs ->
            let onNext x =
                match chooser x with
                | Some y -> obs.OnNext y
                | None -> Job.unit ()

            source ctx (relayNext onNext obs)

    let bind (f: 'T -> AsyncObservable<'U>) (source: AsyncObservable<'T>) : AsyncObservable<'U> =
        fun ctx obs -> job {
            let childCancel, childCtx =
                EventChoice.childContext ctx

            // Observer callbacks here are serialized: the outer source drives OnNext,
            // which awaits the inner subscription to completion before the next
            // notification, so a plain mutable flag guards the terminal transition.
            let mutable terminated = false

            let terminate (final: Job<unit>) =
                job {
                    if not terminated then
                        terminated <- true
                        do! EventChoice.cancel childCancel
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
                    EventChoice.childContext outerCtx

                let mutable seen = 0
                let mutable completed = false

                do! source innerCtx {
                    OnNext = fun x -> job {
                        if not completed && seen < count then
                            seen <- seen + 1
                            do! obs.OnNext x

                            if seen >= count then
                                completed <- true
                                do! EventChoice.cancel localCancel
                                do! obs.OnCompleted()
                    }

                    OnError = fun e -> job {
                        if not completed then
                            completed <- true
                            do! EventChoice.cancel localCancel
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
            let onNext x =
                job {
                    state <- folder state x
                    do! obs.OnNext state
                }

            do! source ctx (relayNext onNext obs)
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
                EventChoice.childContext outerCtx

            let events =
                Ch<SwitchMsg<'T>>()

            let send msg =
                Internal.giveOrStop operatorCtx events msg

            do!
                Internal.forwardInto
                    operatorCtx
                    source
                    OuterNext
                    OuterError
                    OuterCompleted
                    send

            let startInner generation (inner: AsyncObservable<'T>) =
                job {
                    let innerCancel, innerCtx =
                        EventChoice.childContext operatorCtx

                    do!
                        Internal.forwardInto
                            innerCtx
                            inner
                            (fun x -> InnerNext (generation, x))
                            (fun e -> InnerError (generation, e))
                            (InnerCompleted generation)
                            send

                    return innerCancel
                }

            let rec loop generation currentInner outerDone innerActive =
                job {
                    let! next =
                        Internal.takeOrStop outerCtx events

                    match next with
                    | Choice2Of2 () ->
                        do! EventChoice.cancel operatorCancel

                    | Choice1Of2 msg ->
                        match msg with
                        | OuterNext inner ->
                            match currentInner with
                            | Some cancel -> do! EventChoice.cancel cancel
                            | None -> ()

                            let generation' =
                                generation + 1

                            let! innerCancel =
                                startInner generation' inner

                            return! loop generation' (Some innerCancel) outerDone true

                        | OuterError e ->
                            do! EventChoice.cancel operatorCancel
                            do! obs.OnError e

                        | OuterCompleted ->
                            if innerActive then
                                return! loop generation currentInner true innerActive
                            else
                                do! EventChoice.cancel operatorCancel
                                do! obs.OnCompleted()

                        | InnerNext (g, x) when g = generation && innerActive ->
                            do! obs.OnNext x
                            return! loop generation currentInner outerDone innerActive

                        | InnerError (g, e) when g = generation && innerActive ->
                            do! EventChoice.cancel operatorCancel
                            do! obs.OnError e

                        | InnerCompleted g when g = generation && innerActive ->
                            match currentInner with
                            | Some cancel -> do! EventChoice.cancel cancel
                            | None -> ()

                            if outerDone then
                                do! EventChoice.cancel operatorCancel
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
                    EventChoice.childContext outerCtx

                let inbox =
                    Ch<Notification<'T>>()

                let send msg =
                    Internal.giveOrStop innerCtx inbox msg

                do!
                    Internal.forwardInto
                        innerCtx
                        source
                        Next
                        Error
                        Completed
                        send

                let rec loop pending =
                    job {
                        let sourceAlt =
                            EventChoice.take inbox DebounceSource

                        let stopAlt =
                            EventChoice.stop outerCtx DebounceStopped

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
                                  EventChoice.afterMillis dueMs DebounceDue ]

                            | None ->
                                [ stopAlt
                                  sourceAlt ]

                        let! event =
                            EventChoice.choose alts

                        match event with
                        | DebounceStopped ->
                            do! EventChoice.cancel localCancel

                        | DebounceSource (Next x) ->
                            return! loop (Some x)

                        | DebounceSource (Error e) ->
                            do! EventChoice.cancel localCancel
                            do! obs.OnError e

                        | DebounceSource Completed ->
                            do! EventChoice.cancel localCancel

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
    /// `right` value. `AsyncRxBuilder.MergeSources` surfaces this as `and!`.
    /// Distinct from `merge` (interleave) and the first-to-emit choices
    /// (`amb`/`orElse`/`firstValue`).
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
                EventChoice.childContext outerCtx

            let inbox =
                Ch<ZipMsg<'T, 'U>>()

            let leftQueue =
                Collections.Generic.Queue<'T>()

            let rightQueue =
                Collections.Generic.Queue<'U>()

            let send msg =
                Internal.giveOrStop innerCtx inbox msg

            do!
                Internal.forwardInto
                    innerCtx
                    left
                    LeftNext
                    LeftError
                    LeftCompleted
                    send

            do!
                Internal.forwardInto
                    innerCtx
                    right
                    RightNext
                    RightError
                    RightCompleted
                    send

            let rec emitAvailable leftDone rightDone =
                job {
                    if leftQueue.Count > 0 && rightQueue.Count > 0 then
                        let x = leftQueue.Dequeue()
                        let y = rightQueue.Dequeue()

                        do! obs.OnNext (x, y)
                        return! emitAvailable leftDone rightDone

                    elif leftQueue.Count = 0 && leftDone then
                        do! EventChoice.cancel localCancel
                        do! obs.OnCompleted()

                    elif rightQueue.Count = 0 && rightDone then
                        do! EventChoice.cancel localCancel
                        do! obs.OnCompleted()

                    else
                        let! msg =
                            Internal.takeOrStop outerCtx inbox

                        match msg with
                        | Choice2Of2 () ->
                            do! EventChoice.cancel localCancel

                        | Choice1Of2 (LeftNext x) ->
                            leftQueue.Enqueue x
                            return! emitAvailable leftDone rightDone

                        | Choice1Of2 (RightNext y) ->
                            rightQueue.Enqueue y
                            return! emitAvailable leftDone rightDone

                        | Choice1Of2 (LeftError e)
                        | Choice1Of2 (RightError e) ->
                            do! EventChoice.cancel localCancel
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

    // --- Merge: interleave every source's notifications -----------------------
    // (folded in from the former `Merge` module).

    let merge (sources: AsyncObservable<'T> list) : AsyncObservable<'T> =
        fun outerCtx obs -> job {
            match sources with
            | [] ->
                do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())

            | _ ->
                let localCancel, innerCtx =
                    EventChoice.childContext outerCtx

                let out =
                    Ch<Notification<'T>>()

                let send msg =
                    Internal.giveOrStop innerCtx out msg

                for source in sources do
                    do!
                        Internal.forwardInto
                            innerCtx
                            source
                            Next
                            Error
                            Completed
                            send

                let rec loop remaining =
                    job {
                        if remaining = 0 then
                            do! Internal.runUnlessStopped outerCtx (obs.OnCompleted())
                        else
                            let! msg =
                                Internal.takeOrStop outerCtx out

                            match msg with
                            | Choice2Of2 () ->
                                do! EventChoice.cancel localCancel

                            | Choice1Of2 (Next x) ->
                                do! obs.OnNext x
                                return! loop remaining

                            | Choice1Of2 (Error e) ->
                                do! EventChoice.cancel localCancel
                                do! obs.OnError e

                            | Choice1Of2 Completed ->
                                return! loop (remaining - 1)
                    }

                do! loop sources.Length
        }

    let merge2 a b =
        merge [ a; b ]

    // --- Choice: first-to-emit races ------------------------------------------
    // (folded in from the former `Choice` module). `amb`/`orElse` race for the
    // first notification; `firstValue` races for the first *value* (clause
    // selection) and discards empty-completers.

    type private SourceEntry<'T> =
        { Cancel : EventChoice.Token
          Ctx : SubscribeContext
          Ch : Ch<Notification<'T>>
          Source : AsyncObservable<'T> }

    let private sourceEntry outerCtx source =
        let childCancel, childCtx =
            EventChoice.childContext outerCtx

        { Cancel = childCancel
          Ctx = childCtx
          Ch = Ch<Notification<'T>>()
          Source = source }

    let private startEntry entry =
        Internal.forwardInto
            entry.Ctx
            entry.Source
            Next
            Error
            Completed
            (Internal.giveOrStop entry.Ctx entry.Ch)

    let private cancelEntries entries =
        entries
        |> List.map (fun entry -> entry.Cancel)
        |> Internal.cancelMany

    let private cancelEntriesExcept winnerIndex entries =
        entries
        |> List.indexed
        |> List.choose (fun (i, entry) ->
            if i = winnerIndex then None else Some entry.Cancel)
        |> Internal.cancelMany

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
                    |> List.map (sourceEntry outerCtx)

                for entry in entries do
                    do! startEntry entry

                let indexedTakes =
                    entries
                    |> List.mapi (fun i entry ->
                        EventChoice.take entry.Ch (fun msg -> i, msg))

                let! first =
                    EventChoice.orStop outerCtx (EventChoice.choose indexedTakes)

                match first with
                | Choice2Of2 () ->
                    do! cancelEntries entries

                | Choice1Of2 (winnerIndex, firstMsg) ->
                    do! cancelEntriesExcept winnerIndex entries

                    let winner =
                        entries.[winnerIndex]

                    let rec forward msg =
                        job {
                            match msg with
                            | Next x ->
                                do! obs.OnNext x

                                let! next =
                                    EventChoice.orStop outerCtx (Ch.take winner.Ch)

                                match next with
                                | Choice2Of2 () ->
                                    do! EventChoice.cancel winner.Cancel

                                | Choice1Of2 msg' ->
                                    return! forward msg'

                            | Error e ->
                                do! EventChoice.cancel winner.Cancel
                                do! obs.OnError e

                            | Completed ->
                                do! EventChoice.cancel winner.Cancel
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
                    |> List.map (sourceEntry outerCtx)

                for entry in entries do
                    do! startEntry entry

                let cancelOf i =
                    entries.[i].Cancel

                let channelOf i =
                    entries.[i].Ch

                let cancelAll () =
                    cancelEntries entries

                let cancelExcept winnerIndex =
                    cancelEntriesExcept winnerIndex entries

                // Forward the winner's remaining stream after its first value.
                let rec forward winnerIndex =
                    job {
                        let! next =
                            EventChoice.orStop outerCtx (Ch.take (channelOf winnerIndex))

                        match next with
                        | Choice2Of2 () ->
                            do! EventChoice.cancel (cancelOf winnerIndex)

                        | Choice1Of2 (Next x) ->
                            do! obs.OnNext x
                            return! forward winnerIndex

                        | Choice1Of2 (Error e) ->
                            do! EventChoice.cancel (cancelOf winnerIndex)
                            do! obs.OnError e

                        | Choice1Of2 Completed ->
                            do! EventChoice.cancel (cancelOf winnerIndex)
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
                                    EventChoice.take (channelOf i) (fun msg -> i, msg))

                            let! winner =
                                EventChoice.orStop outerCtx (EventChoice.choose indexedTakes)

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
                                do! EventChoice.cancel (cancelOf i)
                                return! select (List.filter (fun j -> j <> i) active)
                    }

                do! select [ 0 .. entries.Length - 1 ]
        }

    // --- Task boundaries (folded in from the former `TaskInterop` module) -----

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
                        EventChoice.orStop ctx (IVar.read outcome)

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
                AsyncRx.runJob
                    source
                    (fun x -> job { results.Add x })
                    (fun e -> job { raise e })
                    (fun () -> Job.unit ())

            return List.ofSeq results
        }

    // --- AsyncSeq boundaries (folded in from the former AsyncRxAsyncSeq.fsx) ---
    // A small bidirectional bridge between the push-shaped kernel and the
    // pull-shaped FSharp.Control.AsyncSeq: `ofAsyncSeq` (pull -> push) and
    // `toAsyncSeq` (push -> pull, via a Hopac `Ch` synchronous rendezvous --
    // lossless, bounded, no buffer-size policy). The bridge crosses the
    // Async <-> Job runtime boundary once per element, so keep it off
    // Hopac-native hot paths. Design notes: docs/asyncrx-asyncseq-plan.md.

    /// A faulted AsyncSeq/Task arrives wrapped in an AggregateException (the same
    /// Task-boundary behaviour `ofTaskFactory` above unwraps). Peel a
    /// single-inner one so the bridge surfaces the source's real exception rather
    /// than "One or more errors occurred."
    let rec private unwrapAggregate (ex: exn) : exn =
        match ex with
        | :? System.AggregateException as agg when agg.InnerExceptions.Count = 1 ->
            unwrapAggregate agg.InnerExceptions.[0]
        | _ -> ex

    /// pull -> push. A near-exact mirror of `ofSeq` (above): acquire the
    /// enumerator inside the protected scope, iterate with a stack-safe Hopac
    /// `while` loop, dispose *before* completing, and turn any
    /// acquisition/iteration/disposal failure into exactly one `OnError`. The
    /// only change from `ofSeq` is that each step is an async pull.
    ///
    /// AsyncSeq 4.x is built on the BCL `IAsyncEnumerable<'T>`, so the steps are
    /// `MoveNextAsync () : ValueTask<bool>` / `Current` / `DisposeAsync ()`, bridged
    /// into the Hopac job via `Async.AwaitTask` (which unwraps a single inner
    /// exception, so a throwing source surfaces its real message, not an
    /// AggregateException). Backpressure falls out for free: `do! obs.OnNext x`
    /// awaits the downstream Job before the next `MoveNextAsync`, so the pull is
    /// paced by the consumer. Async disposal forces `Job.tryFinallyJob` rather than
    /// the kernel's synchronous `try/finally`, but the lifecycle ordering matches.
    let ofAsyncSeq (xs: AsyncSeq<'T>) : AsyncObservable<'T> =
        fun ctx obs ->
            let mutable terminated = false
            let mutable enumerator : System.Collections.Generic.IAsyncEnumerator<'T> option = None

            let forwardError (ex: exn) = job {
                if not terminated then
                    terminated <- true
                    do! obs.OnError ex
            }

            // DisposeAsync at most once, surfacing any failure so the caller can
            // decide whether it becomes the (single) terminal.
            let disposeAsync () : Job<exn option> =
                match enumerator with
                | None -> Job.result None
                | Some en ->
                    enumerator <- None
                    job {
                        try
                            do! Job.fromAsync (Async.AwaitTask (en.DisposeAsync().AsTask()))
                            return None
                        with ex -> return Some (unwrapAggregate ex)
                    }

            Job.tryFinallyJob
                (job {
                    try
                        // Acquire inside the protected scope: a throwing
                        // GetAsyncEnumerator becomes one OnError instead of escaping.
                        enumerator <- Some (xs.GetAsyncEnumerator(CancellationToken.None))
                        let en = enumerator.Value
                        let mutable finished = false

                        // Hopac's job `while` loop is iterative, so this stays
                        // stack-safe even for fully synchronous AsyncSeqs.
                        while not finished && not terminated && not (ctx.IsStopped()) do
                            let! hasNext = Job.fromAsync (Async.AwaitTask (en.MoveNextAsync().AsTask()))
                            if hasNext then do! obs.OnNext en.Current
                            else finished <- true

                        // Normal exhaustion: dispose *before* completing, and turn a
                        // disposal failure into the single terminal error.
                        if finished && not terminated && not (ctx.IsStopped()) then
                            let! disposed = disposeAsync ()
                            match disposed with
                            | Some ex -> do! forwardError ex
                            | None ->
                                terminated <- true
                                do! obs.OnCompleted()
                    with ex ->
                        do! forwardError (unwrapAggregate ex)
                 })
                // Error / stop / acquisition-failure paths: ensure disposal,
                // swallowing any failure (the terminal is already decided).
                (job {
                    let! _ = disposeAsync ()
                    return ()
                 })

    /// push -> pull. Bridge through a Hopac `Ch` (synchronous rendezvous): the
    /// producer's `OnNext` *gives* on the channel, the AsyncSeq generator *takes* on
    /// each pull, and the give does not complete until the take happens. Lossless,
    /// bounded, no buffer-size knob. The terminal is carried in-band as the kernel's
    /// `Notification`, so no side completion signal is needed.
    ///
    /// `Internal.forwardInto` + `Internal.giveOrStop` are reused verbatim:
    /// `forwardInto` subscribes the source on its own job and funnels every
    /// notification (turning a thrown source into one `Error`); `giveOrStop` makes
    /// each give a rendezvous that loses to Stop, so cancelling the root token
    /// withdraws a blocked give. The `finally` cancels that token on normal end OR
    /// early disposal, unwinding the producer subscription either way.
    let toAsyncSeq (source: AsyncObservable<'T>) : AsyncSeq<'T> =
        asyncSeq {
            let token, ctx = Internal.rootContext ()
            let ch = Ch<Notification<'T>> ()

            // Start the producer and return immediately (forwardInto Job.start's it).
            do!
                Job.toAsync (
                    Internal.forwardInto
                        ctx
                        source
                        Notification.Next
                        Notification.Error
                        Notification.Completed
                        (Internal.giveOrStop ctx ch))

            try
                let mutable go = true
                while go do
                    let! n = Job.toAsync (Ch.take ch)   // pull == take (rendezvous)
                    match n with
                    | Notification.Next x    -> yield x
                    | Notification.Completed -> go <- false
                    | Notification.Error e   -> raise e  // surface the fault on the seq
            finally
                // Normal end OR early disposal (consumer stopped pulling): cancel so a
                // blocked giveOrStop is withdrawn and the producer subscription
                // unwinds. cancel is an idempotent, non-blocking IVar fill; fire it
                // and forget (AsyncSeq's finally is synchronous, and we must not risk
                // a Hopac.run from a worker thread).
                Hopac.start (EventChoice.cancel token)
        }

    // --- Computation-expression builder classes -------------------------------
    // (folded in from the former `AsyncRxCE`/`ClauseCE` modules). The CE *values*
    // `asyncRx`/`clauses` live at the top level, so a single `open AsyncRxHopac`
    // yields the keywords without flooding the operator names into scope.

    type AsyncRxBuilder() =

        member _.Return(x: 'T) : AsyncObservable<'T> =
            singleton x

        member _.ReturnFrom(source: AsyncObservable<'T>) : AsyncObservable<'T> =
            source

        member _.Zero() : AsyncObservable<unit> =
            empty

        member _.Delay(f: unit -> AsyncObservable<'T>) : AsyncObservable<'T> =
            fun ctx obs ->
                f () ctx obs

        member _.Bind
            (
                source: AsyncObservable<'T>,
                f: 'T -> AsyncObservable<'U>
            ) : AsyncObservable<'U> =

            bind f source

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

            zip left right

        member _.BindReturn
            (
                source: AsyncObservable<'T>,
                f: 'T -> 'U
            ) : AsyncObservable<'U> =

            map f source

        member _.Run(source: AsyncObservable<'T>) : AsyncObservable<'T> =
            source

    type ClausesBuilder() =

        member _.Yield(x: AsyncObservable<'T>) =
            [ x ]

        member _.Yield(_: unit) : AsyncObservable<'T> list =
            []

        member _.YieldFrom(xs: AsyncObservable<'T> list) =
            xs

        member _.Zero() : AsyncObservable<'T> list =
            []

        member _.Combine(xs: AsyncObservable<'T> list, ys: AsyncObservable<'T> list) =
            xs @ ys

        member _.Delay(f: unit -> AsyncObservable<'T> list) =
            f ()

        /// General chooser-based clause. A `None` completes this clause empty,
        /// so it cannot win unless it produces a value before another clause does.
        [<CustomOperation("case")>]
        member _.Case
            (
                state: AsyncObservable<'T> list,
                source: AsyncObservable<'U>,
                chooser: 'U -> 'T option
            ) =
            state @ [ choose chooser source ]

        /// Match when `source` emits a value satisfying `predicate`, then return
        /// the supplied result as this clause's value.
        [<CustomOperation("whenValue")>]
        member _.WhenValue
            (
                state: AsyncObservable<'U> list,
                source: AsyncObservable<'T>,
                predicate: 'T -> bool,
                result: 'U
            ) =
            state @ [ source |> filter predicate |> map (fun _ -> result) ]

        /// Add a boolean guard clause. A false guard completes empty and is
        /// ignored by `AsyncObservable.firstValue`.
        [<CustomOperation("guardOn")>]
        member _.GuardOn
            (
                state: AsyncObservable<'T> list,
                condition: bool,
                result: 'T
            ) =
            state @ [ guard condition |> map (fun () -> result) ]

        /// Add an unguarded clause. This is not ordered fallback semantics; it
        /// participates in the same first-value race as every other clause.
        [<CustomOperation("always")>]
        member _.Always
            (
                state: AsyncObservable<'T> list,
                source: AsyncObservable<'T>
            ) =
            state @ [ source ]

        /// Selects the first clause to emit a *value*. A non-matching clause
        /// (built with `AsyncObservable.guard` / `AsyncObservable.choose`) completes
        /// without emitting; `AsyncObservable.firstValue` discards such
        /// empty-completers rather than letting their completion win the race (which
        /// plain `amb` would), so a slower clause that actually matches still gets
        /// selected.
        member _.Run(xs: AsyncObservable<'T> list) =
            firstValue xs

// Top-level CE values: a single `open AsyncRxHopac` brings `asyncRx`/`clauses`
// into scope (the builder *keywords*) without also flooding the operator names.

[<AutoOpen>]
module ComputationExpressions =

    let asyncRx =
        AsyncObservable.AsyncRxBuilder()

    let clauses =
        AsyncObservable.ClausesBuilder()
