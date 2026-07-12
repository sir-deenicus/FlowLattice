namespace AsyncRxHopac

open Hopac
open Core

module AsyncRx =

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
                Stop = fun () -> EventChoice.cancel cancelToken
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

    let runJobBlocking (work: Job<'T>) : 'T =
        Hopac.run work

    let startJob (work: Job<'T>) : System.Threading.Tasks.Task<'T> =
        Hopac.startAsTask work

    let runBlocking (source: AsyncObservable<'T>) (onNext: 'T -> unit) : unit =
        Hopac.run <|
            runJob
                source
                (fun x -> job { onNext x })
                (fun e -> job { raise e })
                (fun () -> Job.unit ())

    /// Run `source` to its terminal, blocking the calling thread until then, with
    /// plain (synchronous) lifecycle handlers. This is the Hopac-free entry point
    /// for *consuming* a stream: no `job { }` and no `Hopac.run` at the call site,
    /// so application/example code can stay in AsyncRx vocabulary. Pipe-friendly
    /// (source last): `commands |> run onCmd onError onDone`. The handlers are
    /// synchronous, so the sink exerts no backpressure on the source -- when the
    /// consumer must pace the producer, drop to `runJob` / `subscribeJob` with
    /// `Job<unit>` handlers instead.
    let run
        (onNext: 'T -> unit)
        (onError: exn -> unit)
        (onCompleted: unit -> unit)
        (source: AsyncObservable<'T>)
        : unit =
        Hopac.run <|
            runJob
                source
                (fun x -> job { onNext x })
                (fun e -> job { onError e })
                (fun () -> job { onCompleted () })

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
