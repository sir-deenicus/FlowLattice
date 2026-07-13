# Propagator

`Propagator` is a pure F# library for programs where several facts and rules
jointly determine what values remain possible. Cells hold partial information,
constraints move information between cells, and every update narrows knowledge
until the network reaches a fixpoint or a contradiction.

The important part is not merely solving a set of equations. A propagator
network retains its cells and dependencies, so an application can add an
assumption, inspect its consequences and support, retract it, and continue from
the restored state. The same machinery supports finite constraint problems,
rich information lattices, deterministic search, seeded generation, and live
observation.

Most programs can use the ergonomic `Propagator.Network` facade. Finite domains
delegate to the optimized bitset engine; scalar, interval, and fixed-point
domains delegate to the general lattice engine. Applications that need explicit
models, backend selection, bounded propagation, or structural provenance can
use `Propagator.Core`, `Propagator.General`, and `Propagator.Optimized` directly.

## What is a propagator?

This library follows the propagator model developed by Gerald Jay Sussman and
his doctoral student Alexey Radul. Their starting question was broader than
constraint solving: why are programs so difficult to extend when a new source
of knowledge or a second way of solving a problem appears?

A conventional program usually commits early to a strategy. One expression is
chosen to produce a value, one direction is chosen for a calculation, and one
subsystem becomes authoritative. Adding another method later often means
reorganizing the original control flow. Sussman and Radul wanted a model in
which several partial, redundant, or even competing methods could coexist and
be added without rewriting the methods already present.

Their answer was a network of two kinds of component:

- A **cell** accumulates information about one subject.
- A **propagator** watches some cells and contributes deductions to others.

A propagator is therefore not a value moving through a pipeline. It is a small,
autonomous rule that remains attached to the network. Whenever its inputs become
more informative, it gets another chance to say what follows. A cell may have
many writers because no writer replaces the cell's content; the cell merges all
compatible contributions into a more informative result.

For example, a temperature network can contain both of these rules at once:

```text
Celsius    -- c * 9/5 + 32 --> Fahrenheit
Celsius <-- (f - 32) * 5/9 --    Fahrenheit
```

Neither direction is the program's permanent "real" direction. Supplying a
Celsius fact informs Fahrenheit; supplying Fahrenheit informs Celsius. If
independent measurements arrive on both sides, the cells combine them and can
detect disagreement. A third conversion or measuring method can be attached
later without changing either existing rule.

This additive structure was the central motivation of Sussman and Radul's
[propagator model](https://groups.csail.mit.edu/mac/users/gjs/propagators/):
preserve multiple viewpoints and strategies in the running system instead of
collapsing them into one control plan. The model is developed in
[The Art of the Propagator](https://hdl.handle.net/1721.1/44215) and Radul's
[Propagation Networks](https://hdl.handle.net/1721.1/49525) dissertation.

## The mathematics: partial information

The central mathematical object is a meet-semilattice of information. A cell
does not necessarily hold one finished value. It holds the best summary of
everything currently known about that value.

This library names three parts of that structure:

- `top` means no useful information yet;
- `meet` combines two contributions into one result that satisfies both;
- `bottom` means the contributions are contradictory.

Writing meet as `x /\ y`, a valid information merge is:

```text
x /\ y = y /\ x                 commutative
(x /\ y) /\ z = x /\ (y /\ z)  associative
x /\ x = x                      idempotent
```

These laws mean that a cell's answer does not depend on which compatible
contribution happened to arrive first, how contributions were grouped, or
whether the same information was discovered twice.

A finite domain makes the idea concrete. If a cell may contain `1`, `2`, or
`3`, then its information states are sets of remaining candidates:

```text
top              {1, 2, 3}   nothing ruled out
more information {1, 3}      2 ruled out
solved            {3}         one value remains
bottom             {}         no value is possible

meet = set intersection
```

Intervals use the same shape over continuous values. The whole number line is
`top`, intersection is `meet`, a point interval is exact knowledge, and the
empty interval is `bottom`. `Scalar<'T>` is the smallest equality lattice:
`Top` is unknown, `Val x` is exact knowledge, and two unequal known values meet
at `Bot`. `FixedPoint` is an ergonomic decimal view over the interval lattice,
not another propagation engine.

Each propagator must be monotone with respect to this information order: during
ordinary propagation it may add information, but it may not silently forget
what is already known. The engine repeatedly runs affected propagators and
meets their contributions into target cells. When no contribution changes any
cell, the network is at a **fixpoint**. There is no distinguished "last rule";
the answer is the quiescent state of the whole connected network.

Retraction is explicit rather than a violation of monotonicity. Contributions
carry premises. Retracting a premise removes the contributions that depend on
it, permits affected cells to become less informative, and then computes a new
fixpoint from the surviving information.

## How this differs from nearby models

### State machines

A state machine gives one current state to a transition function. An event
selects a transition, and the old state is replaced by a new state. Sequence is
fundamental: applying event `A` and then `B` may differ from applying `B` and
then `A`.

A propagator network has stateful cells, but it is not organized around one
transition function or an event history. Rules are local and remain installed.
They respond whenever relevant information changes, and their contributions
are combined by the cell's algebra. For monotone propagation, scheduling order
does not choose the meaning of the result; the network settles to a fixpoint.
Premises and retraction describe revisions directly instead of encoding every
revision as another state-machine transition.

State machines remain the natural model for protocols and workflows where the
order of actions is itself the subject. Propagators are a natural model when
the subject is a body of partial knowledge and the deductions it supports.

### Reactive programming

Reactive systems also connect long-lived components and recompute downstream
results when inputs change. The usual reactive value, however, is the latest
value in time: a new value replaces an old one, and dependencies normally have
an authored direction from source to sink.

A propagator cell instead accepts information from multiple directions and
multiple writers. Updates are merged rather than simply replaced, cycles are
ordinary when they refine knowledge, and computation continues until the
network reaches a fixpoint. Provenance can explain which premises support the
current information and can selectively retract their consequences.

The models compose well. This library exposes synchronous observation so an
application can publish cell changes through AsyncRx, a mailbox, an event loop,
or another reactive boundary. Reactive programming then manages change over
time; the propagator network manages what follows from the facts currently in
force.

### Constraint propagation

Constraint propagation is an important special case of the broader propagator
model. In a finite constraint problem, each cell holds a set of candidate
values and each relation removes candidates that have no supporting tuple. The
relations run repeatedly until generalized arc consistency reaches a fixpoint.

Search builds on that propagation rather than replacing it. A guess is asserted
under a temporary premise. If it produces `bottom`, retracting that premise
restores the surviving information and another candidate can be tried. Seeded
generation uses the same general mechanism with a different choice policy.

But propagators are not limited to finite constraint satisfaction. A cell can
carry any well-behaved partial-information structure: intervals, exact scalar
knowledge, application-defined lattices, or values supported by premises and
structural constraints. Constraint propagation is one particularly useful way
to instantiate cells and propagators, not the definition of the model itself.

## Quick start

The project currently lives in this repository, so reference it from an F# app:

```xml
<ItemGroup>
  <ProjectReference Include="..\Propagator\Propagator.fsproj" />
</ItemGroup>
```

Then create a two-value finite network:

```fsharp
open Propagator.Core
open Propagator.Network

let net = Domain.finite [ 1; 2 ]
let left = net.Cell "left"
let right = net.Cell "right"

net.Relate(
    [ left; right ],
    function
    | [ x; y ] -> x <> y
    | _ -> false)

net.Given(left, 1)

printfn "left  = %A" (net.Value left)
printfn "right = %A" (net.Value right)
```

Output:

```text
left  = set [1]
right = set [2]
```

`Domain.finite` preserves the authored value order and uses the optimized
backend. `Relate` accepts an ordinary predicate over a scope of cells; the
backend compiles and propagates the relation without exposing constraint
plumbing to the caller.

## How this library realizes the model

A `Domain` supplies the partial-information algebra carried by the cells. A
`Constraint` attaches one or more propagators that derive information. `Given`
adds permanent input, while `Assume` associates input with a named premise that
can later be retracted. Constraints do not own a second mutable model: the
friendly facade and lower-level APIs operate on the same live network state.

Finite relations are portable across both engines. Rich scalar, interval, and
fixed-point dataflow uses the general engine because those domains meet values
rather than enumerate candidates. Unsupported lowerings are returned as data
before a backend is constructed.

## Common operations

### Add and retract an assumption

Use `Assume` when a fact needs a stable name and may later be withdrawn:

```fsharp
open Propagator.Core
open Propagator.Network

let net = Domain.finite [ 1; 2 ]
let left = net.Cell "left"
let right = net.Cell "right"

net.Relate(
    [ left; right ],
    function
    | [ x; y ] -> x <> y
    | _ -> false)

net.Assume("chosen-left", left, 1)

printfn "%A" (net.Value right)                    // set [2]
printfn "%s" (net.ShowSupport(net.Support right)) // {chosen-left}

net.Retract "chosen-left"

printfn "%A" (net.Value right)                    // set [1; 2]
```

`Given` is convenient for permanent input. `Assume` is the better choice for
interactive facts, alternatives, and data with an explicit lifetime.

### Apply one relation to many scopes

`RelateMany` gives repeated topology one authored predicate. This is useful for
grids, graphs, schedules, and any model where the same relation appears at many
locations:

```fsharp
let net = Domain.finite [ 1; 2; 3 ]
let a = net.Cell "a"
let b = net.Cell "b"
let c = net.Cell "c"

net.RelateMany(
    [ [ a; b ]; [ b; c ] ],
    function
    | [ x; y ] -> x <> y
    | _ -> false)

net.Given(a, 1)
```

The scopes must have the same arity. Arbitrary finite relations remain
expressible with `Relate`; repeated binary relations receive the optimized
backend's compiled support representation automatically.

### Use rich numeric information

`Domain.interval` carries outward-rounded intervals, so floating-point residue
widens an enclosure instead of fabricating an equality contradiction.
`Domain.fixedPoint` presents the same interval semantics through decimal
operators and a caller-selected display quantum:

```fsharp
open Propagator.Core
open Propagator.Network

let net = Domain.fixedPoint 0.1m
let celsius = net.Cell "celsius"
let fahrenheit = net.Cell "fahrenheit"

net.Convert(
    celsius,
    fahrenheit,
    (fun c -> c * 9m / 5m + 32m),
    (fun f -> (f - 32m) * 5m / 9m))

net.Given(celsius, FixedPoint(1.0m))

printfn "C = %O" (net.Value celsius)
printfn "F = %O" (net.Value fahrenheit)
```

Output:

```text
C = 1
F = 33.8
```

`FixedPoint(value)` infers its quantum from the decimal's written scale.
`FixedPoint(value, quantum)` makes it explicit. Arithmetic between two fixed
values requires equal quanta; use `WithQuantum` when changing that modeling
choice intentionally. Decimal operands retain the fixed value's quantum.

`Domain.scalar<'T>` is the smaller exact-equality lattice: an unknown value is
`Top`, one known value is `Val value`, and disagreement is `Bot`. It is suitable
only when equality remains honest under every operation the constraints use.

### Solve or generate a finite network

`Solve` returns one deterministic solution. Candidate order is the finite
domain's authored order on both lowerings.

```fsharp
match net.Solve() with
| Some solution -> printfn "%A" solution
| None -> printfn "no solution"
```

`Generate` chooses finite candidates from a seed and retries contradictions:

```fsharp
match net.Generate(42UL, attempts = 16) with
| Ok solution -> printfn "%A" solution
| Error failure -> printfn "generation failed: %A" failure
```

Generation policy that belongs to an application, such as tiles, adjacency,
grid dimensions, or WFC-specific observation rules, stays in application code.
The library supplies the general finite relations, propagation, seeded choice,
and observation seams those policies use.

### Observe changes

Subscribe to one cell or the whole network:

```fsharp
use subscription =
    net.Observe(fun (cell, state) ->
        printfn "%s -> %A" cell.name state)
```

The callback receives the representation-independent `CellState`, including
widening caused by retraction or backtracking. `Observe` returns an
`IDisposable`, so it can be connected to an event loop, mailbox, AsyncRx source,
or another asynchronous boundary without putting an async runtime in the core.

## Function-shaped syntax

The object-shaped `Network` methods and the `Ops` computation-expression
dialect are two interfaces over the same semantics. Use whichever reads more
naturally at the call site:

```fsharp
open Propagator.Core
open Propagator.Network

let result =
    network (Domain.finite [ 1; 2 ]) {
        let! left = Ops.cell "left"
        let! right = Ops.cell "right"
        do! Ops.relate [ left; right ] (function
            | [ x; y ] -> x <> y
            | _ -> false)
        do! Ops.given left 1
        return! Ops.read right
    }

printfn "%A" result
```

The dialects do not create separate networks or duplicate propagation state.

## Lower-level APIs

Most applications should begin with `Propagator.Network`. The other modules
are available when the model needs a capability the facade intentionally does
not hide:

| Module | Role |
| --- | --- |
| `Propagator.Core` | Cells, domains, constraints, models, interval/fixed-point values, capability results, and shared vocabulary. |
| `Propagator.General` | Finite or rich lattice propagation, custom finite representations, live edits, retraction, bounded propagation, and structural provenance. |
| `Propagator.Optimized` | Runtime-width bitset propagation for finite domains, deterministic solving, seeded generation, and live finite edits. |
| `Propagator.Network` | Low-overhead authoring and inspection over one delegated General or Optimized network. |

A portable finite model can be authored once and lowered to either face:

```fsharp
open Propagator.Core

let left = Cell.create "left"
let right = Cell.create "right"

let model =
    { domain = Domain.finite [ 1; 2 ]
      cells = [ left; right ]
      constraints =
        [ Constraint.relation [ left; right ] (function
            | [ x; y ] -> x <> y
            | _ -> false) ]
      givens = [ left, 1 ] }

let general = Propagator.General.lower model
let optimized = Propagator.Optimized.lower model
```

Both lowerings preserve authored candidate order and constraint meaning.
Rich-lattice or dataflow models lower only to General, and the `Error` value
names why an Optimized lowering is unsupported.

General also offers batched factories and `propagate` for callers that need a
strict budget on aggregate narrowing events. Named `ConstraintId` values join
premises in structural support without introducing an event log.

## Build and run the included checks

Requirements: .NET 8 SDK. The numeric suite also restores
`MathNet.Numerics.FSharp` for its external `BigRational` compatibility check.

```powershell
dotnet build .\Propagator\Propagator.fsproj
dotnet fsi .\Propagator.Tests\run.fsx
```

The aggregate script loads the actual library sources in project order, then
runs the friendly/core regression suite and the separate numeric suite. The
tests contain fixtures and independent oracles, but no copied propagator
implementation.

Optional benchmark switches are forwarded to the regression suite:

```powershell
dotnet fsi .\Propagator.Tests\run.fsx --benchmark
dotnet fsi .\Propagator.Tests\run.fsx --benchmark-read-sweep
```

Benchmark this repository through complete same-process suites and compare
within-run ratios; absolute timings on the development machine vary
substantially with machine uptime.

## Project layout

```text
Propagator/
  Propagator.fsproj       # library (net8.0)
  Core.fs                 # vocabulary, models, domains, numeric value algebras
  General.fs              # generic finite and rich-lattice engine
  Optimized.fs            # optimized finite-domain engine
  Network.fs              # ergonomic facade and Ops dialect
  README.md

Propagator.Tests/
  run.fsx                              # aggregate source-loaded runner
  propagator-friendly.tests.fsx        # facade, core, differential, and benchmark checks
  propagator-number-types.tests.fsx    # scalar, rational, interval, and fixed-point checks
```

The historical root scripts remain executable design records. The compiled
library and `Propagator.Tests` are the enduring consumer and verification
surfaces.
