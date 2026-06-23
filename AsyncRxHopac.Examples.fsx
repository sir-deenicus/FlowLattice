#load "AsyncRxHopac.fsx"

open Hopac
open AsyncRxHopac                  // module prefixes (AsyncObservable, AsyncRx) + the asyncRx/clauses CE values

let backpressureAndCancellationDemo () =
    Hopac.run <| job {
        let fastA =
            AsyncObservable.intervalMillis 10
            |> AsyncObservable.map (fun i -> sprintf "A:%d" i)

        let fastB =
            AsyncObservable.intervalMillis 15
            |> AsyncObservable.map (fun i -> sprintf "B:%d" i)

        let source =
            AsyncObservable.merge [ fastA; fastB ]

        let! subscription =
            AsyncRx.subscribeJob
                source
                (fun x -> job {
                    printfn "start processing %s" x

                    // Pretend this is an async DB write, HTTP call, disk write, etc.
                    do! timeOutMillis 100

                    printfn "done  processing %s" x
                })
                (fun e -> job {
                    printfn "error: %s" e.Message
                })
                (fun () -> job {
                    printfn "completed"
                })

        do! timeOutMillis 550

        printfn "cancelling subscription"

        do! subscription.Stop()

        // Give child jobs a moment to observe cancellation.
        do! timeOutMillis 100
    }

let productMergeDemo () =
    let a =
        AsyncObservable.singleton 10

    let b =
        AsyncObservable.singleton 32

    let result =
        asyncRx {
            let! x = a
            and! y = b
            return x + y
        }

    AsyncRx.runBlocking result (printfn "sum = %d")

/// Walks the `asyncRx { }` computation expression end to end: `for`
/// (enumeration), `let!`/`and!` (bind + applicative product), `do!`
/// (statement bind), `while` (loop), statement `Combine`, and `return`.
let asyncRxBuilderDemo () =
    let program =
        asyncRx {
            // `for` enumerates a sequence, running the body per element; its
            // unit result is `Combine`d with everything that follows.
            for n in [ 1; 2; 3 ] do
                printfn "for: %d" n

            // `let!` binds one value; `and!` pairs a second source
            // applicatively (the zip product surfaced as `and!`).
            let! x = AsyncObservable.singleton 10
            and! y = AsyncObservable.singleton 32

            // `do!` sequences a unit-valued observable for its effect.
            do! AsyncObservable.singleton ()

            // `while` loops while the guard holds.
            let mutable k = 0
            while k < 2 do
                printfn "while: k=%d" k
                k <- k + 1

            return x + y
        }

    AsyncRx.runBlocking program (printfn "asyncRx result = %d")

let branchChoiceDemo () =
    let fast =
        AsyncObservable.intervalMillis 50
        |> AsyncObservable.map (fun i -> sprintf "fast:%d" i)
        |> AsyncObservable.take 1

    let slow =
        AsyncObservable.intervalMillis 200
        |> AsyncObservable.map (fun i -> sprintf "slow:%d" i)
        |> AsyncObservable.take 1

    let winner =
        AsyncObservable.amb [ fast; slow ]

    AsyncRx.runBlocking winner (printfn "winner = %s")

let patternLikeDemo () =
    let a =
        AsyncObservable.singleton false

    let b =
        AsyncObservable.singleton true

    let result =
        clauses {
            whenValue a ((=) true) "a was true"
            whenValue b ((=) true) "b was true"
            always (asyncRx {
                let! x = a
                and! y = b
                return sprintf "fallback: a || b = %b" (x || y)
            })
        }

    AsyncRx.runBlocking result (printfn "%s")

printfn "== productMergeDemo =="
productMergeDemo ()

printfn "\n== asyncRxBuilderDemo =="
asyncRxBuilderDemo ()

printfn "\n== branchChoiceDemo =="
branchChoiceDemo ()

printfn "\n== patternLikeDemo =="
patternLikeDemo ()

printfn "\n== backpressureAndCancellationDemo =="
backpressureAndCancellationDemo ()
