# Clause builder — `CustomOperation` DSL plan

*— Claude (Opus 4.8), 2026-06-18; implemented by Codex, 2026-06-18*

Goal: shrink what a clause costs to write in `clauses { }` by moving the clause
vocabulary **onto the builder** as `[<CustomOperation>]` members, rather than
adding free functions (`when_`, `whenValue`) to `module AsyncRx`. Keeping the
vocabulary on the builder makes it cohesive and discoverable (IntelliSense inside
`clauses { }` surfaces exactly the legal clause heads) and reads as one DSL
instead of "a list builder plus some loose helpers."

This supersedes the earlier chat suggestion of free `when_`/`whenValue` helpers,
and folds in the "drop `yield`" finding: with custom operations you don't write
`yield` at all.

> Status: **implemented.** A throwaway FSI spike confirmed that the
> custom-operation syntax compiles, multi-argument clause heads parse, generic
> result types unify across clause kinds, and the new operations can coexist with
> the old `yield` / `yield!` surface.

## Current state

[`module ClauseCE`](../AsyncRxHopac.fsx) is now a backward-compatible clause CE:
the original list-collecting `Yield`/`YieldFrom`/`Zero`/`Combine`/`Delay`
surface remains, and custom operations add the clause vocabulary directly to
`clauses { }`. `Run` still hands the list to `Choice.firstValue`. The old shape
cost two bits of ceremony per clause:

```fsharp
clauses {
    yield case a (function true -> Some "a was true" | _ -> None)
    yield case b (function true -> Some "b was true" | _ -> None)
    yield asyncRx { ... }
}
```

1. `yield` on every clause — pure ceremony.
2. `case a (function true -> Some r | _ -> None)` — the `Some/None` chooser is
   noise for the common "value equals / satisfies predicate" case.

## Target surface

```fsharp
clauses {
    whenValue a ((=) true) "a was true"
    whenValue b ((=) true) "b was true"
    always (asyncRx {
                let! x = a
                and! y = b
                return sprintf "fallback: a || b = %b" (x || y) })
}
```

with `case` retained as the general escape hatch when a clause genuinely needs a
chooser:

```fsharp
clauses {
    case events (function Tick n when n > 0 -> Some n | _ -> None)
    guardOn ready "ready"
}
```

## Compaction accounting (how much this actually saves)

Be precise about the payoff — it is concentrated, not uniform.

- **`whenValue`/`guardOn` clauses — the real win.**
  `case a (function true -> Some "…" | _ -> None)` (~63 chars) →
  `whenValue a ((=) true) "…"` (~35). The `function … -> Some/None` ceremony is
  what disappears (~45% off the clause head).
- **`yield` removal — small but every clause, every block.**
- **`case`/`always` clauses — marginal.** They only shed `yield`; the
  chooser/body is unchanged. `case` stays a lambda by design.

File-level accounting: at the **call site** it compacts meaningfully for guarded
clauses. In **total line count** it is roughly a wash until `clauses` is used in
more than a couple of places — the builder *definition* grows ~10–15 lines (more
in superset mode, adding 4 custom ops on top of the list-style members). With one
call site today (`patternLikeDemo`), the net is near-neutral on lines.

Therefore frame this change as **readability- and discoverability-driven, not
line-count-driven.** The payoff is a cohesive clause vocabulary on the builder;
raw line savings only materialise once `clauses` earns its keep across several
sites. Do not justify it as a line-count reduction.

## Implemented builder

Each operation appends one clause to the accumulated `AsyncObservable<'U> list`;
`Run` feeds `Choice.firstValue`. The operations are thin wrappers over existing
`AsyncObservable` combinators — no new core logic, so semantics are unchanged.

```fsharp
type ClausesBuilder() =

    member _.Yield(x: AsyncObservable<'T>) = [ x ]
    member _.Zero() : AsyncObservable<'T> list = []
    member _.Yield(_: unit) : AsyncObservable<'T> list = []   // seed for the op chain
    member _.YieldFrom(xs: AsyncObservable<'T> list) = xs
    member _.Combine(xs: AsyncObservable<'T> list, ys: AsyncObservable<'T> list) = xs @ ys
    member _.Delay(f: unit -> AsyncObservable<'T> list) = f ()

    /// General chooser-based clause (wraps AsyncObservable.choose; ≡ existing `case`).
    [<CustomOperation("case")>]
    member _.Case(state, source, chooser) =
        state @ [ AsyncObservable.choose chooser source ]

    /// Clause that matches when `source` emits a value satisfying `pred`.
    [<CustomOperation("whenValue")>]
    member _.WhenValue(state, source, pred, result) =
        state @ [ source |> AsyncObservable.filter pred |> AsyncObservable.map (fun _ -> result) ]

    /// Guard on a bool known at build time (false ⇒ a non-matching clause that
    /// cannot win the race — exactly the `guard` design firstValue relies on).
    [<CustomOperation("guardOn")>]
    member _.GuardOn(state, cond, result) =
        state @ [ AsyncObservable.guard cond |> AsyncObservable.map (fun () -> result) ]

    /// Unguarded clause (e.g. a fallback body).
    [<CustomOperation("always")>]
    member _.Always(state, body) =
        state @ [ body ]

    member _.Run(state) = Choice.firstValue state
```

No `[<ProjectionParameter>]` is needed — these operations take ordinary
arguments, not lambdas over a range variable (there is no `for`-bound variable).

## Semantic note that drives naming

`clauses` is `Choice.firstValue`: **a first-value race, not ordered pattern
matching.** All clauses are subscribed; the first to emit a *value* wins; clauses
that complete empty (a false `guardOn`/non-matching `case`) are dropped so they
can't beat a slower match.

Consequence: an unguarded clause is not a guaranteed "else" — if it emits as fast
as a guarded clause, it can win the race. The example's fallback only loses
because its `let!/and!` zip resolves slightly later than the direct `whenValue a`
clause. So:

- Prefer the name **`always`** (honest: "an unguarded clause").
- Avoid **`otherwise`/`fallback`** unless we also guarantee last-resort
  semantics — which would mean changing `firstValue` (e.g. priority ordering),
  out of scope here.

## Decisions taken

1. **Operation names.** Keep `case` / `whenValue` / `guardOn` / `always`.
   `whenValue` and `guardOn` remove the `Some/None` noise; `case` remains the
   general escape hatch.
2. **Superset vs. pure.** Use the superset builder. The spike confirmed custom
   operations coexist with the original `yield` and `yield!` paths, so existing
   clause blocks stay source-compatible.

## Verification completed

A throwaway fsi spike (`#load "AsyncRxHopac.fsx"`, define the candidate builder,
exercise it) confirmed:

1. **It compiles & runs.** A custom-op-only `clauses { case…; whenValue…; always… }`
   elaborates and produces the right winner.
2. **Generic unification.** The result type `'U` unifies across heterogeneous
   ops in one block (`case` chooser-derived vs `whenValue` result vs `always`
   body) and with `Run`.
3. **Coexistence.** `yield` and `yield!` still work in a builder that also defines
   custom operations.
4. **Multi-arg parsing.** The 3-arg `whenValue source pred result` parses as a
   single clause head.
5. **firstValue semantics intact.** A false `guardOn` is dropped and does not
   preempt an unguarded value clause.

## Files changed

- [`AsyncRxHopac.fsx`](../AsyncRxHopac.fsx) — added `case`, `whenValue`,
  `guardOn`, and `always` custom operations to `ClauseCE.ClausesBuilder`, while
  keeping the old `yield` / `yield!` members.
- [`AsyncRxHopac.Examples.fsx`](../AsyncRxHopac.Examples.fsx) — rewrote
  `patternLikeDemo` to the new surface.
- [`AsyncRxHopac.Tests.fsx`](../AsyncRxHopac.Tests.fsx) — added coverage for the
  new custom operations and `yield!` compatibility.
- Updated the `clauses { }` description in the file header comment.

## Sequencing outcome

1. Spike passed.
2. Superset builder chosen.
3. Builder, example, tests, and docs updated.
4. Full verification harness rerun as part of implementation sign-off.

## Risks / trade-offs

- **Error messages.** Custom-operation CEs can produce worse diagnostics on a
  malformed clause than the current list builder. Acceptable for a small DSL.
- **Splicing/implicit-yield may be lost** if styles don't coexist (Verification
  #3) — mitigated by a `yieldAll` op.
- **`case` still takes a lambda.** Moving vocabulary onto the builder removes the
  `yield` and (via `whenValue`/`guardOn`) the `Some/None` noise, but `case`'s
  chooser stays a function literal by design — it's the general escape hatch.
