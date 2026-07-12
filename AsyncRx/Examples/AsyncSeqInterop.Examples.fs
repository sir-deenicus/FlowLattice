module AsyncSeqInteropExamples

(*
    AsyncSeqInterop.Examples.fs

    A small demo of two paradigm-native systems coupled into one feedback loop,
    each a genuine driver, talking through the AsyncRx <-> AsyncSeq bridge:

      R  - sensor feed   (push / AsyncRx-native): TWO sensors on independent
                          clocks, merged into one stream. Merging independent
                          producers is a one-liner in push and awkward in pull;
                          this is what makes R push-native rather than incidental.
                          R initiates by emitting.
      P  - controller    (pull / AsyncSeq-native): an asyncSeq transform
                          AsyncSeq<Reading> -> AsyncSeq<Command> that pulls on its
                          own (slower) schedule and yields commands. P initiates a
                          command, and P's policy decides when to stop.

    The loop is de-cycled: commands are lifecycle/control only, they never gate R's
    value production, so the build order is acyclic. One policy decision (P stops
    pulling) unwinds the whole loop:

        P stops pulling (take N done)  ->  readings enumerator disposed
        ->  toAsyncSeq cancels its token  ->  R's feed stops
        ->  P yields a final Halt  ->  ofAsyncSeq delivers it, then completes.

    What it shows in one run: both bridges exercised; both initiation directions;
    backpressure on both edges (P's slow pull throttles BOTH merged sensors below
    their own clocks, through merge's single serialized channel); and a clean
    cross-link shutdown.

    Run:  dotnet run --project AsyncRx/Tests/AsyncSeqInterop.Examples/AsyncSeqInterop.Examples.fsproj
*)


open FSharp.Control
open AsyncRxHopac                  // module prefixes for qualified calls: AsyncObservable, AsyncRx
open AsyncRxHopac.Core             // the AsyncObservable type, for annotations

// A monotonic wall-clock stamp so the trace makes the pacing visible: R wants to
// tick every 50ms but its "producing" lines land at P's ~150ms pull cadence.
let private sw = System.Diagnostics.Stopwatch.StartNew()
let private stamp () = sprintf "%6dms" sw.ElapsedMilliseconds

// R (Hopac) and P (Async) are genuinely concurrent drivers on different
// schedulers, so their console writes can interleave mid-line. Funnel every trace
// line through one lock: this serializes the *observation* only -- the timestamps
// still report the true concurrency and pacing.
let private logLock = obj ()
let private logf fmt =
    Printf.kprintf (fun s -> lock logLock (fun () -> System.Console.WriteLine s)) fmt

let setPoint = 20.0

type Command =
    | Heat
    | Cool
    | Halt

// R - sensor feed (push / AsyncRx-native). Each sensor is a free-running clock
// that only *computes* a wandering temperature, tagged with its name. `merge`
// fans the two independent clocks into one stream: a push-native one-liner, where
// the pull equivalent means hand-interleaving two enumerators. The single log /
// handoff point sits *downstream* of merge, so it runs on merge's one serialized
// consumer loop (the A8 source contract) instead of racing across the two
// producer jobs -- and because that loop is what P pulls through, P's slow pull
// throttles BOTH sensors. (Logging inside each sensor's own map would put the
// effect upstream of the funnel, where the two writes interleave.)
let private sensor (name: string) (periodMs: int) (phase: float) : AsyncObservable<string * float> =
    AsyncObservable.intervalMillis periodMs
    |> AsyncObservable.map (fun i -> name, setPoint + 4.0 * sin (float i / 2.0 + phase))

let sensorFeed () : AsyncObservable<float> =
    AsyncObservable.merge [ sensor "A" 50 0.0; sensor "B" 70 1.5 ]
    |> AsyncObservable.map (fun (name, temp) ->
        logf "[%s][R:%s] producing reading = %5.2f" (stamp ()) name temp
        temp)

// P - controller (pull / AsyncSeq-native). An honest policy loop with its own
// decision logic: pull (deliberately slower than R's clock, to make backpressure
// visible), and on its own policy emit a command. `take` bounds the run; when the
// loop ends the underlying readings enumerator is disposed, which is what stops R.
let controller (readings: AsyncSeq<float>) : AsyncSeq<Command> =
    asyncSeq {
        let mutable n = 0
        for temp in AsyncSeq.take 6 readings do
            n <- n + 1
            logf "[%s][P] pulled    reading #%d = %5.2f (processing...)" (stamp ()) n temp
            do! Async.Sleep 150                      // slow pull => R is throttled to this pace
            if temp > setPoint + 1.5 then
                logf "[%s][P]   policy: too hot  -> Cool" (stamp ())
                yield Cool
            elif temp < setPoint - 1.5 then
                logf "[%s][P]   policy: too cold -> Heat" (stamp ())
                yield Heat
            else
                logf "[%s][P]   policy: in band  -> (no command)" (stamp ())
        logf "[%s][P]   policy: enough samples -> Halt" (stamp ())
        yield Halt
    }

let thermostatDemo () =
    printfn "=== thermostat feedback loop: push sensor feed (R) <-> pull controller (P) ==="
    printfn "    R = two sensors @ 50ms + 70ms, merged; P pulls every ~150ms => backpressure throttles both to P."
    printfn ""

    // The whole loop reads as one left-to-right dataflow, entirely in AsyncRx /
    // AsyncSeq vocabulary -- no `Hopac.run`, no `job { }`, no Hopac import at all:
    //   push source -> (to pull) -> controller -> (back to push) -> run.
    let commands =
        sensorFeed ()                          // R  (push: two merged sensors)
        |> AsyncObservable.toAsyncSeq          // R -> P   (R-initiated, P-paced)
        |> controller                          // P's policy loop, yields commands
        |> AsyncObservable.ofAsyncSeq          // P -> R   (P-initiated)

    // `AsyncRx.run` consumes to the terminal and blocks until teardown -- that
    // return is the deterministic teardown. The sink drives nothing; the pull side
    // does (when P stops pulling), so no Stop handle is needed.
    commands
    |> AsyncRx.run
        (fun cmd -> logf "[%s][R] <- applying command: %A" (stamp ()) cmd)
        (fun e   -> logf "[%s][R] ERROR: %s" (stamp ()) e.Message)
        (fun ()  -> logf "[%s][R] command feed completed; loop torn down" (stamp ()))

    printfn ""
    printfn "=== demo complete ==="
