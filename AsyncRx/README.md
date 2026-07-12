# AsyncRxHopac

`AsyncRxHopac` is a library for programs where values arrive over time: sensor
readings, socket messages, queue items, file changes, timers, or work emitted
by several concurrent services. It gives those values a single composable F#
shape, `AsyncObservable<'T>`.

The important part is not merely that the source is asynchronous. It is that
the consumer can acknowledge each value asynchronously before the producer is
allowed to continue. A slow database write, HTTP request, or disk operation can
therefore pace an upstream feed instead of letting the feed silently build an
unbounded queue of work.

It is built on [Hopac](https://github.com/Hopac/Hopac), while keeping ordinary
stream construction and consumption behind the `AsyncObservable` and `AsyncRx`
APIs. It also bridges to `Task` and `FSharp.Control.AsyncSeq`, and includes two
computation expressions for composing streams and racing conditional branches.

## Why this instead of a list, `Task`, or `seq`?

Those tools solve different shapes of problem:

| If the work is... | A natural fit is... | Use AsyncRxHopac when... |
| --- | --- | --- |
| One result that will arrive later | `Task<'T>` or `Async<'T>` | the operation produces a continuing stream of results. |
| A collection already in memory | `list`, `array`, or `seq<'T>` | values are produced independently and arrive while the program is running. |
| Pull-driven iteration | `seq<'T>` or `AsyncSeq<'T>` | the producer must push, or several producers need to be merged naturally. |
| A callback/event with no acknowledgement | an event handler | each item needs asynchronous work, cancellation, ordering, and a clear lifetime. |

Use AsyncRxHopac when you need to combine live inputs, process every value in
order, and let processing capacity regulate production. It is especially handy
for integration pipelines, device or telemetry feeds, concurrent background
work, and systems that cross between push-based events and pull-based
`AsyncSeq` policies.

For a fixed batch of data, or one eventual answer, a collection or `Task` is
usually simpler. AsyncRxHopac earns its place when *the stream and its
lifetime* are part of the problem.

## Quick start

The project currently lives in this repository, so reference it from an F# app:

```xml
<ItemGroup>
  <ProjectReference Include="..\AsyncRx\AsyncRxHopac.fsproj" />
</ItemGroup>
```

Then create and consume a small finite stream:

```fsharp
open AsyncRxHopac

let numbers =
    AsyncObservable.ofSeq [ 1; 2; 3; 4; 5 ]
    |> AsyncObservable.map (fun n -> n * n)
    |> AsyncObservable.filter (fun n -> n > 10)

numbers
|> AsyncRx.run
    (fun n -> printfn "next: %d" n)
    (fun error -> printfn "error: %s" error.Message)
    (fun () -> printfn "done")
```

Output:

```text
next: 16
next: 25
done
```

`AsyncRx.run` is the simplest blocking consumer. It runs a source to its
terminal notification with ordinary F# functions for the handlers; application
code does not need to start the underlying runtime itself.

## The stream contract

An `AsyncObservable<'T>` is a source that pushes values to an observer. For
each subscription it guarantees:

- callbacks are serialized: `OnNext`, `OnError`, and `OnCompleted` never run
  concurrently;
- a source emits zero or more values, followed by at most one terminal event;
- `Job<unit>` callback handlers can apply backpressure: the next value waits
  until the current handler finishes;
- subscriptions can be stopped cooperatively and expose a completion event for
  deterministic teardown.

Most applications only need the `AsyncObservable` operators to make streams
and `AsyncRx` to consume them. The synchronous handlers accepted by
`AsyncRx.run` are intentionally immediate. Use `subscribeJob` or `runJob` when
the consumer's work must pace the producer.

## Common operations

### Transform a source

```fsharp
open AsyncRxHopac

let labels =
    AsyncObservable.ofSeq [ 1 .. 10 ]
    |> AsyncObservable.filter (fun n -> n % 2 = 0)
    |> AsyncObservable.map (sprintf "even:%d")
    |> AsyncObservable.take 3

AsyncRx.runBlocking labels (printfn "%s")
```

Use `map` when the transformation is immediate. Use `mapJob` when transforming
one value needs asynchronous work, such as reading a record or writing to a
service: it waits for that `Job` to finish, then emits the transformed value.
`choose` combines filtering and mapping through an option-returning function.

### Combine independent sources

`merge` forwards values from all its sources as they arrive. `zip` pairs values
by position, and `amb` selects the source that produces the first notification.

```fsharp
open AsyncRxHopac

let left =
    AsyncObservable.intervalMillis 50
    |> AsyncObservable.map (fun n -> $"left:{n}")
    |> AsyncObservable.take 3

let right =
    AsyncObservable.intervalMillis 80
    |> AsyncObservable.map (fun n -> $"right:{n}")
    |> AsyncObservable.take 2

AsyncObservable.merge [ left; right ]
|> AsyncRx.run
    (printfn "%s")
    (fun error -> printfn "error: %s" error.Message)
    (fun () -> printfn "merged stream finished")
```

### Compose values with `asyncRx`

After `open AsyncRxHopac`, the `asyncRx` computation expression is available.
`let!` consumes a value from a source; `and!` uses the applicative product to
combine two sources.

```fsharp
open AsyncRxHopac

let sum =
    asyncRx {
        let! x = AsyncObservable.singleton 10
        and! y = AsyncObservable.singleton 32
        return x + y
    }

AsyncRx.runBlocking sum (printfn "sum = %d")
```

The builder also supports `for`, `while`, `do!`, and normal `return` syntax.

### Stop a long-running subscription

For asynchronous handlers or explicit lifetime control, use `subscribeJob`.
The handler below takes 100 ms per item, so it deliberately slows a source that
would otherwise tick every 10 ms.

```fsharp
open Hopac
open AsyncRxHopac

AsyncRx.runJobBlocking (
    job {
        let source = AsyncObservable.intervalMillis 10

        let! subscription =
            AsyncRx.subscribeJob source
                (fun n -> job {
                    printfn "processing %d" n
                    do! timeOutMillis 100
                })
                (fun error -> job { printfn "error: %s" error.Message })
                (fun () -> job { printfn "completed" })

        do! timeOutMillis 500
        do! subscription.Stop()
        do! subscription.Completion
    })
```

`Stop` is idempotent. Awaiting `Completion` is preferable to sleeping or
polling when a program needs to know that teardown has finished.

## AsyncSeq interop

Use `toAsyncSeq` when a pull-based consumer should determine the pace, and
`ofAsyncSeq` to return to an AsyncRx source:

```fsharp
open FSharp.Control
open AsyncRxHopac

let controller (readings: AsyncSeq<float>) : AsyncSeq<string> =
    asyncSeq {
        for temperature in AsyncSeq.take 3 readings do
            do! Async.Sleep 100
            yield if temperature > 20.0 then "cool" else "heat"
    }

let commands =
    AsyncObservable.ofSeq [ 18.5; 22.0; 19.5; 23.0 ]
    |> AsyncObservable.toAsyncSeq
    |> controller
    |> AsyncObservable.ofAsyncSeq

commands
|> AsyncRx.run
    (printfn "command: %s")
    (fun error -> printfn "error: %s" error.Message)
    (fun () -> printfn "controller finished")
```

The pull side's demand propagates through `toAsyncSeq`, allowing an
`AsyncSeq` consumer to pace the AsyncRx producer.

## Conditional first-value choice

The `clauses` expression races branches and returns the first branch that
actually emits a value. It is a race, not ordered fallback: write conditions
that make non-matching branches produce no value.

```fsharp
open AsyncRxHopac

let a = AsyncObservable.singleton false
let b = AsyncObservable.singleton true

let message =
    clauses {
        whenValue a ((=) true) "a was true"
        whenValue b ((=) true) "b was true"
    }

AsyncRx.runBlocking message (printfn "%s")
```

`always` can add an unguarded branch, but it participates in that same race;
it is not an ordered `else` or fallback branch.

## Build and run the included checks

Requirements: .NET 8 SDK. Package restore brings in Hopac and
`FSharp.Control.AsyncSeq` automatically.

```powershell
dotnet build AsyncRx/AsyncRxHopac.fsproj
dotnet run --project AsyncRx/Tests/AsyncRxHopac.Tests.fsproj
dotnet run --project AsyncRx/Examples/AsyncRxHopac.Examples.fsproj
```

The test project runs the kernel and AsyncSeq-interop suites. The example
project pauses before each individual demonstration, making it easy to inspect
one trace at a time.

## Project layout

```text
AsyncRx/
  AsyncRxHopac.fsproj             # library (net8.0)
  Core.fs                         # observable, observer, subscription types
  EventChoice.fs                  # event-choice and cancellation helpers
  AsyncRx.fs                      # subscribing and consuming streams
  AsyncObservable.fs              # sources, operators, bridges, builders
  Tests/AsyncRxHopac.Tests.fsproj # executable compatibility checks
  Examples/AsyncRxHopac.Examples.fsproj
                                   # executable, interactive demonstrations
```

For a more involved push/pull example, run the thermostat demonstration from
the examples project. It combines two push-driven sensor streams with a
pull-driven `AsyncSeq` controller and shows the feedback loop's pacing and
shutdown in the console trace.
