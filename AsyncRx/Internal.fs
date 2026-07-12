namespace AsyncRxHopac

open Hopac
open Core

module internal Internal =

    let rootContext () : EventChoice.Token * SubscribeContext =
        let token = EventChoice.create ()

        token,
        { Stop = EventChoice.asEventChoice token
          IsStopped = fun () -> EventChoice.isCancellationRequested token }

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
            EventChoice.choose [
                ctx.Stop
                Ch.give ch value
            ]

    let takeOrStop (ctx: SubscribeContext) (ch: Ch<'T>) : Job<Choice<'T, unit>> =
        if ctx.IsStopped() then
            result (Choice2Of2 ())
        else
            EventChoice.orStop ctx (Ch.take ch)

    /// Subscribe `source` on its own job, forwarding each notification into an
    /// operator mailbox. A thrown source is routed through the same error path as
    /// `OnError`, matching the per-operator terminal funnel.
    let forwardInto
        (ctx: SubscribeContext)
        (source: AsyncObservable<'T>)
        (onNext: 'T -> 'Msg)
        (onError: exn -> 'Msg)
        (onCompleted: 'Msg)
        (send: 'Msg -> Job<unit>)
        : Job<unit> =

        Job.start <| job {
            try
                do! source ctx {
                    OnNext = fun x ->
                        send (onNext x)

                    OnError = fun e ->
                        send (onError e)

                    OnCompleted = fun () ->
                        send onCompleted
                }
            with e ->
                do! send (onError e)
        }

    let cancelMany (tokens: EventChoice.Token list) : Job<unit> =
        job {
            for token in tokens do
                do! EventChoice.cancel token
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
