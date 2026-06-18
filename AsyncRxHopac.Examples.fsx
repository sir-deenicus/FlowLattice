#load "AsyncRxHopac.fsx"

open Hopac
open AsyncRxHopac
open AsyncRxHopac.AsyncRx
open AsyncRxHopac.ClauseCE
open AsyncRxHopac.Subscribe

let backpressureAndCancellationDemo () =
    Hopac.run <| job {
        let fastA =
            interval 10
            |> map (fun i -> sprintf "A:%d" i)

        let fastB =
            interval 15
            |> map (fun i -> sprintf "B:%d" i)

        let source =
            merge [ fastA; fastB ]

        let! subscription =
            subscribeJob
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
        value 10

    let b =
        value 32

    let result =
        asyncRx {
            let! x = a
            and! y = b
            return x + y
        }

    runBlocking result (printfn "sum = %d")

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
            let! x = value 10
            and! y = value 32

            // `do!` sequences a unit-valued observable for its effect.
            do! value ()

            // `while` loops while the guard holds.
            let mutable k = 0
            while k < 2 do
                printfn "while: k=%d" k
                k <- k + 1

            return x + y
        }

    runBlocking program (printfn "asyncRx result = %d")

let branchChoiceDemo () =
    let fast =
        interval 50
        |> map (fun i -> sprintf "fast:%d" i)
        |> take 1

    let slow =
        interval 200
        |> map (fun i -> sprintf "slow:%d" i)
        |> take 1

    let winner =
        race [ fast; slow ]

    runBlocking winner (printfn "winner = %s")

let patternLikeDemo () =
    let a =
        value false

    let b =
        value true

    let result =
        clauses {
            yield case a (function true -> Some "a was true" | _ -> None)
            yield case b (function true -> Some "b was true" | _ -> None)

            yield asyncRx {
                let! x = a
                and! y = b
                return sprintf "fallback: a || b = %b" (x || y)
            }
        }

    runBlocking result (printfn "%s")

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
