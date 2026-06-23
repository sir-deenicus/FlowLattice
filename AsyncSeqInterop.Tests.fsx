(*
    AsyncSeqInterop.Tests.fsx

    Assertion-based regression checks for the AsyncRx <-> AsyncSeq bridge
    (AsyncObservable.ofAsyncSeq / AsyncObservable.toAsyncSeq). Same harness style as AsyncRxHopac.Tests.fsx:
    record notification sequences, count assertions, exit non-zero on failure.
    Run:  dotnet fsi AsyncSeqInterop.Tests.fsx
*)

#load "AsyncRxHopac.fsx"

open System
open System.Threading
open Hopac
open FSharp.Control
open AsyncRxHopac
open AsyncRxHopac.Core
open AsyncRxHopac.AsyncRx          // bare run-family names: subscribeJob/...

// ---- recorder (push side) -------------------------------------------------

type Ev<'T> =
    | N of 'T
    | E of string
    | C

/// Subscribe, collect notifications for `waitMs`, then Stop and return the
/// recorded sequence. Bounded so a deadlock fails visibly rather than hanging.
let record (waitMs: int) (source: AsyncObservable<'T>) : Ev<'T> list =
    let acc = ResizeArray<Ev<'T>>()
    let gate = obj ()
    let add e = lock gate (fun () -> acc.Add e)

    Hopac.run <| job {
        let! sub =
            subscribeJob
                source
                (fun x -> job { add (N x) })
                (fun e -> job { add (E e.Message) })
                (fun () -> job { add C })

        do! timeOutMillis waitMs
        do! sub.Stop()
        do! timeOutMillis 20
    }

    List.ofSeq acc

// ---- pull-side helper -----------------------------------------------------

let toList (xs: AsyncSeq<'T>) : 'T list =
    AsyncSeq.toListAsync xs |> Async.RunSynchronously

// AsyncSeq 4.x sits on the BCL IAsyncEnumerable, so manual control uses
// GetAsyncEnumerator / MoveNextAsync / Current / DisposeAsync.
let getEnum (xs: AsyncSeq<'T>) : System.Collections.Generic.IAsyncEnumerator<'T> =
    xs.GetAsyncEnumerator(CancellationToken.None)

let moveNext (e: System.Collections.Generic.IAsyncEnumerator<'T>) : Async<'T option> =
    async {
        let! hasNext = Async.AwaitTask (e.MoveNextAsync().AsTask())
        return (if hasNext then Some e.Current else None)
    }

let disposeEnum (e: System.Collections.Generic.IAsyncEnumerator<'T>) : Async<unit> =
    Async.AwaitTask (e.DisposeAsync().AsTask())

// ---- assertions -----------------------------------------------------------

let mutable failures = 0

let check name (cond: bool) =
    if cond then printfn "PASS  %s" name
    else
        failures <- failures + 1
        printfn "FAIL  %s" name

let checkEq name (expected: 'a) (actual: 'a) =
    if expected = actual then printfn "PASS  %s" name
    else
        failures <- failures + 1
        printfn "FAIL  %s\n        expected: %A\n        actual:   %A" name expected actual

// ===========================================================================
// AsyncObservable.ofAsyncSeq : AsyncSeq<'T> -> AsyncObservable<'T>   (pull -> push)
// ===========================================================================

checkEq "AsyncObservable.ofAsyncSeq: forwards all elements in order"
    [ N 1; N 2; N 3; C ]
    (record 200 (AsyncObservable.ofAsyncSeq (AsyncSeq.ofSeq [ 1; 2; 3 ])))

checkEq "AsyncObservable.ofAsyncSeq: empty seq -> just OnCompleted"
    [ C ]
    (record 200 (AsyncObservable.ofAsyncSeq (AsyncSeq.ofSeq ([]: int list))))

let throwingSeq =
    asyncSeq {
        yield 1
        failwith "boom"
    }

checkEq "AsyncObservable.ofAsyncSeq: throwing MoveNext -> exactly one OnError after the value"
    [ N 1; E "boom" ]
    (record 200 (AsyncObservable.ofAsyncSeq throwingSeq))

// Stop mid-stream halts pulling and disposes the enumerator.
let mutable ofDisposed = false

let infiniteSrc =
    asyncSeq {
        try
            let mutable i = 0
            while true do
                yield i
                i <- i + 1
                do! Async.Sleep 10
        finally
            ofDisposed <- true
    }

let ofStopEvents =
    Hopac.run <| job {
        let acc = ResizeArray<int>()
        let gate = obj ()
        let! sub =
            subscribeJob
                (AsyncObservable.ofAsyncSeq infiniteSrc)
                (fun x -> job { lock gate (fun () -> acc.Add x) })
                (fun _ -> job { () })
                (fun () -> job { () })

        do! timeOutMillis 60
        do! sub.Stop()
        do! timeOutMillis 60
        return List.ofSeq acc
    }

check "AsyncObservable.ofAsyncSeq: Stop mid-stream disposes the enumerator" ofDisposed
check "AsyncObservable.ofAsyncSeq: Stop mid-stream emitted some values then halted" (not (List.isEmpty ofStopEvents))

// Backpressure: a slow OnNext paces MoveNext (no read-ahead past one element).
let mutable produced = 0
let mutable consumed = 0
let mutable maxAhead = 0

let pacedSrc =
    asyncSeq {
        for i in 0 .. 4 do
            produced <- produced + 1
            yield i
    }

Hopac.run <| job {
    let! sub =
        subscribeJob
            (AsyncObservable.ofAsyncSeq pacedSrc)
            (fun (_: int) -> job {
                let ahead = produced - consumed
                if ahead > maxAhead then maxAhead <- ahead
                do! timeOutMillis 20
                consumed <- consumed + 1 })
            (fun _ -> job { () })
            (fun () -> job { () })

    do! sub.Completion
}

check "AsyncObservable.ofAsyncSeq: backpressure -> no read-ahead past one element" (maxAhead <= 1)
checkEq "AsyncObservable.ofAsyncSeq: backpressure -> consumed every element" 5 consumed

// ===========================================================================
// AsyncObservable.toAsyncSeq : AsyncObservable<'T> -> AsyncSeq<'T>   (push -> pull)
// ===========================================================================

checkEq "AsyncObservable.toAsyncSeq: lossless + ordered"
    [ 10; 20; 30 ]
    (toList (AsyncObservable.toAsyncSeq (AsyncObservable.ofSeq [ 10; 20; 30 ])))

checkEq "AsyncObservable.toAsyncSeq: source OnCompleted -> seq ends"
    ([]: int list)
    (toList (AsyncObservable.toAsyncSeq (AsyncObservable.empty: AsyncObservable<int>)))

let erroredMsg =
    try
        AsyncObservable.toAsyncSeq (AsyncObservable.fail (exn "kaboom"): AsyncObservable<int>)
        |> toList
        |> ignore
        "no-error"
    with e -> e.Message

checkEq "AsyncObservable.toAsyncSeq: source OnError -> seq raises that exception" "kaboom" erroredMsg

// Bounded: the producer's give blocks until the consumer pulls (no buffering
// ahead of demand). Pull exactly three, then assert the producer ran at most
// one element ahead.
let mutable producedB = 0

let boundedSrc : AsyncObservable<int> =
    fun ctx obs -> job {
        let mutable i = 0
        while not (ctx.IsStopped()) && i < 100 do
            producedB <- producedB + 1
            do! obs.OnNext i
            i <- i + 1
    }

let firstThree =
    Async.RunSynchronously <| async {
        let e = getEnum (AsyncObservable.toAsyncSeq boundedSrc)
        let! a = moveNext e
        let! b = moveNext e
        let! c = moveNext e
        do! disposeEnum e
        return [ a; b; c ]
    }

checkEq "AsyncObservable.toAsyncSeq: bounded -> delivers in order" [ Some 0; Some 1; Some 2 ] firstThree
check "AsyncObservable.toAsyncSeq: bounded -> no buffering ahead of demand (produced <= pulled+1)" (producedB <= 4)

// Early disposal stops the source: disposing the enumerator before completion
// fires the source's Stop and unwinds its subscription.
let stoppedIv = IVar<unit>()

let stopAwareSrc : AsyncObservable<int> =
    fun ctx obs ->
        Job.tryFinallyJob
            (job {
                let mutable i = 0
                while not (ctx.IsStopped()) do
                    do! obs.OnNext i
                    i <- i + 1
             })
            (IVar.tryFill stoppedIv ())

Async.RunSynchronously <| async {
    let e = getEnum (AsyncObservable.toAsyncSeq stopAwareSrc)
    let! _ = moveNext e
    let! _ = moveNext e
    do! disposeEnum e
    return ()
}

let didStop =
    Hopac.run (
        Alt.choose [
            Alt.afterFun (fun () -> true) (IVar.read stoppedIv)
            Alt.afterFun (fun () -> false) (timeOutMillis 1000)
        ])

check "AsyncObservable.toAsyncSeq: early disposal stops the source" didStop

// ===========================================================================
// Round-trip identity through both bridges
// ===========================================================================

checkEq "round-trip: ofSeq |> AsyncObservable.ofAsyncSeq |> AsyncObservable.toAsyncSeq = identity"
    [ 1 .. 5 ]
    ([ 1 .. 5 ] |> AsyncSeq.ofSeq |> AsyncObservable.ofAsyncSeq |> AsyncObservable.toAsyncSeq |> toList)

// ---- summary --------------------------------------------------------------

printfn "\n%s" (String.replicate 50 "-")
if failures = 0 then printfn "ALL TESTS PASSED"
else printfn "%d TEST(S) FAILED" failures

exit failures
