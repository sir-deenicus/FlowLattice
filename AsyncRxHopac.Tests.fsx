(*
    AsyncRxHopac.Tests.fsx

    Assertion-based regression checks for the AsyncRx kernel. Records full
    notification sequences (not just printed output) and asserts terminal counts.
    Run:  dotnet fsi AsyncRxHopac.Tests.fsx
*)

#load "AsyncRxHopac.fsx"

open Hopac
open AsyncRxHopac
open AsyncRxHopac.Core
open AsyncRxHopac.AsyncRx          // bare run-family names: subscribeJob/runJob/...

// ---- recorder -------------------------------------------------------------

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

let terminalCount events =
    events |> List.filter (function C | E _ -> true | N _ -> false) |> List.length

// ---- building blocks ------------------------------------------------------

let value x = AsyncObservable.singleton x
let ofSeq xs = AsyncObservable.ofSeq xs
let fail<'T> (e: exn) : AsyncObservable<'T> = AsyncObservable.fail e

/// A unit observable that runs a side effect, then completes.
let effect (f: unit -> unit) : AsyncObservable<unit> =
    fun _ obs -> job {
        f ()
        do! obs.OnCompleted()
    }

checkEq "switchLatest: a single inner stream's value is forwarded"
    [ N 42; C ]
    (record 100 (AsyncObservable.switchLatest (AsyncObservable.singleton (AsyncObservable.singleton 42))))

// ---- P0-1: terminal-safe bind --------------------------------------------

checkEq "bind: single value, one completion"
    [ N 1; C ]
    (record 100 (asyncRx { let! x = value 1 in return x }))

checkEq "bind: multi-value flatten"
    [ N 1; N 10; N 2; N 20; N 3; N 30; C ]
    (record 100 (ofSeq [ 1; 2; 3 ] |> AsyncObservable.bind (fun x -> ofSeq [ x; x * 10 ])))

let innerErr =
    ofSeq [ 1; 2; 3 ]
    |> AsyncObservable.bind (fun x -> if x = 1 then fail (exn "boom") else value x)
    |> record 100

checkEq "bind: inner error -> exactly one error, no later values/completion"
    [ E "boom" ] innerErr

checkEq "bind: outer error -> exactly one error"
    [ E "outer" ]
    (record 100 ((fail (exn "outer")) |> AsyncObservable.bind (fun x -> value x)))

// ---- P0-2: completion-based Combine --------------------------------------

checkEq "combine: empty;value emits second, one completion"
    [ N 5; C ]
    (record 100 (asyncRx.Combine(AsyncObservable.empty, value 5)))

let mutable combineSecond = 0
let combineMultiFirst =
    asyncRx.Combine(ofSeq [ (); (); () ], effect (fun () -> combineSecond <- combineSecond + 1))
let combineEvents = record 100 combineMultiFirst
check "combine: multi-value first starts second exactly once"
    (combineSecond = 1)
check "combine: one terminal after sequencing"
    (terminalCount combineEvents = 1)

checkEq "combine: first error -> second never subscribed"
    [ E "first" ]
    (record 100 (asyncRx.Combine(fail (exn "first"), effect (fun () -> combineSecond <- 99))))

// ---- P0-3: While ----------------------------------------------------------

let mutable guardCalls = 0
let mutable bodyRuns = 0
let whileObs =
    asyncRx.While(
        (fun () -> guardCalls <- guardCalls + 1; bodyRuns < 3),
        effect (fun () -> bodyRuns <- bodyRuns + 1))

check "while: guard not evaluated before subscription" (guardCalls = 0)
let whileEvents = record 100 whileObs
checkEq "while: body runs 3 times" 3 bodyRuns
checkEq "while: guard checked 4 times" 4 guardCalls
checkEq "while: single completion" [ C ] whileEvents

// An empty-completing body must still advance the loop. The guard self-terminates
// (true, true, false) so we verify the loop iterates rather than stalling.
let mutable emptyIters = 0
let whileEmptyBody =
    asyncRx.While(
        (fun () -> emptyIters <- emptyIters + 1; emptyIters <= 2),
        AsyncObservable.empty)               // empty-completing body
let whileEmptyEvents = record 100 whileEmptyBody
checkEq "while: empty-completing body still advances" 3 emptyIters
checkEq "while: empty-body loop completes once" [ C ] whileEmptyEvents

// ---- P1-3: lazy, in-order For --------------------------------------------

let mutable enumerated = 0
let lazySeq = seq { for i in 1 .. 3 do enumerated <- enumerated + 1; yield i }
let order = ResizeArray<int>()
let forObs = asyncRx.For(lazySeq, fun x -> effect (fun () -> order.Add x))

check "for: sequence not enumerated before subscription" (enumerated = 0)
let forEvents = record 100 forObs
checkEq "for: body runs in order for all elements" [ 1; 2; 3 ] (List.ofSeq order)
checkEq "for: single completion" [ C ] forEvents

let forErr =
    asyncRx.For([ 1; 2; 3 ], fun x -> if x = 2 then fail (exn "mid") else effect (fun () -> ()))
    |> record 100
checkEq "for: body error -> one error, no completion" [ E "mid" ] forErr

// ---- stack safety (large synchronous loops) ------------------------------

/// Drive a finite source to its terminal notification (no Stop involved).
let recordToEnd (source: AsyncObservable<'T>) : Ev<'T> list =
    let acc = ResizeArray<Ev<'T>>()
    Hopac.run <|
        runJob source
            (fun x -> job { acc.Add(N x) })
            (fun e -> job { acc.Add(E e.Message) })
            (fun () -> job { acc.Add C })
    List.ofSeq acc

/// Deterministic recorder: subscribe and await the subscription's `Completion`
/// signal (no timeouts), then return the recorded sequence. Exercises the P2-6
/// completion job and the rewritten `ofSeq` lifecycle.
let recordUntilDone (source: AsyncObservable<'T>) : Ev<'T> list =
    let acc = ResizeArray<Ev<'T>>()
    Hopac.run <| job {
        let! sub =
            subscribeJob
                source
                (fun x -> job { acc.Add(N x) })
                (fun e -> job { acc.Add(E e.Message) })
                (fun () -> job { acc.Add C })
        do! sub.Completion
    }
    List.ofSeq acc

// ---- source-authoring helpers -------------------------------------------

let unfoldEventChoiceFinite =
    AsyncObservable.unfoldEventChoice 0 (fun _ state ->
        if state >= 3 then
            EventChoice.afterMillis 0 AsyncObservable.Complete
        else
            EventChoice.afterMillis 0 (AsyncObservable.Emit (state + 1, state + 1)))

checkEq "unfoldEventChoice: finite state machine emits then completes"
    [ N 1; N 2; N 3; C ]
    (recordUntilDone unfoldEventChoiceFinite)

checkEq "unfoldEventChoice: fail step -> one error"
    [ E "boom" ]
    (recordUntilDone (
        AsyncObservable.unfoldEventChoice () (fun _ () ->
            EventChoice.afterMillis 0 (AsyncObservable.Fail (exn "boom")))))

checkEq "repeatEventChoice: emits until downstream take stops driver"
    [ N "tick"; N "tick"; C ]
    (recordUntilDone (
        AsyncObservable.repeatEventChoice (fun _ -> EventChoice.afterMillis 5 "tick")
        |> AsyncObservable.take 2))

let bigN = 100000

let mutable forCount = 0
let bigForEvents =
    recordToEnd (asyncRx.For(seq { 1 .. bigN }, fun _ -> effect (fun () -> forCount <- forCount + 1)))
check "for: 100k synchronous iterations do not overflow the stack"
    (forCount = bigN && bigForEvents = [ C ])

let mutable whileCount = 0
let bigWhileEvents =
    recordToEnd (asyncRx.While((fun () -> whileCount < bigN), effect (fun () -> whileCount <- whileCount + 1)))
check "while: 100k synchronous iterations do not overflow the stack"
    (whileCount = bigN && bigWhileEvents = [ C ])

// ---- P1-1: Task interop lifetime -----------------------------------------

open System.Threading
open System.Threading.Tasks

checkEq "task: normal completion -> one value + completion (and Stop after is safe)"
    [ N 42; C ]
    (record 100 (AsyncObservable.ofTaskFactory (fun _ -> Task.FromResult 42)))

// The Task boundary unwraps a single-inner AggregateException (P2-8), so the
// underlying fault message ("kaboom") surfaces instead of the AggregateException
// wrapper ("One or more errors occurred...").
let faultEvents =
    record 100 (AsyncObservable.ofTaskFactory (fun _ ->
        Task.Run(System.Func<int>(fun () -> raise (exn "kaboom")))))
checkEq "task: fault -> one error carrying the unwrapped message"
    [ E "kaboom" ] faultEvents

// Stop during a running task: the factory token is cancelled, the task is
// observed, and nothing is delivered downstream (and no disposed-CTS crash).
let mutable tokenSawCancel = false
let longTask (tok: CancellationToken) : Task<int> =
    Task.Run(System.Func<int>(fun () ->
        let sw = System.Diagnostics.Stopwatch.StartNew()
        while not tok.IsCancellationRequested && sw.ElapsedMilliseconds < 5000L do
            Thread.Sleep 10
        if tok.IsCancellationRequested then tokenSawCancel <- true
        tok.ThrowIfCancellationRequested()
        99), tok)

let stoppedEvents = record 50 (AsyncObservable.ofTaskFactory longTask)
checkEq "task: stop during task -> no notifications" ([]: Ev<int> list) stoppedEvents
check "task: stop during task -> factory token observed cancellation" tokenSawCancel

// ---- P0-5: boundary terminal gate (malformed sources) --------------------

let malformedDoubleError : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnError (exn "e1")
        do! obs.OnError (exn "e2")
    }

let malformedCompleteThenError : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnCompleted()
        do! obs.OnError (exn "late")
    }

let malformedValueAfterComplete : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnNext 1
        do! obs.OnCompleted()
        do! obs.OnNext 2
    }

let malformedErrorThenThrow : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnError (exn "real")
        raise (exn "post-error throw")
    }

checkEq "boundary: double error reduced to one" [ E "e1" ] (record 100 malformedDoubleError)
checkEq "boundary: complete-then-error -> completion only" [ C ] (record 100 malformedCompleteThenError)
checkEq "boundary: value-after-complete dropped" [ N 1; C ] (record 100 malformedValueAfterComplete)
checkEq "boundary: source throws after error -> one error" [ E "real" ] (record 100 malformedErrorThenThrow)

// ---- P0-2: Combine terminal gate over both sources -----------------------

let mutable secondStartedCount = 0
let firstDoubleErr : AsyncObservable<unit> =
    fun _ obs -> job {
        do! obs.OnError (exn "c1")
        do! obs.OnError (exn "c2")
    }
let combineDoubleErr =
    record 100 (asyncRx.Combine(firstDoubleErr, effect (fun () -> secondStartedCount <- secondStartedCount + 1)))
checkEq "combine: first double error -> exactly one error" [ E "c1" ] combineDoubleErr
check "combine: second not started after first error" (secondStartedCount = 0)

let secondCompleteThenErr : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnNext 7
        do! obs.OnCompleted()
        do! obs.OnError (exn "late2")
    }
checkEq "combine: second completes-then-errors -> value + one completion"
    [ N 7; C ]
    (record 100 (asyncRx.Combine(AsyncObservable.empty, secondCompleteThenErr)))

// `second` throwing (not calling OnError) before/after a value must still surface
// as exactly one downstream error — the throw is caught and routed through the gate.
let secondThrowsImmediately : AsyncObservable<int> =
    fun _ _ -> job { raise (exn "second-throw") }

let secondEmitsThenThrows : AsyncObservable<int> =
    fun _ obs -> job {
        do! obs.OnNext 7
        raise (exn "second-after-value")
    }

let firstThrows : AsyncObservable<unit> =
    fun _ _ -> job { raise (exn "first-throw") }

checkEq "combine: second throws before terminal -> one error"
    [ E "second-throw" ]
    (record 100 (asyncRx.Combine(AsyncObservable.empty, secondThrowsImmediately)))

checkEq "combine: second emits then throws -> value + one error"
    [ N 7; E "second-after-value" ]
    (record 100 (asyncRx.Combine(AsyncObservable.empty, secondEmitsThenThrows)))

checkEq "combine: first throws -> one error"
    [ E "first-throw" ]
    (record 100 (asyncRx.Combine(firstThrows, value 5)))

// ---- P1-3: For enumerator lifecycle --------------------------------------

open System
open System.Collections.Generic

/// Enumerator that can fail on a chosen MoveNext or on Dispose, and records
/// whether it was disposed. `Current` is 1-based (the count of MoveNexts).
type ProbeEnumerator(count: int, throwMoveNextAt: int option, throwDispose: bool, disposed: bool ref) =
    let mutable i = 0
    interface IEnumerator<int> with
        member _.Current = i
    interface System.Collections.IEnumerator with
        member _.Current = box i
        member _.MoveNext() =
            i <- i + 1
            match throwMoveNextAt with
            | Some k when i = k -> raise (exn "move-next")
            | _ -> i <= count
        member _.Reset() = ()
    interface IDisposable with
        member _.Dispose() =
            disposed.Value <- true
            if throwDispose then raise (exn "dispose")

let probeSeq count throwMoveNextAt throwDispose (disposed: bool ref) : seq<int> =
    { new IEnumerable<int> with
        member _.GetEnumerator() : IEnumerator<int> =
            new ProbeEnumerator(count, throwMoveNextAt, throwDispose, disposed) :> IEnumerator<int>
      interface System.Collections.IEnumerable with
        member _.GetEnumerator() : System.Collections.IEnumerator =
            new ProbeEnumerator(count, throwMoveNextAt, throwDispose, disposed) :> System.Collections.IEnumerator }

let getEnumeratorThrows : seq<int> =
    { new IEnumerable<int> with
        member _.GetEnumerator() : IEnumerator<int> = raise (exn "get-enum")
      interface System.Collections.IEnumerable with
        member _.GetEnumerator() : System.Collections.IEnumerator = raise (exn "get-enum") }

let noop (_: int) = effect (fun () -> ())

checkEq "for: GetEnumerator failure -> one error, does not escape"
    [ E "get-enum" ]
    (record 100 (asyncRx.For(getEnumeratorThrows, noop)))

let mnDisposed = ref false
let mnEvents = record 100 (asyncRx.For(probeSeq 5 (Some 2) false mnDisposed, noop))
checkEq "for: MoveNext failure -> one error, no completion" [ E "move-next" ] mnEvents
check "for: enumerator disposed after MoveNext failure" mnDisposed.Value

let dpDisposed = ref false
let dpEvents = record 100 (asyncRx.For(probeSeq 2 None true dpDisposed, noop))
checkEq "for: normal-path Dispose failure -> one error, no completion" [ E "dispose" ] dpEvents
check "for: enumerator disposed (attempted) on Dispose failure" dpDisposed.Value

let okDisposed = ref false
let okEvents = record 100 (asyncRx.For(probeSeq 3 None false okDisposed, noop))
checkEq "for: normal completion -> single completion" [ C ] okEvents
check "for: enumerator disposed on normal completion" okDisposed.Value

let bodyErrDisposed = ref false
let bodyErrEvents =
    record 100 (asyncRx.For(probeSeq 3 None false bodyErrDisposed,
                            fun x -> if x = 2 then fail (exn "body2") else effect (fun () -> ())))
checkEq "for: body error -> one error, no completion (probe)" [ E "body2" ] bodyErrEvents
check "for: enumerator disposed on body error" bodyErrDisposed.Value

let stopDisposed = ref false
let stopForEvents =
    record 50 (asyncRx.For(probeSeq 3 None false stopDisposed, fun _ -> AsyncObservable.never))
check "for: stop during body -> no terminal" (terminalCount stopForEvents = 0)
check "for: enumerator disposed on stop" stopDisposed.Value

// ---- P0-4: Stop terminates cleanly (no terminal, no hang) ----------------

check "interval: stop -> no terminal"
    (terminalCount (record 60 (AsyncObservable.intervalMillis 15)) = 0)

check "bind: stop during active inner -> no terminal"
    (terminalCount (record 60 (value 0 |> AsyncObservable.bind (fun _ -> AsyncObservable.intervalMillis 15))) = 0)

check "merge: stop -> no terminal"
    (terminalCount (record 60 (AsyncObservable.merge [ AsyncObservable.intervalMillis 15; AsyncObservable.intervalMillis 18 ])) = 0)

check "zip: stop -> no terminal"
    (terminalCount (record 60 (AsyncObservable.zip (AsyncObservable.intervalMillis 15) (AsyncObservable.intervalMillis 18))) = 0)

// Codex non-blocking follow-up: stopping an active synchronous `ofSeq` must
// dispose its enumerator, emit no terminal, and fire `Subscription.Completion`.
// A suspending `mapJob` paces the source so Stop lands mid-iteration.
let stopOfSeqDisposed = ref false
let stopOfSeqEvents = ResizeArray<Ev<int>>()
let stopOfSeqCompletionFired =
    // A suspending per-element job paces the source so Stop lands mid-iteration.
    let pace x = job {
        do! timeOutMillis 5
        return x
    }
    Hopac.run <| job {
        let source =
            AsyncObservable.ofSeq (probeSeq 1000 None false stopOfSeqDisposed)
            |> AsyncObservable.mapJob pace
        let! sub =
            subscribeJob source
                (fun x -> job { stopOfSeqEvents.Add(N x) })
                (fun e -> job { stopOfSeqEvents.Add(E e.Message) })
                (fun () -> job { stopOfSeqEvents.Add C })
        do! timeOutMillis 40
        do! sub.Stop()
        // Bounded so a missing Completion fails visibly rather than hanging.
        let awaitCompletion =
            EventChoice.choose [
                EventChoice.map (fun () -> true) sub.Completion
                EventChoice.afterMillis 1000 false
            ]
        return! awaitCompletion
    }
check "ofSeq: stop during active iteration -> enumerator disposed" stopOfSeqDisposed.Value
check "ofSeq: stop during active iteration -> no terminal"
    (terminalCount (List.ofSeq stopOfSeqEvents) = 0)
check "ofSeq: stop during active iteration -> Subscription.Completion fires"
    stopOfSeqCompletionFired

// ---- P2-6 / P2-7: completion signal + rewritten ofSeq lifecycle ----------

checkEq "completion: finite source signals deterministic teardown"
    [ N 1; N 2; N 3; C ]
    (recordUntilDone (ofSeq [ 1; 2; 3 ]))

checkEq "completion: empty source completes deterministically"
    [ C ]
    (recordUntilDone AsyncObservable.empty)

checkEq "completion: error source signals completion after the error"
    [ E "boom" ]
    (recordUntilDone (fail (exn "boom")))

// ofSeq enumerator lifecycle (mirrors For): acquisition / MoveNext / Dispose
// failures each become exactly one error after any already-emitted values, and
// the enumerator is always disposed.
checkEq "ofSeq: GetEnumerator failure -> one error"
    [ E "get-enum" ]
    (recordUntilDone (AsyncObservable.ofSeq getEnumeratorThrows))

let ofSeqMnDisposed = ref false
checkEq "ofSeq: MoveNext failure -> emitted value then one error"
    [ N 1; E "move-next" ]
    (recordUntilDone (AsyncObservable.ofSeq (probeSeq 5 (Some 2) false ofSeqMnDisposed)))
check "ofSeq: enumerator disposed after MoveNext failure" ofSeqMnDisposed.Value

let ofSeqDpDisposed = ref false
checkEq "ofSeq: normal-path Dispose failure -> values then one error, no completion"
    [ N 1; N 2; E "dispose" ]
    (recordUntilDone (AsyncObservable.ofSeq (probeSeq 2 None true ofSeqDpDisposed)))
check "ofSeq: enumerator disposed (attempted) on Dispose failure" ofSeqDpDisposed.Value

let ofSeqOkDisposed = ref false
checkEq "ofSeq: normal completion -> values then single completion"
    [ N 1; N 2; N 3; C ]
    (recordUntilDone (AsyncObservable.ofSeq (probeSeq 3 None false ofSeqOkDisposed)))
check "ofSeq: enumerator disposed on normal completion" ofSeqOkDisposed.Value

// ofSeq stack-safety: a large synchronous sequence must not overflow the stack
// (the old `return! loop ()` did, ~75-95k). Driven to its terminal via runJob.
let mutable ofSeqCount = 0
let bigOfSeqEvents =
    recordToEnd (AsyncObservable.ofSeq (seq { 1 .. bigN }) |> AsyncObservable.map (fun x -> ofSeqCount <- ofSeqCount + 1; x))
check "ofSeq: 100k synchronous elements do not overflow the stack"
    (ofSeqCount = bigN && List.last bigOfSeqEvents = C)

// ---- clause selection: AsyncObservable.firstValue (matching, not first-to-notify) --

// A non-matching clause (choose -> None on a finite source) completes without
// emitting. Under plain `amb` that completion could win the selection race and
// yield an empty result; `AsyncObservable.firstValue` must discard empty-completers so a
// slower clause that actually produces a value is selected instead.
let nonMatch : AsyncObservable<string> =
    value 1 |> AsyncObservable.choose (fun (_: int) -> (None: string option))

let slowMatch (label: string) (ms: int) : AsyncObservable<string> =
    AsyncObservable.intervalMillis ms
    |> AsyncObservable.map (fun _ -> label)
    |> AsyncObservable.first

checkEq "firstValue: empty-completer does not preempt a slower match"
    [ N "b"; C ]
    (record 200 (AsyncObservable.firstValue [ nonMatch; slowMatch "b" 20 ]))

checkEq "firstValue: all clauses empty -> single completion"
    [ C ]
    (record 100 (AsyncObservable.firstValue [ nonMatch; nonMatch ]))

checkEq "firstValue: faster value wins, slower clause cancelled"
    [ N "fast"; C ]
    (record 200 (AsyncObservable.firstValue [ slowMatch "fast" 20; slowMatch "slow" 300 ]))

checkEq "firstValue: error wins the race"
    [ E "boom" ]
    (record 200 (AsyncObservable.firstValue [ (fail (exn "boom") : AsyncObservable<string>); slowMatch "b" 50 ]))

// End-to-end through the clauses { } builder (Run = AsyncObservable.firstValue).
checkEq "clauses: non-matching clause does not preempt a slower match"
    [ N "b"; C ]
    (record 200 (clauses {
        yield nonMatch
        yield slowMatch "b" 20
    }))

checkEq "clauses: matching value selected then completes"
    [ N "a"; C ]
    (record 200 (clauses {
        yield slowMatch "a" 20
        yield slowMatch "b" 300
    }))

checkEq "clauses: whenValue skips non-matches and selects first matching value"
    [ N "b was true"; C ]
    (record 200 (clauses {
        whenValue (value false) ((=) true) "a was true"
        whenValue (value true) ((=) true) "b was true"
    }))

checkEq "clauses: case custom operation keeps chooser semantics"
    [ N "tick:7"; C ]
    (record 200 (clauses {
        case (value 7) (fun n -> if n > 0 then Some (sprintf "tick:%d" n) else None)
    }))

checkEq "clauses: guardOn false completes empty and does not preempt always"
    [ N "unguarded"; C ]
    (record 200 (clauses {
        guardOn false "guard"
        always (slowMatch "unguarded" 20)
    }))

checkEq "clauses: yield! compatibility remains"
    [ N "spliced"; C ]
    (record 200 (clauses {
        yield! [ nonMatch; slowMatch "spliced" 20 ]
    }))

// ---- summary --------------------------------------------------------------

printfn "\n%s" (String.replicate 50 "-")
if failures = 0 then printfn "ALL TESTS PASSED"
else printfn "%d TEST(S) FAILED" failures

exit failures
