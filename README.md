# FlowLattice

FlowLattice is a home for two F# libraries about long-lived computation:

- **AsyncRxHopac** composes values that arrive over time, with asynchronous
  acknowledgement and backpressure.
- **Propagator** combines partial information from multiple sources until a
  network reaches a fixpoint or contradiction.

They address different dimensions of an incremental system. AsyncRx answers
*when did something arrive, and how should its processing be paced?* Propagator
answers *given everything currently known, what else follows?*

The libraries are independent. Use either one on its own, or connect them at an
application boundary: an AsyncRx stream can introduce and retract facts in a
propagator network, and network observations can be published as another
stream.

## Libraries

### AsyncRxHopac

[AsyncRxHopac](AsyncRx/README.md) is an asynchronous push-stream library built
on Hopac. An `AsyncObservable<'T>` sends serialized notifications to a consumer,
and asynchronous handlers can acknowledge each value before the producer
continues. That makes backpressure part of the stream contract rather than an
application convention.

It includes:

- finite, timed, callback, and asynchronous stream sources;
- transformation, filtering, merging, zipping, and racing operators;
- cooperative cancellation and deterministic subscription completion;
- `Task` and `FSharp.Control.AsyncSeq` bridges;
- `asyncRx` and conditional `clauses` computation expressions.

Start with the [AsyncRxHopac README](AsyncRx/README.md) for examples and the
full stream contract.

### Propagator

[Propagator](Propagator/README.md) implements propagator networks in pure F#.
Cells accumulate partial information; propagators contribute deductions; lawful
information merges narrow the network until no rule can add anything new.

It includes:

- an ergonomic `Network` facade and function-shaped `Ops` dialect;
- portable finite relations with deterministic solving and seeded generation;
- an optimized runtime-width bitset backend for finite domains;
- a general engine for finite representations and rich information lattices;
- scalar, outward-rounded interval, and fixed-point value domains;
- named assumptions, provenance, retraction, observation, and bounded
  propagation.

Start with the [Propagator README](Propagator/README.md) for the mathematical
model, its relationship to constraint propagation and reactive programming,
and runnable examples.

## How they fit together

Consider a controller receiving measurements from several devices:

```text
device callbacks
      |
      v
 AsyncRxHopac          arrival, ordering, pacing, cancellation
      |
      v
 Propagator            partial knowledge, deduction, contradiction, retraction
      |
      v
 AsyncRxHopac          observed conclusions delivered to application services
```

AsyncRx is responsible for the temporal protocol. It decides how concurrent
sources are combined, how slowly a consumer may run, and when a subscription
ends. The propagator network is responsible for meaning. It combines the facts
currently in force, derives consequences through every applicable rule, and
tracks which assumptions support those consequences.

Neither library absorbs the other's job. Propagator observation is deliberately
synchronous and runtime-neutral, so an application can choose AsyncRx, a
mailbox, an event loop, or another boundary. AsyncRx does not impose a knowledge
algebra on stream values; it carries whatever facts the application chooses to
send.

## Build and test

Requirements: .NET 8 SDK.

Build both libraries:

```powershell
dotnet build .\AsyncRx\AsyncRxHopac.fsproj
dotnet build .\Propagator\Propagator.fsproj
```

Run the AsyncRx compatibility checks and examples:

```powershell
dotnet run --project .\AsyncRx\Tests\AsyncRxHopac.Tests.fsproj
dotnet run --project .\AsyncRx\Examples\AsyncRxHopac.Examples.fsproj
```

Run the complete Propagator regression suite:

```powershell
dotnet fsi .\Propagator.Tests\run.fsx
```

The Propagator numeric suite restores `MathNet.Numerics.FSharp` for its external
`BigRational` compatibility check. The library itself remains pure F# and has no
package dependency.

## Referencing the projects

From another project in this repository:

```xml
<ItemGroup>
  <ProjectReference Include="..\AsyncRx\AsyncRxHopac.fsproj" />
  <ProjectReference Include="..\Propagator\Propagator.fsproj" />
</ItemGroup>
```

Reference only the project the application actually uses.

## Repository layout

```text
FlowLattice/
  AsyncRx/
    AsyncRxHopac.fsproj       # asynchronous push-stream library
    README.md                 # stream guide and examples
    Tests/                    # executable compatibility checks
    Examples/                 # interactive demonstrations

  Propagator/
    Propagator.fsproj         # propagator and constraint library
    Core.fs                   # vocabulary and information domains
    General.fs                # general finite and lattice engine
    Optimized.fs              # optimized finite-domain engine
    Network.fs                # ergonomic facade
    README.md                 # conceptual and API guide

  Propagator.Tests/
    run.fsx                   # aggregate source-loaded test runner
    propagator-friendly.tests.fsx
    propagator-number-types.tests.fsx

  docs/                       # design history and work orders
  *.fsx                       # historical experiments and executable records
```

The root scripts record the repository's development and remain useful as
executable design evidence. New consumers should reference the compiled
projects and begin with the library READMEs rather than loading those historical
stages as application code.
