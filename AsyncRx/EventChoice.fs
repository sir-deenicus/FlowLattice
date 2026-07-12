namespace AsyncRxHopac

open Hopac

// `Core` holds only the library's types. It is opened explicitly here, rather than
// carrying `[<AutoOpen>]`, so that depending on the core types is always a visible
// `open` (in this file, and `open AsyncRxHopac.Core` in consumers) -- never an
// implicit side effect of `open AsyncRxHopac`. Opens are a deliberate choice.
open Core

module EventChoice =

    /// Named wrapper for Hopac's Alt mapping operator.
    let map (f: 'T -> 'U) (choice: EventChoice<'T>) : EventChoice<'U> =
        Alt.afterFun f choice

    /// Named wrapper for racing a set of selectable operations.
    let choose (choices: EventChoice<'T> list) : EventChoice<'T> =
        Alt.choose choices

    /// Create an event choice that fires when subscription Stop fires.
    let stop (ctx: SubscribeContext) (value: 'T) : EventChoice<'T> =
        map (fun () -> value) ctx.Stop

    /// Race a selectable operation against subscription Stop.
    let orStop (ctx: SubscribeContext) (choice: EventChoice<'T>) : EventChoice<Choice<'T, unit>> =
        choose [
            map Choice1Of2 choice
            stop ctx (Choice2Of2 ())
        ]

    /// Create an event choice from a channel receive.
    let take (ch: Ch<'T>) (f: 'T -> 'U) : EventChoice<'U> =
        map f (Ch.take ch)

    /// Create an event choice from a timeout.
    let afterMillis (milliseconds: int) (value: 'T) : EventChoice<'T> =
        map (fun () -> value) (timeOutMillis milliseconds)

    // --- Cancellation (absorbed from the former `Cancel` module) -------------
    // The public substrate for authoring cancellable combinators. Cancellation
    // is represented purely by an IVar latch; a real CancellationToken is created
    // only at boundaries that need one (see `AsyncObservable.ofTaskFactory`),
    // with an owned, bounded lifetime.

    type Token =
        private
            { Latch : IVar<unit> }

    let create () : Token =
        { Latch = IVar<unit>() }

    let asEventChoice (token: Token) : EventChoice<unit> =
        IVar.read token.Latch

    let isCancellationRequested (token: Token) : bool =
        IVar.Now.isFull token.Latch

    let cancel (token: Token) : Job<unit> =
        IVar.tryFill token.Latch ()

    let childContext (parent: SubscribeContext) : Token * SubscribeContext =
        let token = create ()

        let ctx =
            { Stop =
                choose [
                    parent.Stop
                    asEventChoice token
                ]

              IsStopped =
                fun () ->
                    parent.IsStopped()
                    || isCancellationRequested token }

        token, ctx
