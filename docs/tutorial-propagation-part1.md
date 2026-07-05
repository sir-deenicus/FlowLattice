# Build Your Own Propagation Engine
### Part 1 — One Little Engine, Two Puzzles

```
   ╔══════════════════════════════════════════════════════════╗
   ║                                                          ║
   ║     ·  cells that gossip  ·  knowledge that combines  ·  ║
   ║     ·  and an engine that changes its mind  ·            ║
   ║                                                          ║
   ╚══════════════════════════════════════════════════════════╝
```

> **Difficulty:** ★★☆☆☆ &nbsp;·&nbsp; **You'll need:** F# + `dotnet` (we run everything with `dotnet fsi`) &nbsp;·&nbsp; **Time:** one coffee
> *— Claude (Opus 4.8), 2026-06-22. This is the current stage of a thing we are building; it will grow more capable later. Today it gets* clear.

---

## 0. The plan

We are going to build a small engine — about sixty lines — and then point it at two problems that appear to have nothing in common:

1. **Celsius ⇄ Fahrenheit**, both directions at once, where you may *change your mind* about the input and watch the rest catch up.
2. **A 4×4 Sudoku.**

The claim worth your attention is that **a single engine handles both** — not one engine with a Sudoku attachment, but the *same* cells, the *same* combine step, the *same* loop. Between the two problems we swap only the few lines that describe the problem itself.

By the end the two will look like one problem viewed from two angles. Earning that "of course" is the goal.

---

## 1. The idea in one sentence

> A collection of **cells** holds *what is currently known*; a collection of small **propagators** watches some cells and, on learning anything, writes *more* knowledge into others; we let them run until nothing new can be said.

Everything below is precision about three words in that sentence: *cells*, *known*, and *more*.

---

## 2. A cell holds *what is known*, not a value

An ordinary variable holds a value. A **cell** holds something more general: the current *state of knowledge* about some quantity. That state is one of three kinds of thing:

```
        Top          "Nothing is known yet."      (every value still possible)
         │
       value          "It is 212."                 (pinned to one possibility)
         │
         ⊥            "Impossible."                 (no value satisfies what we were told)
       (bottom)
```

Read top-to-bottom as *increasing knowledge*. `Top` is total ignorance — everything remains possible. As facts arrive the cell **descends**: from `Top` to a definite value. If two facts genuinely conflict, it falls to **`⊥`** ("bottom"), the cell reporting that what it was told cannot all hold at once.

> The order is deliberately upside-down: "knowing more" means "fewer possibilities survive," so the *informative* direction points **down**. This structure is a **lattice** of information — `Top` is its greatest element, `⊥` its least. (A term to search, if useful: a partial order *by refinement*.)

We will reuse this single picture for both problems, and that reuse is the entire trick.

---

## 3. Combining knowledge: the **meet**

A cell typically hears from several sources. Fahrenheit hears "the user typed 212" and also "Celsius is 100, therefore 212." A Sudoku cell hears from all of its neighbours. We need one rule for folding two pieces of knowledge into one.

That rule is the **meet** (written `⊓`). The point of interest is that it is the same idea in both problems, despite operating on different data.

**Temperatures** — a single number:

| known | new fact | `meet` |
|---|---|---|
| nothing (`Top`) | it is 212 | **212** |
| it is 212 | it is 212 | **212** (agreement) |
| it is 212 | it is 32 | **⊥** (conflict) |

**Sudoku** — a *set* of surviving candidates:

| known | new fact | `meet` |
|---|---|---|
| `{1,2,3,4}` | not 2, not 4 | **`{1,3}`** |
| `{1,3}` | must be 3 | **`{3}`** |
| `{3}` | must be 1 | **`{ }`** (conflict) |

The shared rule is **keep exactly what *both* sources permit.** For numbers that is "agree, or contradict." For candidate sets it is set intersection, and "nothing in common" is precisely `⊥`. (Its lattice name is the *greatest lower bound*.)

Change what a "value" is and what "meet" means, and you have changed problems. We will do exactly that, twice.

---

## 4. Propagators: small components that share what they learn

A **propagator** is a small component wired to some cells. It does one thing: read its cells, and if it can conclude something, `meet` that conclusion into another cell.

Temperatures need two of them:

```
        ┌─────────────┐      C→F:  f = c·9/5 + 32      ┌─────────────┐
        │   Celsius   │ ───────────────────────────▶  │ Fahrenheit  │
        │     cell    │  ◀───────────────────────────  │    cell     │
        └─────────────┘      F→C:  c = (f−32)·5/9      └─────────────┘
```

Set **Celsius = 100**. `C→F` fires and writes 212 into Fahrenheit. That changed Fahrenheit, so its watchers wake; `F→C` fires and computes Celsius = 100 — which we already had, so `meet(100, 100) = 100` and nothing changes. With nothing new to say, the system goes quiet, and we are done.

"Fire the propagators with something to react to; stop when the system goes quiet" is the engine's heartbeat — a **worklist**. Because every step only ever *adds* knowledge (cells descend, never rise), the system is guaranteed to fall silent; it cannot loop forever.

That property — *we only ever learn more* — is called **monotone**. Hold onto it: in the next section we break it deliberately, and that is where the real idea lives.

---

## 5. Problem #1: Sudoku is the *easy* case

The mild surprise is that **Sudoku is the easy one.** Solving it is *pure learning*: you begin knowing little ("each cell is some digit 1–4") and only ever cross possibilities off. Nothing is ever taken back — monotone from start to finish. It is the quiet-system loop of Part 4 with a single kind of propagator:

> **The "all-different" propagator.** For each row, column, and box: if some cell in the group is pinned to a value `v`, none of its neighbours may be `v`, so cross `v` off them. (A freshly pinned cell is a *naked single*; the twin rule — if a value has only one possible home left in a group, place it there — is a *hidden single*. Our code runs both, and together they finish any clean 4×4 without guessing.)

Each "cross `v` off" is a `meet` with "everything except `v`." Cells descend, the system quiets, and when it does every cell is pinned — solved. No guessing, no backtracking. We will return to that phrase, **no guessing**.

---

## 6. Problem #2: temperatures, and the complication

The first time you set a temperature it behaves exactly like Sudoku: set Celsius, Fahrenheit fills in, the system quiets. Monotone.

Now do what any real interface demands. Set **Celsius = 100**, then reconsider and set **Celsius = 0**.

The cell currently *knows* "Celsius is 100." You now assert "Celsius is 0." What is `meet(100, 0)`?

It is **⊥**. A contradiction — and the engine is correct, because given everything it was told, "100 and 0" cannot both hold. This is the wall, and it is the reason this project exists. Monotone learning cannot *change its mind*, because doing so means **un-learning** — rising back toward `Top` — and `meet` only descends.

### The resolution: record *why* each fact is believed

The move is to store not only *what* a cell knows, but *why*.

Asserting "Celsius = 100" is not a law of nature; it is a *revocable choice*. Call that choice a **premise**, and stamp every derived fact with the premises it rests on:

- "Celsius = 100" is stamped *premise #1*.
- The propagator derives "Fahrenheit = 212" *from* Celsius, so that fact inherits the same stamp — *premise #1*.

Changing your mind is now precise. To set Celsius = 0:

1. **Retract premise #1.** Delete every fact stamped `#1` — "Celsius = 100" *and* the "Fahrenheit = 212" derived from it. Both cells rise back to `Top`.
2. **Assert premise #2: Celsius = 0.** The system re-derives from clean cells; Fahrenheit becomes 32.

`meet(100, 0)` never occurs, because the 100 — and everything resting on it — was cleared before the 0 arrived.

> ### A clarification worth pausing on: this is not backtracking
> A **backtracking** solver *guesses* ("try a 5 here"), proceeds, hits a wall, and **rewinds** to its last guess to try another. That is **search**: trying possibilities in some order and undoing them in reverse — a maze-walk with a ball of string.
>
> We never guessed, and never rewound *time*. We kept a record of *reasons*, and when a reason was withdrawn we deleted exactly the facts that depended on it — regardless of when they were added or what followed. Not "undo the last few steps," but **"these particular beliefs lost their support, so they are gone."** That is not search; it is bookkeeping about reasons, and the un-learning it provides is simply **propagation run in reverse**.
>
> (Guess-and-rewind is a real and useful technique for problems too hard to reason out — but it is a *separate* component layered *on top* of this engine, never part of it, and pointedly not where this engine comes from. Later.)

So: **Sudoku never performs step 1.** You never retract a clue, the "record why" machinery sits idle, and you get plain monotone narrowing. **Temperatures use it** the moment you re-assign. One engine; one problem simply exercises a capability the other never needs.

That is the "of course." Now to the code.

---

## 7. The code

Three parts: the **engine**, which knows nothing of either problem; then **temperatures**; then **Sudoku** — first in a plain `Set<int>` form that reads like the rest of the article, then re-encoded as bitsets for speed. Paste the blocks below, in order, into `engine.fsx` and run `dotnet fsi engine.fsx`. It is plain F#, no packages.

A note for readers newer to F#: I explain each idiom as it first appears. The two recurring shapes are the **record** (`{ field = ... }`, a fixed bundle of named fields) and the **discriminated union** (`type T = A | B of int`, a value that is *one of* several labelled shapes, taken apart with `match`).

### 7a. Vocabulary: facts and cells

```fsharp
open System.Collections.Generic

// A premise is a label for "a choice we are making now and could revoke later."
type Premise = int

// Who placed a fact on a cell — an outside assertion, or one of our propagators?
type Origin = Ext of Premise | Prop of int

// A fact about a cell: a value, plus the set of premises it rests on (its "stamp").
type Contribution<'a> = { value: 'a; support: Set<Premise> }
```

`Premise` is just an `int` — a name we can compare and look up. `Origin` is a discriminated union with two cases, recording *where a fact came from*: `Ext p` for something asserted from outside under premise `p`, or `Prop n` for something computed by propagator number `n`. `Contribution` is the fundamental unit: a `value` together with `support`, the immutable `Set` of premises it depends on. That `support` field is the whole "record why" mechanism, hiding in plain sight.

```fsharp
type Cell<'a> =
    { id: int
      contribs: Dictionary<Origin, Contribution<'a>>   // keyed by source, so a re-write replaces
      mutable value: 'a                                // cached meet of everything in contribs
      mutable support: Set<Premise> }                  // why `value` is what it is
```

A cell is a small whiteboard. It keeps every source's latest fact in `contribs`, a dictionary **keyed by `Origin`** — so when the same source speaks again, its new fact *replaces* the old one rather than piling up. The `value` and `support` fields are `mutable` because we recompute and cache them as facts arrive; caching means a *read* of the cell is instant, never a re-fold over all contributions. The `<'a>` makes `Cell` generic over what a value is — a number for temperatures, a candidate set for Sudoku.

### 7b. Vocabulary: the two things that vary

Here is the seam. Everything puzzle-specific is bundled into one small record:

```fsharp
// The entire difference between our two problems lives in here.
type Lattice<'a> = { top: 'a; meet: 'a -> 'a -> 'a }
```

Just two fields: `top` (the "nothing known yet" value a fresh cell starts at) and `meet` (how to combine two values). That is genuinely all the engine needs today.

> **Aside — why only two fields? (and what is coming)**
> A fuller lattice would carry four:
> ```fsharp
> type Lattice<'a> = { top: 'a; meet: 'a -> 'a -> 'a; isBot: 'a -> bool; eq: 'a -> 'a -> bool }
> ```
> We leave two out *because, at this stage, the engine never calls them*:
> - **`eq`** — how to tell whether a value changed. F#'s built-in structural `=` already does this for our values, so a custom equality earns its place only when `=` is the *wrong* notion — e.g. floats you want compared within a tolerance, where `212.0000001` should count as `212`. Our demo's numbers are exact, so `=` suffices.
> - **`isBot`** — how to recognise `⊥`. The engine will need this once it *acts* on a contradiction (refusing the input, or letting a future search component react). In Part 1 we only ever *record* `⊥`; nothing reacts to it, so the field would be dead code.
>
> So this is an early-stage trim, not a different design: the contract asks for exactly what it uses. (The design journal keeps the four-field version on purpose, because Part 2 starts using both.)

A propagator reads some cells and, when fired, returns a list of edits — each a target cell paired with a fact to `meet` into it:

```fsharp
type Propagator<'a> =
    { pid: int                                             // this propagator's id (its slot in a cell)
      reads: Cell<'a> list                                 // which cells to watch
      fire: unit -> (Cell<'a> * Contribution<'a>) list }   // what to conclude, when asked
```

`fire` takes `unit` (no arguments — it reads the cells it has closed over) and returns a list, because one propagator may update several cells at once, or — returning the empty list — conclude nothing yet.

### 7c. The engine: storage and recompute

Now the engine itself, as a class parameterised by a `Lattice`. The `when 'a : equality` is F#'s way of saying "this works for any value type that supports `=`," which we rely on for change detection.

```fsharp
type Engine<'a when 'a : equality>(L: Lattice<'a>) =
    let cells = ResizeArray<Cell<'a>>()                          // every cell, for retraction sweeps
    let watch = Dictionary<int, ResizeArray<Propagator<'a>>>()   // cell id -> propagators watching it
    let mutable nCell = 0
    let mutable nProp = 0

    // Re-fold a cell's facts into its cached value + stamp.
    member private _.Recompute (c: Cell<'a>) =
        let mutable v = L.top
        let mutable s = Set.empty
        for kv in c.contribs do
            v <- L.meet v kv.Value.value
            if kv.Value.value <> L.top then s <- Set.union s kv.Value.support
        c.value <- v; c.support <- s
```

`ResizeArray` is F#'s name for .NET's growable list; `watch` is the reverse index that makes the worklist efficient — given a changed cell, it tells us which propagators care. `Recompute` is the heart of "combine": start from `top` (knowing nothing) and `meet` in every contribution, so the result is everything all sources jointly allow. Alongside, it unions the `support` of every *informative* contribution — the `if ... <> L.top` skips contributions still at `Top`, since a fact that constrains nothing justifies nothing. The result is the cell's cached `value` and the exact set of premises holding it up.

### 7d. The engine: the heartbeat

```fsharp
    // still inside type Engine — fire propagators until the system goes quiet
    member private this.Quiesce (frontier: seq<Cell<'a>>) =
        let q = Queue<Propagator<'a>>()
        let wake (c: Cell<'a>) = for p in watch.[c.id] do q.Enqueue p
        Seq.iter wake frontier
        while q.Count > 0 do
            let p = q.Dequeue()
            for (target, fact) in p.fire () do
                let before = target.value
                target.contribs.[Prop p.pid] <- fact          // record this propagator's latest word
                this.Recompute target
                if target.value <> before then wake target    // changed? then wake its watchers
```

This is the worklist. We seed a queue with every propagator watching a cell in the starting `frontier`, then drain it: fire a propagator, and for each edit it returns, file the fact under that propagator's slot (`Prop p.pid`) in the target — replacing whatever it said before — and recompute. The decisive line is the last: we wake the target's watchers **only if its cached value actually changed.** That guard is what makes the loop terminate — under monotone updates a value can only change finitely often before it can descend no further, so the queue must eventually empty.

### 7e. The engine: the public verbs

```fsharp
    // still inside type Engine — construction helpers
    member _.NewCell () =
        let c = { id = nCell; contribs = Dictionary(); value = L.top; support = Set.empty }
        nCell <- nCell + 1; watch.[c.id] <- ResizeArray(); cells.Add c; c

    member _.AddProp (reads, fire) =
        let p = { pid = nProp; reads = reads; fire = fire }
        nProp <- nProp + 1
        for c in reads do watch.[c.id].Add p     // register p as a watcher of each cell it reads
        p

    member _.Value (c: Cell<'a>) = c.value
```

Bookkeeping. `NewCell` mints a cell at `Top` with a fresh id and an empty watcher list. `AddProp` registers a propagator and — the important part — adds it to the `watch` list of every cell it reads, so changes to those cells will later wake it. `Value` simply reads the cache.

```fsharp
    // still inside type Engine — the two verbs that matter
    member this.Assert (p: Premise, c: Cell<'a>, v: 'a) =
        c.contribs.[Ext p] <- { value = v; support = Set.singleton p }
        this.Recompute c
        this.Quiesce [c]

    member this.Retract (p: Premise) =
        let touched = ResizeArray<Cell<'a>>()
        for c in cells do
            let dead = [ for kv in c.contribs do if kv.Value.support.Contains p then yield kv.Key ]
            if not dead.IsEmpty then
                for k in dead do c.contribs.Remove k |> ignore   // drop every fact stamped p
                this.Recompute c                                 // value rises back toward Top
                touched.Add c
        this.Quiesce touched                                     // re-derive whatever still has support
```

These two are the whole interface. `Assert(p, c, v)` files an external fact "`c` is `v`, on premise `p`," stamped with just `{p}`, then lets the system settle. `Retract(p)` is the dependency-directed un-learning from Part 6 made literal: sweep every cell, collect the keys of contributions whose `support` contains `p` (gathered into `dead` first, because we cannot delete from a dictionary while iterating it), remove them, and recompute — which lets the cell rise back up as its support vanishes. A final `Quiesce` re-derives anything that *still* has independent support. Note what is absent: no log of past states, no notion of "the last step." Deletion is keyed entirely on *reasons*.

That is the complete engine — and it mentions neither temperatures nor Sudoku, neither numbers nor sets. Everything specific arrives from outside, starting now.

### 7f. Temperatures: the lattice

A value is unknown, a number, or impossible; `meet` says "agree, or contradict":

```fsharp
type Scalar = Top | Val of float | Bot       // unknown / a number / impossible

let scalarL =
    { top = Top
      meet = fun a b ->
        match a, b with
        | Top, x | x, Top       -> x           // "nothing known" combined with anything = the anything
        | Val x, Val y when x=y -> Val x        // they agree
        | _                     -> Bot }        // anything else (two different numbers) = contradiction }
```

`Scalar` is our flat ladder as a three-case union. The `meet` is a `match` over the pair `a, b`: if either side is `Top`, the answer is the other side (knowing nothing adds nothing); if both are equal numbers, that number stands; every remaining case — two *different* numbers, or anything involving `Bot` — collapses to `Bot`. The `when x=y` is a *guard*, an extra condition on a match case.

### 7g. Temperatures: the propagators

```fsharp
let e  = Engine<Scalar>(scalarL)
let cC = e.NewCell()   // Celsius
let fF = e.NewCell()   // Fahrenheit

e.AddProp([cC], fun () -> match cC.value with Val x -> [ fF, { value = Val (x*9.0/5.0 + 32.0); support = cC.support } ] | _ -> []) |> ignore   // C→F
e.AddProp([fF], fun () -> match fF.value with Val y -> [ cC, { value = Val ((y-32.0)*5.0/9.0); support = fF.support } ] | _ -> []) |> ignore   // F→C
```

Two cells and two propagators. Read the first `fire` as: *when Celsius holds a number `x`, emit one edit — set Fahrenheit to `x·9/5 + 32` — and stamp that fact with `cC.support`; otherwise (`Celsius` is `Top` or `Bot`) emit nothing.* That `support = cC.support` is the crucial line: the derived Fahrenheit fact inherits Celsius's reasons, so retracting those reasons later will take the 212 with them. The trailing `|> ignore` discards the `Propagator` handle that `AddProp` returns, which we do not need here.

### 7h. Temperatures: changing your mind

```fsharp
e.Assert(1, cC, Val 100.0)
printfn "set C=100  ->  F = %A" (e.Value fF)

e.Retract 1
e.Assert(2, cC, Val 0.0)
printfn "set C=0    ->  F = %A" (e.Value fF)
```

Set Celsius to 100 under premise 1; Fahrenheit settles at 212. Then retract premise 1 — which removes the 100 *and* the 212 that leaned on it, leaving both cells at `Top` — and assert 0 under premise 2. Fahrenheit re-derives to 32, and `meet(100, 0)` is never formed.

### 7i. Sudoku: the lattice and helpers

The same engine, a new lattice — and this one is almost a transcript of Part 3. A cell's value is the **set of digits still possible** for that square: a plain `Set<int>`. `Top` is `{1,2,3,4}` (all four still open), and `meet` is set intersection — keep only what both sides allow.

```fsharp
let setL = { top = set [1..4]; meet = Set.intersect }   // start open; combine by intersection
let solved (d: Set<int>) = Set.count d = 1              // pinned to exactly one digit?
```

`set [1..4]` builds `{1,2,3,4}`. `Set.intersect` is F#'s built-in set intersection, and it already has the exact shape `meet` wants (`'a -> 'a -> 'a`), so we hand it over by name. `solved` asks whether a cell is down to a single surviving candidate — our "this square is decided" test. Notice there is not a bit trick in sight: this lattice reads the same way the temperature one did, which is the point. (We will make it *fast* in Part 7m; first we make it *clear*.)

### 7j. Sudoku: the grid and its groups

```fsharp
let s    = Engine<Set<int>>(setL)
let grid = Array2D.init 4 4 (fun _ _ -> s.NewCell())
let at (cs: (int*int) list) = [ for (r, c) in cs -> grid.[r, c] ]

let units =                                       // the 4 rows, 4 columns, and 4 boxes
    [ for r in 0..3 -> [ for c in 0..3 -> r, c ] ]
  @ [ for c in 0..3 -> [ for r in 0..3 -> r, c ] ]
  @ [ for br in 0..1 do for bc in 0..1 -> [ for dr in 0..1 do for dc in 0..1 -> 2*br+dr, 2*bc+dc ] ]
```

`grid` is a 4×4 array of fresh cells; `at` turns a list of coordinates into the cells at them. `units` is the list of the twelve groups that must each contain `1,2,3,4` once: the first comprehension yields the four rows, the second the four columns, and the third the four 2×2 boxes (the `2*br+dr, 2*bc+dc` arithmetic walks each box's top-left corner over its four cells). The `@` operator concatenates the three lists.

### 7k. Sudoku: the two propagators

```fsharp
for u in units do
    let cs    = at u
    let sup () = cs |> List.map (fun p -> p.support) |> Set.unionMany
    // naked single: digits taken by a solved peer are no longer available here
    s.AddProp(cs, fun () ->
        [ for t in cs ->
            let gone = cs |> List.fold (fun a p -> if p.id <> t.id && solved p.value then Set.union a p.value else a) Set.empty
            t, { value = Set.difference (set [1..4]) gone; support = sup () } ]) |> ignore
    // hidden single: a digit with a unique home in the group lands there
    s.AddProp(cs, fun () ->
        [ for v in 1..4 do
            match cs |> List.filter (fun p -> p.value.Contains v) with
            | [t] when not (solved t.value) -> yield t, { value = set [v]; support = sup () }
            | _ -> () ]) |> ignore
```

For each group we attach two propagators. The **naked-single** one produces, for every cell `t` in the group, the set of digits still allowed: it folds the group into `gone` — the **union** of the candidate sets of every *other* cell that is already `solved` — and emits `{1,2,3,4}` minus `gone` (`Set.difference`), i.e. "anything not already claimed by a settled peer." The **hidden-single** one walks each digit `v`, filters the group to the cells whose candidate set still `Contains` it, and if exactly one such home `[t]` survives and is not yet pinned, places `v` there as the singleton `set [v]`. Both stamp their output with `sup ()`, the union of the group's supports — a deliberately generous stamp we will refine later. (`List.fold` is a left fold; `List.filter` keeps matching elements; the `match` on a one-element list `[t]` is how we detect "a unique home.")

### 7l. Sudoku: clues, and reading it back

```fsharp
let givens = [ (0,0),1; (0,1),2; (0,3),4; (1,1),4; (2,0),2; (2,3),3; (3,1),3; (3,2),2 ]
givens |> List.iteri (fun i ((r, c), v) -> s.Assert(100 + i, grid.[r, c], set [v]))

let digit (d: Set<int>) = if solved d then Set.minElement d else 0
printfn "\n4x4 Sudoku:"
for r in 0..3 do printfn "   %A" [ for c in 0..3 -> digit (s.Value grid.[r,c]) ]
```

Each clue is asserted as a premise — the singleton candidate set `set [v]` — and `List.iteri` hands each a distinct premise number `100 + i`. We never retract them, so retraction stays asleep and the whole solve is monotone: Sudoku as the pure-narrowing case. Finally `digit` decodes a settled cell back to its number (`Set.minElement` of a one-element set; `0` if it is somehow still unsettled), and we print the grid.

### 7m. The same Sudoku, made fast

The version above runs and solves, and it reads cleanly because a `Set<int>` says exactly what it means. But a `Set<int>` is a heap object: every `meet` allocates a fresh set, every `Contains` chases pointers. On a 4×4 that cost is invisible. On a 9×9 — or the 50,000-tile texture a wave-function-collapse generator grinds through — that per-cell allocation *is* the running time. So here is the standard optimization, worth doing now precisely because you have the readable version to check it against.

A candidate set over the digits `1..4` is really just **four bits**, so pack it into a `uint16`: bit 0 = "1 is still possible," bit 1 = "2," and so on. Every set operation we used then becomes a single instruction on that integer — no allocation, no pointer-chasing:

| set version | bit version | what it is |
|---|---|---|
| `set [1..4]` | `0xFus` | `1111` — all four open |
| `Set.intersect` | `&&&` | intersection = bitwise AND |
| `Set.union a b` | `a ||| b` | union = bitwise OR |
| `Set.difference (set [1..4]) g` | `0xFus &&& ~~~g` | "all, except `g`" = AND-NOT |
| `x.Contains v` | `x &&& bit v <> 0us` | membership test |
| `set [v]` | `bit v` | the singleton `{v}` |
| `Set.count d = 1` | popcount `= 1` | "exactly one candidate" |

Read across any row and the meaning is unchanged; only the machine work shrinks. The lattice and helpers become:

```fsharp
let bitL = { top = 0xFus; meet = (&&&) }                       // 1111; meet = intersection = AND
let bit v : uint16 = 1us <<< (v - 1)                           // digit v -> its single bit
let single (d: uint16) = d <> 0us && (d &&& (d - 1us)) = 0us   // exactly one bit set?
```

`0xFus` is the `uint16` literal `1111`; `bit v` shifts a `1` into digit `v`'s slot. `single` is the classic "is this a power of two?" test: `d &&& (d - 1us)` clears the lowest set bit, so the result is zero exactly when only one bit was set — a one-instruction stand-in for `Set.count d = 1` (it compiles to a `popcnt` on real hardware). These three lines are the only genuinely *new* idea in this section; the rest is transliteration.

The `grid`, `at`, and `units` definitions from Part 7j never mentioned the representation, so keep them verbatim — only build the engine over `uint16`. The two propagators are then Part 7k passed through the table above, line for line, set operations turning into bit operations and nothing else moving:

```fsharp
let s    = Engine<uint16>(bitL)
// grid, at, units: exactly as in Part 7j

for u in units do
    let cs    = at u
    let sup () = cs |> List.map (fun p -> p.support) |> Set.unionMany
    s.AddProp(cs, fun () ->                                    // naked single
        [ for t in cs ->
            let gone = cs |> List.fold (fun a p -> if p.id <> t.id && single p.value then a ||| p.value else a) 0us
            t, { value = 0xFus &&& ~~~gone; support = sup () } ]) |> ignore
    s.AddProp(cs, fun () ->                                    // hidden single
        [ for v in 1..4 do
            match cs |> List.filter (fun p -> p.value &&& bit v <> 0us) with
            | [t] when not (single t.value) -> yield t, { value = bit v; support = sup () }
            | _ -> () ]) |> ignore
```

The clues and read-back change the same mechanical way — `set [v]` becomes `bit v`, and `digit` hunts for the lone set bit:

```fsharp
givens |> List.iteri (fun i ((r, c), v) -> s.Assert(100 + i, grid.[r, c], bit v))
let digit (d: uint16) = if single d then [1..4] |> List.find (fun v -> d &&& bit v <> 0us) else 0
```

Swap this block in for Parts 7i–7l (same variable names throughout, so it is a true drop-in), re-run, and the printed grid is identical — same engine, same two propagators, same answer, with the per-cell allocation gone. That is the point worth keeping: the speed lived entirely in the *representation*, and the representation was sealed behind `Lattice`. The reasoning never had to know it changed.

---

## 8. Run it

```
$ dotnet fsi engine.fsx

set C=100  ->  F = Val 212.0
set C=0    ->  F = Val 32.0

4x4 Sudoku:
   [1; 2; 3; 4]
   [3; 4; 1; 2]
   [2; 1; 4; 3]
   [4; 3; 2; 1]
```

The same `Engine` class, the same `Assert`, the same quiet-system loop. The temperature run changed its mind mid-stream and landed correctly with no contradiction; the Sudoku run filled the grid into a proper Latin square — every row, column, and box holding `1,2,3,4` exactly once — purely by crossing candidates off. Nothing was guessed. (Swap in the Part 7m bitset block and re-run: the grid prints byte-for-byte the same, only faster.)

---

## 9. What just happened

Notice what we did *not* write: not a temperature solver, not a Sudoku solver. We wrote one engine that understands cells, combining, and reasons, then *described* two problems to it:

| | temperatures | Sudoku |
|---|---|---|
| **a value is…** | unknown / a number / impossible | a set of candidate digits |
| **`meet` is…** | "they must agree" | set intersection |
| **`⊥` is…** | two numbers in conflict | the empty candidate set |
| **ever change your mind?** | **yes** → uses retraction | **no** → retraction sleeps |

Two columns — that is the entire distance between a live, two-way, re-editable converter and a Sudoku solver. Everything else is shared and untouched.

The last row is the deepest. **Sudoku is the temperature engine with the "change your mind" capability held still.** Stated plainly: *constraint solving is propagation that only ever moves in one direction.* They were never two things; they are one thing, seen with and without retraction.

---

## 10. A fair question: is this a state machine?

It is reasonable to ask, since "state that changes over time" describes both. The honest answer is *no, not in the usual sense* — and seeing why sharpens the whole idea.

A classic **finite state machine** has a single current state and moves between states according to (current state, input). The path matters: feed the same inputs in a different order and you can land somewhere else. It is a model of *control* — where am I now, where do I go next.

Our network is the opposite on the points that count. There is no single global state, but many cells. The update — `meet` — is commutative and idempotent, so **the order in which propagators fire does not affect the result**; the system always settles to the same answer. That order-independence (its proper name is *confluence*, and the answer it settles to is a *fixed point*) is exactly what a state machine does *not* have. This is a model of *information flow*, not control: knowledge accumulating until it stops growing.

So the nearer relatives are **spreadsheets** (change a cell, dependents recompute), **dataflow networks**, and — in compilers — *dataflow analysis*, all of which compute a fixed point over a lattice. If you insist on the state-machine framing, the most you can say is that a *single cell* is a degenerate one whose only permitted move is "learn more, never go back" — and "never go back" is precisely the freedom a general state machine has and we have given up.

Except in one place. **Retraction is where genuine, history-like state re-enters**: which premises are currently live is real, mutable state, and withdrawing one moves the system *backward*. That is the one spot with a state-machine flavour — and, not coincidentally, it is the hard and interesting part we keep circling. The clean monotone core is not a state machine; the retraction layer is where a little of that character is unavoidable, and we contain it deliberately.

---

## 11. Next time

This was the **clear** build, shaped so every part is visible — not the *fast* one. Several honest loose ends remain:

- **Speed.** Part 7m took the first step — candidate sets became `uint16` bitsets — but the engine *around* them still reads better than it runs: the `Set<Premise>` stamps and the per-cell dictionaries would crawl on a 9×9, let alone a 50,000-tile texture. Next time we give *those* the same treatment — array-backed cells and a tight worklist — and measure the difference, without altering a single law from today.
- **When reasoning runs out.** A clean 4×4 falls to crossing-off alone; the hardest Sudoku does not, and at some point you must genuinely *guess*. We will meet that guessing component, and the point will be that it sits **outside** this engine and merely *drives* it (recall the clarification box in Part 6). The engine stays a reasoning engine.
- **Sharper reasons.** Our stamps are deliberately generous — a fact carries *all* its inputs' premises, even ones it did not strictly need. Harmless (it only causes some extra re-derivation), but a precise reason-tracker does better, and we will weigh the trade.
- **The two fields we omitted.** `isBot` and a custom `eq` (the Part 7b aside) return the moment the engine starts *acting* on contradictions and caring about approximate equality.

> **Exercises.** (1) Add Kelvin (`K = C + 273.15`) as two more propagators and confirm all three stay consistent, including through a retraction. (2) Give the Sudoku two clues that conflict and find where `⊥` appears (inspect the cells' `value`). (3) Construct a 4×4 that naked + hidden singles cannot finish, and satisfy yourself that the engine gets *stuck*, not *wrong* — that gap is exactly what the "next time" guessing component fills.

Until then: cells hold what is known, propagators spread it, and the only genuinely hard part of the whole business is knowing how to *change your mind*.

See you in Part 2.

*— the IncrementalControl notebook. This streamlines the verified sketch in [constraint-engine-design-journal.md](constraint-engine-design-journal.md) (2026-06-22), which keeps the fuller four-field lattice for what Part 2 needs. Runs under `dotnet fsi`, plain F#, no packages.*
