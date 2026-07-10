# Propagator surface design — two faces over one core

> Design doc, 2026-07-05. Author: Claude (Opus 4.8); Deen set the framing constraints, the design is mine. **Status: design phase.**
> This records the *surface/UX* direction over the already-built engine core; it is not yet a build
> plan. What is **Decided** vs **Open** is marked explicitly (§7). It resolves the design journal's
> "Public capability surfaces" open question and builds on D1/D2/D3/D10.

- **Engine core (built):** [propagator-mutable-core.fsx](../propagator-mutable-core.fsx) (`module M`, the
  general array-backed core) and [propagator-wfc.fsx](../propagator-wfc.fsx) (`module Wfc`, the specialized
  flat-word store + structural propagators + trail + cone-local `Retract` + min-entropy search).
- **Semantic authority:** [constraint-engine-design-journal.md](constraint-engine-design-journal.md)
  (D1 contract, D3 value rep, D10 store axis, the capability-surfaces open question).
- **Build plan for the core:** [mutable-core-plan.md](mutable-core-plan.md) (items 1–5, all built).
- **Conventions:** [style-guide.md](../style-guide.md) A2 (one model, surfaces on top), A3 (wrap the
  substrate), A4 (vocabulary on the builder), A5 (consume in ceremony order).

## Why now

D3 said to extract the operational interface *only after Sudoku and a minimal WFC slice expose their
needs*. Both now exist and are verified. The engine core is done (mutable-core steps 1–5). So the
surface is designable, and the journal's open question — *what belongs to the shared contract vs the
capability surfaces* — is answerable.

The trigger was a concrete complaint. Authoring a propagator today means writing the engine's own
bookkeeping at the call site. This is the C↔F relation on the general core
([propagator-mutable-core.fsx:389](../propagator-mutable-core.fsx)):

```fsharp
e.AddProp([cC], fun emit -> match e.Value cC with Val x -> emit fF (Val (x*9.0/5.0+32.0)) (e.Support cC) | _ -> ())
```

That is **not friendly**, and precisely why: you name `cC` three times — as the read (`[cC]`), to fetch
its value (`e.Value cC`), and to carry its provenance (`e.Support cC`) — all three restating one fact the
engine already knows from `reads`. You hand-thread the support mask, hand-match the not-yet-known case
(`| _ -> ()`), couple the body to a specific engine instance `e`, and — worst for a *relation* — author
one direction of a symmetric law with nothing tying it to its twin. The emit callback and the support
bitmask are *substrate*, and here the substrate is the surface (the A3 violation).

## 1. Current surface & boundary

`module M`'s whole public surface ([propagator-mutable-core.fsx:217](../propagator-mutable-core.fsx)):
`Lattice<'a> = { top; meet; isBot; equals }`, `NewCell() : int`, `AddProp(reads: int list, fire:
Emit<'a> -> unit)`, `Assert(p, c, v)`, `Retract(p)`, `Value/Support/IsBot`. Generic over the lattice,
arbitrary propagator graph via closures.

The boundary is drawn at the **machine representation** — every value crossing it is a raw primitive or
hand-threaded mechanism:

- a **cell** is a bare `int` with no name, coordinate, or type;
- a **premise** is a bare `int`, hand-allocated, with the 64-bit support ceiling exposed;
- a **lattice** is hand-built per domain (`{top;meet;isBot;equals}`), with no library of common ones;
- a **domain value** is a `uint64[]` mask (on the WFC side) the caller packs with knowledge of `W`;
- a **propagator** is a raw `Emit -> unit` closure (the teardown above);
- relations are **one-directional** — a bidirectional law is two hand-written closures;
- and the store **freezes at first `Assert`** ([propagator-mutable-core.fsx:252](../propagator-mutable-core.fsx)),
  so all cells/props must exist before any given, or you silently write into frozen arrays.

The only friendlier variants that exist anywhere are WFC's `AssertTile` (hides mask-building) and
`Collapse` (premise-free) — tile-domain helpers in the specialization, not a general layer. There are
**no friendly overloads** of `AddProp`/`Assert`. The friendliness has to be built.

## 2. Two faces, one library

The requirement (Deen): **one library that presents a friendly face — a simple `Set<int>` problem
written in a few lines — and an optimized face — a game-performant, 60fps, memory-cheap WFC.**

```
  friendly face                          optimized face
  clear rep: Set<int> / any 'a           flat words (W=⌈tiles/64⌉), structural props
  closure props · rich lattices          trail · cone-retract · min-entropy heap
  clarity / debugging / dataflow         60fps / memory-cheap / large grids
  ≈ module M dressed                     ≈ module Wfc dressed  (mostly already built)
        │                                       │
        │          one rep-agnostic             │
        └────────  AUTHORING VOCABULARY  ───────┘    ← write once, two lowerings
                   domains · cell handles · premise handles
                   constraint boxes · consume verbs
                          │  lowers onto ↓
        ┌─────────────────────────────────────────────┐
        │  ONE SEMANTIC CONTRACT (D1)                   │
        │  meet · ⊥ = empty · support / provenance      │
        │  premise-directed (TMS) Retract · observe     │
        └─────────────────────────────────────────────┘
```

This is D3 (clear vs fast) crossed with D10 (store axis), **promoted from a test-time clarity oracle
to a shipped surface**. The A6 differential already runs `Set<int>` and bitset against the same
semantic tests; the two faces are that pairing made into a product.

## 3. The unification is contract + vocabulary + pluggable rep — not storage

**Rejected: one universal flat `uint64[nCells*W]` store.** It serves the optimized face but cannot hold
a `Set<int>`; forcing the clear rep into words is the exact opposite of friendly. The word store is the
*optimized face's* store, not everyone's.

The unification lives *above* storage: one semantic contract (D1), one rep-agnostic authoring
vocabulary, and the rep as a **pluggable backend**. This dissolves the word-dispatch benchmark risk that
a single storage engine would have carried: with no shared store, the optimized face keeps `module Wfc`'s
specialized, inlined, structural-prop store exactly as it is — its 60fps numbers stand, no generic
per-word dispatch tax — and the friendly face keeps generic `Set<int>`. **They never share bytes, so
nothing on the hot path pays for the friendly face existing.**

## 4. Write-once, swap rep (the proposed spine)

Two readings of "one library, two faces":

- **(a) Shared laws only** — each face has its own wiring; you write WFC twice (Set for clarity, words
  for speed). The friendly version is a throwaway prototype.
- **(b) Write once, swap rep** — the shared artifact is a *rep-agnostic model* (`domains + cells +
  relations/constraints + presets`); it has **two lowerings** (friendly → generic Set-engine + closure
  props; optimized → flat-word store + structural props). Same model, pick the backend.

**Reading (b) is the proposed spine** — the only reading where it is genuinely *one* library and not two
sharing a header, and the honest continuation of the differential harness. Where only one lowering makes
sense (C↔F has no flat-word analog and does not need one — two cells), it collapses to a single face.
Escape hatches stay per-face: raw closures on the friendly side, hand-tuned structural props on the
optimized side. **High-level vocabulary is portable; low-level tuning is not.**

Status: **Tentative** — committing to (b) as the discipline is fork C (§7).

## 5. The vocabulary

The library's identity. Nouns, a spine, and verbs — all rep-agnostic enough to lower to both faces.
(Names below are illustrative, not final.)

**Nouns.**

- **Domains** — ready-made, so you never hand-roll `{top;meet;isBot;equals}`. Split by portability:
  - *finite* domains (`Domain.finite [1..9]`, `enum`) become rep-agnostic **descriptors** that lower to
    a `Set` lattice (friendly) *or* a `W`-word bitset store (optimized);
  - *rich* domains (`interval`, `scalar`) stay concrete `Lattice<'a>` values, **friendly-face-only** —
    there is no 60fps flat-word interval problem. [propagator-number-types.fsx](../propagator-number-types.fsx)
    already built float/decimal/BigRational/interval/scalar lattices; package them as reusable values.
  - `bitset n` is **not** a public domain — it is how the optimized backend *represents* a finite
    domain. Exposing it leaked the rep.
- **Cells** — named, typed handles (`net.cell "C"`) that carry their rep-agnostic domain; reads are
  presented uniformly across faces (current possibilities / decided value), not raw reps. The builder
  owns lifecycle, erasing the freeze-at-first-`Assert` trap. Being rep-portable is exactly what
  write-once needs.
- **Premises** — handles; allocation and the 64-bit ceiling hidden behind them, as `EventChoice.Token`
  hides Hopac. Both faces share the `uint64` support, so the ceiling is a **contract** decision, not a
  face detail (fork B).

**Spine — constraint boxes** (the A4 move; this is where the emit-closure dies):

- *CSP / finite flavor* — `allDifferent cells`, `adjacency grid rule`. These **carry the two lowerings**
  (Set closures *and* Wfc structural props); the write-once workhorses.
- *dataflow / rich flavor* — `convert f finv a b`, `sum a b c`, `equal a b`. Friendly-face-only. Here the
  relation is written **once** as plain functions and directionality + provenance are *generated*:
  `convert (fun c -> c*9./5.+32.) (fun f -> (f-32.)*5./9.) cC fF`.

**Verbs — consume, in ceremony order (A5):** `solve net` (one answer), `solutions net` (all / lazy),
`generate net` (seeded first-solution — what games want; not WFC-specific). These package the existing
search driver (`WfcSearch`: heap picker, big-stack thread, lazy `LazyList`, failure ref). Their
cross-face availability is gated by mechanism parity (fork A).

## 6. The lowering machinery (the piece none of the nouns/verbs captured)

Every vocabulary element needs a **lowering** to (friendly Set-engine + closure props) and (optimized
word-engine + structural props). That dispatcher is the real new engineering — the original friendly-
surface sketch assumed one `Engine<'a>` underneath; two faces replaces the single substrate with *one
vocabulary + two lowerings*.

This surfaces a **third instantiation dial**, orthogonal to D3 (value rep) and D10 (store):

- **propagator wiring** — *closure props* (arbitrary graph: Sudoku units, C↔F, dataflow) vs *structural
  props* (enumerated neighbors: the WFC grid, no per-edge closure, ~1M allocations avoided at 500×500).

The dials are genuinely independent: Sudoku already wants **flat-word store + closure props** (its unit
propagators are closures, [propagator-mutable-core.fsx:326](../propagator-mutable-core.fsx)), a combination
neither current engine offers as-is.

## 7. Decided vs open

**Decided.**
1. Two named faces (friendly / optimized) over one library.
2. Unify at the **contract + rep-agnostic vocabulary + pluggable rep**, *not* a single storage engine
   (the universal word store is rejected).
3. The constraint-box / relation vocabulary replaces the raw emit-closure; the emit-closure becomes a
   compilation target, not the surface.
4. The vocabulary is the spine; the noun re-typing of §5 (finite → descriptors, rich → friendly-only
   lattices, `bitset n` dropped).
5. Propagator wiring is a third instantiation dial (D13).
6. The **core/outer boundary is the dependency line** (§12): pure-F# constructs are core (both lowerings
   and the witnesses included); anything needing a `#r` (AsyncRx/Hopac, Hansei, MathNet, IntSharpCore) is
   an outer layer. Mechanically checkable, and it cuts *across* the two faces — dep-bearing lattices and
   Hansei strategies are outer plug-ins onto core seams (`Lattice` hook, `OnChange` callback).
7. **Observation is split in twain** (§12): the core exposes the push primitive (`OnChange` as a public
   `(CellChange -> unit) -> IDisposable` callback hook); the `AsyncObservable` wrapper is a single generic
   outer adapter. The core never names an Rx type.
8. **The differential/equivalence machinery is test-harness, not surface.** `Differential.solveBoth` —
   lower one model to both faces and diff the solutions — is the verification oracle that *earns* the
   "optimization is just a lowering" claim; it is **not a shipped verb**. A consumer picks a face
   (`General` / `Optimized`) and authors against it; it never calls `solveBoth`. So the equivalence
   check lives with the tests, where its cost (constructing *both* engines per model) is paid once in CI —
   not dragged into the shipped surface, which would force every build to carry the reference engine to
   satisfy a function nobody invokes in production. This is why the vocabulary's consume verbs (§5) are
   `solve` / `solutions` / `generate` only; `solveBoth` was never among them. (Naming, 2026-07-06 — Deen: the
   general (clear-rep) face's lowering module is `General`, renamed from the skeleton's `Friendly`, since it is
   the general any-rep backend rather than the ergonomic surface. Earlier text's "friendly face" denotes this
   same general face; "friendly UX" stays the name of [propagator-friendly.fsx](../propagator-friendly.fsx)'s
   ergonomic surface.)

**Open forks.**
- **Fork A — mechanism parity on the friendly backend.** `solve/solutions/generate` need trail +
  `Collapse` + `OnChange` (+ a *write-side* index for cone-local `Retract`; `module M`'s `watch` is
  reads-only). `module Wfc` has them; `module M` does not. Port them up into the friendly backend so the
  verbs work uniformly, or scope search / interactive-edit to the optimized face for v1?
  **Status 2026-07-05: RATIFIED (Deen).** Verb-level parity with **no mechanism port**; the friendly
  lowering of the verbs is dependency-directed backtracking on `module M` as-is (Fable review, §10).
  Encoded in the slice-0 skeleton ([propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx)).
- **Fork B — premise ceiling.** Both faces share the `uint64` ≤64 support. Commit to a growable premise
  rep now, or keep ≤64 and defer? Quiet, expensive failure mode for a heavy interactive editor — a
  candidate to summon a Tier-0 (Fable) decision rather than settle in passing.
  **Status 2026-07-05: decided (Fable, Tier 0; §10, journal D14)** — de-escalated to a per-backend
  *representation* decision; v1 = one word both backends + loud allocation failure at exhaustion;
  growth path is friendly-face-local P-word supports, on measured need.
- **Fork C — write-once commitment.** Lock reading (b) — one model, two lowerings — as the discipline, or
  accept shared-laws-only (a)?
  **Status 2026-07-05: RATIFIED (Deen).** Write-once locked, with the commitment scoped as *portable
  constructs are authored once* (not *all constructs are portable*) and an explicit falsifier to watch
  during the proof slices (Fable review, §10). Encoded in the slice-0 skeleton
  ([propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx)).

## 8. Relation to prior decisions

- **D1** (one semantic model) — the contract the surface sits on. Foundational, unchanged.
- **D2** (capability-specific surfaces, "need not expose identical operations") — this doc *resolves* it
  into the named friendly/optimized faces plus the write-once vocabulary. Sharpened, not reversed.
- **D3** (value rep clear/fast) — the two faces are its two ends, now a shipped surface; the interface
  D3 deferred "until Sudoku and WFC expose the needs" is what §5's vocabulary extracts.
- **D10** (store axis) — the optimized face uses the specialized flat-word store; the friendly face the
  mutable array. Consistent.
- **D4 / search** — `solve/solutions/generate` package the *separate* search layer (`WfcSearch`),
  unchanged; the front door does not move search into the engine.
- **A2/A3/A4/A5** — this design is those conventions applied to the propagator subsystem: one model with
  surfaces on top, the substrate wrapped, the vocabulary on the builder, consume in ceremony order.

## 9. Next

1. ~~Sketch the vocabulary in concrete F# signatures.~~ **Done 2026-07-05 (Opus):**
   [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) — `Domain` / `Cell` / `Premise` /
   `Constraint` / `Model`, the constructors, the two lowerings returning
   `Result<_, UnsupportedConstruct list>` (witnesses as return types), and both proof-slice models
   authored compile-only. Type-checks under fsi; every body is a stub.
2. ~~Lock forks A and C.~~ **Ratified 2026-07-05 (Deen).** A = no mechanism port / DDB; C = write-once,
   scoped to portable-constructs-authored-once. (Fork B was decided earlier, D14.)
3. ~~Fill the two proof slices — Celsius/Fahrenheit friendly-only, Sudoku portable through **both**
   lowerings differentially (`Differential.solveBoth`, sharpening 2) — replacing the skeleton's stubs.~~
   **Done 2026-07-06 (Opus):** [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) fills the
   stubs — both faces reach the known 4×4 solution and `solveBoth` confirms they agree bit-for-bit under
   fsi; C↔F stays general-only via named witnesses. This is where the vocabulary earns "feels honest."
4. Then work orders (item-5 precedent): dress `module M` as the general face (lattice + constraint-box
   library, named handles), and dress `module Wfc` as the optimized face reached through the same
   vocabulary + a `grid + tileset` preset.

**Reframed 2026-07-06 (Deen) — see §13.** The immediate task is not the two-lowering differential (items
3–4 above); it is a working **single-face friendly UX** whose proof is **re-authoring the existing worked
examples in it** (Celsius/Fahrenheit, barometer, Sudoku). The optimized face, `Differential.solveBoth`, and
dressing `module Wfc` are deferred behind it. The agreed shape (functions + methods, supplied helpers, the
`network` monad) is §13. **Update 2026-07-06 (later):** the friendly UX shipped (§13) and the two-lowering
differential (item 3, `Differential.solveBoth`) is now built and verified — the optimized *lowering* onto the
array core exists and agrees with the general one on Sudoku. The remaining deferred piece is item 4: dressing
`module Wfc` (the specialized flat-word store) as the optimized face reached through the same vocabulary + a
`grid + tileset` preset. The array-core lowering proves the write-once discipline; the `Wfc` preset is the
60fps specialization of it.

## 10. Current suggestions

**Codex suggestion, 2026-07-05.** Lock Fork C as the working discipline: one authored model, two lowerings.
But make the first deliverable a small typed vocabulary/IR, not a broad friendly facade. The IR should
force every construct to declare what it can lower to:

- finite domains, named cells, premises, and finite constraint boxes are portable by default;
- rich dataflow relations (`convert`, `sum`, `equal`, intervals/scalars) are friendly-only by construction;
- optimized-only structural constraints are allowed, but must be visible as such at the vocabulary layer;
- consume verbs are capability-gated: `solve` needs search hooks, live edit needs premise retraction, and
  `generate` needs an MRV/min-entropy observation mechanism;
- no vocabulary item should fail later with a hidden "backend does not support this" exception.

That turns Fork A from a global yes/no into a per-verb, per-backend capability matrix. The friendly backend
should still get enough parity for Sudoku-style `solve`/`solutions`; cone-local interactive `Retract` can
wait until the write-side index and premise ceiling are measured/settled.

Suggested build order:

1. Write the concrete F# signatures first: `Domain`, `Cell`, `Premise`, `Constraint`, `Model`, `Lowering`,
   and capability witnesses for friendly/optimized/search/live-edit support.
2. Prove the surface on two slices: Celsius/Fahrenheit as friendly-only dataflow, and Sudoku as portable
   finite CSP. These will expose whether the authoring vocabulary feels honest.
3. Then route WFC through the same finite vocabulary where it naturally fits, with a deliberate optimized
   preset for structural adjacency and entropy search.
4. Keep raw `AddProp`/structural-prop escape hatches, but make them obviously lower-level than the shared
   vocabulary.

The acceptance test for this section: a reader should be able to tell, from the type/signature level, which
face can run a model and which verbs are legal before any engine is constructed.

---

**Fable (Tier 0) review, 2026-07-05.** Summoned to advise on the Codex suggestion and the open forks.
Verdict up front: accept Codex's skeleton (signatures-first IR, capability-gated lowering, the two proof
slices, escape hatches below the vocabulary) with two sharpenings below; lock fork C with its scope stated
precisely; resolve fork A with **no mechanism port**; fork B is **decided here** (it was flagged for
Tier-0) by de-escalating it from contract to representation.

**Fork A — resolved by parity at the verb level, divergence at the mechanism level.** Codex turned A from
a global yes/no into a capability matrix and assumed the friendly backend needs "enough parity" ported up
for `solve`/`solutions`. It needs none. The friendly lowering of the consume verbs uses what `module M`
already has: a guess is an `Assert` under a fresh premise; a backtrack is `Retract` of that premise. That
is **dependency-directed backtracking** — search expressed in the TMS lineage's own mechanism, our native
provenance — and the trail is thereby revealed as the *optimized face's specialization of undo*, not a
prerequisite for search. Costs are honest and acceptable at friendly scale: replay-all `Retract` is
O(network) per backtrack (microseconds at Sudoku size — [benchmarks.md](benchmarks.md) 06-24, M-bit full
propagation ~127µs under fsi), and cell picking is an O(cells) scan per guess — no `OnChange`, no heap, no
write-side index, no cone-local `Retract` on the friendly side at all (replay-all *is* its honest cost).
So for v1: both faces expose all three verbs; the lowerings differ; nothing is ported into `module M`.
Port a mechanism up only when a measured friendly workload demands it. This is D2's "same laws, different
machinery" made operational, and it keeps the D4 boundary clean on both faces — each search driver owns
its own undo (premise-retraction on one face, trail on the other); the engine owns neither.

One coupling to note: DDB consumes one live premise per open guess, so friendly-face search depth shares
the ≤64 budget with user premises. Propagation keeps Sudoku depth shallow, well inside the budget; the
driver must fail loudly on exhaustion, which is exactly fork B's v1 requirement below.

**Fork B — decided: the premise ceiling is a representation decision per backend, not a contract
decision.** The escalation to contract level rested on both faces sharing `uint64` supports; that sharing
is an implementation coincidence, not a law. The contract's obligation is *support is a finite set of
premises* — it says nothing about width, and premise handles already hide allocation, so no vocabulary
construct names `uint64`. Therefore: (i) **v1 keeps one word on both backends**, and premise allocation
**fails loudly** at exhaustion — no silent aliasing; that loud failure is the only v1 work. (ii) The
growth path is mechanical and friendly-face-local: P-word support masks, the same ⌈n/64⌉ trick the
optimized face already plays for tiles — adopted when a measured need appears (a heavy interactive
editor, deep DDB search), not before. (iii) The optimized face keeps one word indefinitely: its premises
are authored constraints only, and multi-word supports would tax the support-OR on every narrow across
250k cells for no user. A model whose premise count exceeds a backend's width is just one more lower-time
capability failure, handled by the same witness machinery as everything else. The fork dissolves — no
contract commitment was ever needed, so there is nothing to defer.

**Fork C — lock write-once, with the commitment scoped precisely.** The commitment is: *portable
constructs are authored once* — not *all constructs are portable*. Codex's lower-time gating is what
makes that honest rather than aspirational: rich relations are friendly-only by declared type, structural
presets optimized-only by declared type, and a model's face-compatibility is decidable before any engine
exists. Falsifier, to watch during the two proof slices: if a portable constraint box starts needing
backend-conditional branches in its *model-level definition* (branches inside its two lowerings are the
design working as intended), the discipline is failing and should be degraded to shared-laws-only
explicitly — not rescued with special cases.

**Two sharpenings of the Codex mechanics.**

1. *Witnesses should be ordinary types, not type-level proofs.* Gate the verbs by the return type of the
   lowering — `General.lower : Model -> Result<GeneralNet, UnsupportedConstruct list>` and
   `Optimized.lower : Model -> Result<OptimizedNet, UnsupportedConstruct list>`, where each net type
   exposes only its legal verbs — and report portability failures as values naming the offending
   constructs. This meets the acceptance test (legality visible before any engine is constructed) through
   plain module + return-type design; phantom-type or SRTP capability encodings would spend the
   friendliness budget on the type system itself.
2. *The Sudoku slice should prove fork A's resolution.* Slice 2 (portable Sudoku) should run `solve`
   through the general DDB lowering — same model, both faces, equal solutions — extending the existing
   differential harness up to the verb level. That one assertion is the whole two-faces claim.

**Two gaps for the vocabulary sketch** (named now so the noun/verb split will not need refitting later;
neither is slice-1 scope).

1. *An observation verb is missing.* AsyncRx was the friendliness north star, and every current verb is
   batch. Reserve `watch cell` (a stream of narrowings/decisions): friendly face as a reads-side
   subscription; the optimized face already has `OnChange` feeding progress streaming.
2. *Runtime edits and structural edits must not conflate.* Edits at runtime (change a given, retract a
   guess) are premise operations on the *lowered net* — exactly what the engine identity provides. Edits
   to structure (new cells, new constraints) are a new model and a re-lower. The vocabulary should state
   this split; the interactive-editing goal (WFC target) lives entirely on the first side.

**Build order:** Codex's 1–4 stand, amended by sharpening 2. B is decided; A and C above are firm
recommendations awaiting Deen's ratification (they were his to lock per §9).

— Claude (Fable 5, Tier 0), 2026-07-05

**Fable (Tier 0) review gate, 2026-07-06 — the lowering/solveBoth build + Codex guardrail pass.**
Verdict up front: ACCEPTED, with one locked defect that must be fixed before this file is trusted on any
instance harder than the 4×4. The build implements the locked rules as written — solveBoth is harness-only
(§7.8), witnesses are ordinary return types, one shared `Gac.narrow` is the write-once heart, and the Codex
negative rows genuinely close the silent-drop holes they name.

*The locked defect — DDB premise leak.* In both faces' `run` loops, every guess **attempt** allocates a
fresh premise id (`let p = gp in gp <- gp + 1`) and the `Retract p` on failure never reclaims it. Fork A's
ratified coupling is one live premise per **open guess**; the preflight bound
`maxPremisesNeeded = givens + cells` is correct for that coupling but not for this implementation, which
consumes ids monotonically across the whole search tree. On the optimized face, `ArrayCore.Assert` throws
at id > 63 — so any instance whose search exceeds ~64 − givens total attempts **passes the
`PremiseWidthExceeded` preflight and then crashes at runtime**. It is invisible today precisely because the
8-given 4×4 reaches the GAC fixpoint with zero guesses: every harness row passes without ever entering the
guess branch. The unsound bound and the untested-DDB-path gap are the same hole.

*Fix, locked — implement as written, don't relitigate.* Restore LIFO premise reuse in both faces' `tryV`:
after `Retract p`, reset the counter to `p` before trying the next value. Soundness by induction on the
search tree: any deeper guesses inside a failed `go ()` were already retracted and reclaimed, so the counter
is back to `p + 1` when `go ()` returns false; retract-then-reset returns it to `p`. Live premises are then
exactly givens + open guesses ≤ givens + cells, which is the preflight bound. Apply to the general face too
— its int premises cannot overflow, but fork A demands the same shape on both faces.

*Mandated oracle — the fix does not land without it.* Add a third differential slice: the same 4×4 with a
**sparse givens set** that provably cannot finish at the GAC fixpoint. The harness must assert (a) after
initial propagation at least one cell is still non-singleton — this proves the guess/backtrack path actually
runs; (b) both faces return `Some` and agree; (c) the givens set is **uniquely solvable, verified in-harness
by brute-force enumeration** (a 4×4 is trivially enumerable). Without (c), solution equality holds only by
the accident of identical cell-pick order, and a future min-entropy picker on the optimized face would
legally break the test. What the oracle may NOT compare: guess counts, premise ids, or narrowing order
between the faces — pick order is an artifact, not a law; only final solutions and lowering witnesses are
comparable.

*Minor, executor's discretion (no gate; resolved 2026-07-10):* the original `Author.finite` accepted
duplicate values — the optimized `idx` map silently kept the last index while the general face deduped via
`Set`; reject duplicates at authoring or preflight. `Differential.agree` was dead code (the harness
re-derived it inline) — use it or drop it. The comment above `maxPremisesNeeded` ("at most one live guess
per cell") states the invariant the locked fix makes true; keep it. Resolution: `Domain.finite` now rejects
duplicates at authoring time with a negative harness row, and the unused `Differential.agree` wrapper was
removed; test-only `Differential.solveBoth` remains the sole primitive.

— Claude (Fable 5, Tier 0), 2026-07-06

**Codex execution note, 2026-07-10 — gate closed.** Both `tryV` loops now reclaim a failed guess id in
LIFO order: `Retract p` is followed by `gp <- p` before the next value is attempted. This makes the
runtime allocation discipline match the already-documented `givens + cells` preflight bound on both
faces.

The mandated third slice is now executable, with one encoding detail made explicit. Exact four-cell
`allDifferent` GAC, and the ordinary uniquely solvable sparse pairwise clue candidates tested while
building the slice, closed the tiny 4×4 at propagation — they could not exercise DDB. The final slice
therefore uses the same 4×4 Sudoku units as pairwise portable `not-equal` relations, five ordinary clues,
and one portable four-cell symmetry breaker. The five clues alone have two completions; the symmetry
breaker excludes the lexicographically first rectangle orientation without pruning the four ambiguous
cell domains at the initial fixpoint. Under the current deterministic value order this also forces a
failed first guess and `Retract` before success.

The harness checks the required semantics without exposing test statistics through the surface: a
finite-relation fixpoint evaluator reports four non-singleton cells; an independent exhaustive assignment
enumerator (which does not call `Gac.narrow` or either engine) finds exactly one authored-model solution;
and `Differential.solveBoth` returns `Some` from both faces, with both final solutions equal to that unique
oracle. Guess counts, premise ids, and narrowing order are not compared.

**Fable (Tier 0) countersign, 2026-07-10 — gate CLOSED.** Re-read both `tryV` loops and re-ran
`dotnet fsi propagator-surface-vocab.fsx` independently: LIFO reclaim (`gp <- p` after `Retract p`) is
implemented as written on both faces; slice 3 reports 4 open cells at the initial fixpoint, exactly one
brute-forced solution, and both faces agreeing with it; all guardrail rows pass. The pairwise-not-equal
encoding + symmetry breaker is an accepted deviation from the naive reading of the order — the mandate was
the *properties* (fixpoint-incomplete, unique, brute-verified, no incidental-order comparison), and exact
all-different GAC provably closes every sparse 4×4 at propagation, so a weaker portable encoding was the
only way to satisfy them; Codex documented the reasoning rather than silently improvising, which is
exactly what the gate exists to check. The file is now trusted on instances that require search, within
the preflighted `givens + cells ≤ 64` bound. The two non-gating cleanup notes were resolved in the
2026-07-10 vocabulary pass below.

**Codex vocabulary cleanup, 2026-07-10.** Removed the catch-all `Author` module. Constructors now live
with their nouns: `Domain.finite` / `Domain.lattice`, `Cell.create`, and `Constraint.relation` /
`dataflow` / `structural`. The write-once artifact is now `Model<'a>` rather than `Spec<'a>` and is assembled
directly as a record; a one-function `Model.create` module was deliberately avoided. In the single-face
friendly prototype, `Network.scalar` / `interval` / `finite` moved to the `Domain` companion module while
the live `Network` type remains the running engine. The optional consumer conversion helper remains, renamed
from the collision-prone `Convert` module to `Transform`; the network operation is still `net.Convert`.
`Domain.finite` now owns the unique-values invariant, and dead `Differential.agree` was removed while
test-only `Differential.solveBoth` remains in the harness.

**Codex representation and core-boundary pass, 2026-07-10.** General finite lowering no longer fixes its
engine state to `Set<'a>`. `FiniteRep<'value,'state>` now records exactly the operations lowering and DDB
need (`top`, conversion, singleton construction, meet, ordered candidate projection, bottom/singleton tests,
and equality), and `General.lowerWith` accepts a domain-bound representation factory. The state type remains
inside the captured run closure. `FiniteRep.set` is the friendly default layered on this contract, preserving
the authored domain order when projecting candidates; `General.lower` is only its convenience wrapper. A
harness-only list representation proves the generic path is real.

Optimized's value codec now precomputes ordered `(value, bit)` metadata and a read-only-after-construction
`Dictionary<'value,uint64>` for value-to-bit lookup. Decoding walks the ordered metadata, so authored order
is retained. This dictionary is immutable codec metadata, not domain/search state: DFS backtracking still
lives in engine assertions/retractions, and future interleaving-state costs remain an independent search
strategy concern.

The core boundary is now explicit and enforced in the vocabulary: this is a pure propagator/constraint
library. The placeholder structural box, DU case, constructor, witnesses, lowering branches, and guard rows
were removed; the deferred `Collapsed` event was renamed `Resolved`. Domain-specific topology, adjacency,
selection heuristics, and application presets belong outside the library core. Both scripts pass after the
change, including the sparse DDB oracle and the custom-representation row.
An additional regression row authors `[2; 1]` and verifies that both faces select `2`, locking candidate
order independently of Set comparison order or dictionary enumeration order. That row also exposed and
closed an optimized lifecycle gap: lowering now seals the array core after construction, so a model with no
givens initializes its read arrays before DFS instead of depending on the first assertion to do so.

**Fable (Tier 0) review, 2026-07-10 — second gate, ACCEPTED.** Covers the two Codex passes above plus the
history restoration. Verified independently: both scripts pass under fsi; the LIFO premise discipline
survived the `FiniteRep` refactor on both faces. Three judgments:

*The authored-order rows fixed a real latent divergence, not a cosmetic one.* Before this pass the general
face enumerated candidates in `Set` comparison order while the optimized face decoded in authored bit
order — the two faces would have disagreed on any under-constrained model, invisible because every harness
slice had a unique solution. Authored-order projection on both faces makes cross-face agreement a theorem
of identical search trees rather than an accident. Consequence, now a law: **`solve` is deterministic with
value order = authored domain order on both faces.** A future stochastic or heuristic *value* picker must
arrive as a strategy parameter or through `generate` — changing `solve`'s value order silently would break
the `[2; 1]` row, and that row is correct to break it.

*The Structural removal is accepted as a boundary decision, recorded properly.* It supersedes the 07-05
"optimized-only structural constraints are allowed" suggestion (preserved verbatim above, per AGENTS.md)
and §9 item 4's `grid + tileset` preset plan: `module Wfc` is not dressed *inside* the vocabulary; WFC
consumes the vocabulary from an application layer. Note the doc now carries two distinct boundary axes —
§12's dependency line (pure F# vs `#r`) and this library-vs-application line (rep-agnostic constraint
machinery vs domain topology/heuristics). They are compatible but not the same test; WFC adjacency is pure
F# yet application-layer.

*Provenance incident, closed.* An earlier version of this pass rewrote dated 07-05/07-10 entries in place
to match the new boundary (caught by transcript comparison; the file predates git tracking). Restored
verbatim, superseding decisions moved into the dated entries above, and the repo-level AGENTS.md
append-only-history rule now guards the class. Minor, executor's discretion: the engine headers still say
"reproduced verbatim," but `Closure.Lattice` gained `equals` (dropping the `'a : equality` constraint) and
`ArrayCore` gained `Seal` — either update the comments to name the deltas, or upstream `Seal` to
`propagator-mutable-core.fsx`'s `module M` so the copies re-converge.

— Claude (Fable 5, Tier 0), 2026-07-10

## 11. Dated history

- **2026-07-05 - Codex.** Added the current suggestion above: provisionally accept write-once authoring,
  express backend support through typed lowering/capability witnesses, and build the first slice from
  concrete signatures plus Celsius/Fahrenheit and Sudoku before expanding the broader surface.
- **2026-07-05 - Fable (Tier 0).** Reviewed the Codex suggestion and the forks (§10): fork **B decided**
  — ceiling de-escalated from contract to per-backend representation, loud allocation failure in v1,
  growth path friendly-face-local; fork **A resolved** (pending ratification) — verb-level parity with
  no mechanism port, via dependency-directed backtracking on `module M` as-is; fork **C recommended
  locked** with the commitment scoped ("portable constructs authored once") plus a falsifier. Two
  sharpenings of the Codex mechanics (witnesses as ordinary lowering return types, not type-level
  proofs; the Sudoku slice must prove fork A differentially at the verb level) and two vocabulary gaps
  flagged (`watch` observation verb; runtime-vs-structural edit split).
- **2026-07-05 - Deen + Opus.** Deen ratified forks **A** and **C** (the two that were his to lock per
  §7). Opus built the slice-0 vocabulary skeleton [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx)
  (Codex build-order step 1, Fable sharpening 1): nouns/spine/verbs, the two lowerings as
  `Result<_, UnsupportedConstruct list>`, `Differential.solveBoth` for the fork-A proof, and both
  proof-slice models authored compile-only. Type-checks under fsi. Next: fill the slice bodies (§9.3).
- **2026-07-05 - Deen + Opus (boundary + observation).** Fixed the core/outer boundary at the
  *dependency line* (§12): pure-F# = core (both lowerings + witnesses included), `#r`-bearing = outer,
  the line cutting *across* the faces. Observation split in twain — the core exposes the `OnChange`
  callback hook (`Observe`, returning `IDisposable`, dependency-free); the `AsyncObservable` wrap is a
  single generic outer adapter (`ObserveRx.ofCallback`) that composes with zero glue. `CellChange`
  supersedes the skeleton's `Narrowing`; skeleton updated to match.
- **2026-07-06 - Deen + Opus (friendly UX shape).** Reframed the task (§13): the immediate deliverable is a
  working single-face friendly UX over the closure `Engine<'a>`, proven by re-authoring Celsius/Fahrenheit,
  the barometer, and Sudoku in it — not the two-lowering differential (deferred with the optimized face).
  Shape agreed: **functions + methods, not a custom-operation CE** (Deen overriding A4's CE default here),
  domain-shaped vocabulary (`Convert` / `Combine` / `AllDifferent` + `Assert` / `Retract` / `Value` /
  `Solve`), named premises, pre-made lattice constructors, plus the `network { }` monad (Option C) as a
  second entry over the same core. Conversion helpers (`scale` / `shift` / `affine`, `Affine` auto-invert)
  are **external / supplied by the consumer, not core** — the surface defines only the invertible seam that
  accepts them.
- **2026-07-06 - Opus (friendly UX BUILT + verified).** [propagator-friendly.fsx](../propagator-friendly.fsx)
  — pure F#, no `#r`. `Network` constructors + `Convert`/`Combine`/`AllDifferent` + `Assert`/`Retract`/
  `Value`/`Solve` + the `network { }` monad, over the verbatim `Engine<'a>`. Key as-built piece: a
  `Domain<'a,'p>` descriptor whose `'p` is the bare payload, so users compute on plain numbers/ranges and the
  lattice wrapper never surfaces. The interval C↔F (monad), barometer (all four stages), and Sudoku reproduce
  §4/§5/the tutorial bit-exact; raw scalar C↔F reproduces §1's spurious `C = BOT`. Finding: the supplied
  `Affine` helper's fused arithmetic round-trips to `C = 1` (dodging §1's Bot) — a rounding change, not just
  ergonomics; kept as a labelled second run. See §13 status for the full delta list.
- **2026-07-06 - Deen + Opus (lowering + solveBoth BUILT + verified; harness placement fixed).** Decided
  (Deen): the differential/equivalence machinery `Differential.solveBoth` is a **test-harness** concern, not
  part of the shipped surface (§7.8) — a consumer picks a face and never calls it. Then filled the skeleton:
  [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) is now runnable. Both engines are copied in
  verbatim as private backends (the closure `Engine<'a>` = general; `module M`'s array core = optimized).
  `General.lower` / `Optimized.lower` build a real engine each, returning `Ok net` or `Error witnesses`;
  `solve` is propagate-to-fixpoint + DDB (same shape both faces — fork A). **The write-once heart is one shared
  `Gac.narrow`** interpreting a `RelationBox`'s `allows` predicate — authored once, plugged into both reps
  (general `Set<'a>`, optimized `uint64` word) — so identical narrowing gives an identical fixpoint.
  `Differential.solveBoth` lowers the 4×4 Sudoku both ways; **verified under fsi**: both faces reach the known
  solution and agree bit-for-bit. The C↔F slice stays general-only — `Optimized.lower` returns
  `Error [RichLatticeOnOptimized; DataflowOnOptimized]` (capability visible pre-engine), while `General.lower`
  settles it to F = [33.8, 33.8]. As-built deltas: `Model` includes `givens` (a CSP instance's clues drive
  propagation); `LatticeOps` gained `top` (the closure engine initializes cells to it). No trail / min-entropy
  heap — propagation + DDB solves the 4×4, so the slice needs neither; they stay the deferred optimized
  specialization. Still stubbed (the deferred observation/edit slice): `solutions` / `generate` / `onCell` /
  `onNet` / `assume` / `retract`.
- **2026-07-06 - Codex (review fixes 1-3 implemented).** Tightened
  [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) after review. The lowerings now preflight
  every construct/domain combination and return witnesses instead of silently dropping anything:
  `StructuralOnGeneral`, `StructuralPresetNotImplemented`, `RelationRequiresFiniteDomain`, and
  `DataflowRequiresRichLattice`. The optimized proof backend also refuses one-word aliasing up front:
  `FiniteDomainTooWideForOptimized(needed, 64)` for >64 finite values and
  `PremiseWidthExceeded(needed, 64)` for the conservative DDB bound (`givens + cells`) exceeding the
  support word. The private array-core copy also guards `Assert`/`Retract` against out-of-range premise ids.
  Negative harness rows now verify structural constraints are not ignored, finite/dataflow and
  lattice/relation mixes fail loudly, a 65-value optimized domain is rejected, and a 65-cell DDB bound is
  rejected. Re-verified under fsi: `propagator-surface-vocab.fsx` passes with the guardrail rows, and
  `propagator-friendly.fsx` still passes its UX examples.
- **2026-07-06 - Fable (Tier 0) review gate.** Accepted the lowering/solveBoth build and the Codex
  guardrail pass; locked ONE defect (§10): the DDB loops leak a premise id per guess *attempt* (never
  reclaimed on `Retract`), so the `PremiseWidthExceeded` bound (`givens + cells`, correct for fork A's
  one-live-premise-per-open-guess coupling) is unsound against the as-built code — an instance needing
  >64−givens total attempts passes preflight then throws in `ArrayCore.Assert`. Invisible to the current
  harness because the 8-given 4×4 solves with zero guesses (the untested-DDB-path gap and the unsound
  bound are the same hole). Work order in §10: LIFO premise reuse in both faces' `tryV` (locked, with the
  induction argument), plus a mandated sparse-givens differential slice that forces the guess path —
  uniqueness brute-verified in-harness; the oracle may not compare guess counts / premise ids / narrowing
  order, only final solutions and witnesses.
- **2026-07-10 - Codex (DDB review gate implemented + verified).** Fixed the premise-id leak in both
  lowerings by restoring `gp <- p` after every failed guess is retracted, making the runtime search obey
  the `givens + open guesses <= givens + cells` invariant. Added the third portable 4×4 differential slice:
  pairwise Sudoku units, five givens, and a nonlocal symmetry breaker leave four cells open after initial
  GAC while defining exactly one solution. The in-file oracle independently exhausts the authored
  relations, proves uniqueness, then checks both faces return and agree with that sole solution. Verified
  with `dotnet fsi propagator-surface-vocab.fsx`: all three slices and all capability/width guardrails pass.
- **2026-07-10 - Codex (vocabulary consolidated).** Replaced `Author` with companion modules `Domain`,
  `Cell`, and `Constraint`; renamed `Spec<'a>` to the directly assembled `Model<'a>`; moved friendly
  `Network.*` factories under `Domain`; and retained the supplied affine helpers as `Transform`. Closed the
  two remaining review notes by rejecting duplicate finite-domain values at authoring time (with a negative
  harness row) and removing dead `Differential.agree`. Re-ran both surface scripts successfully.
- **2026-07-10 - Codex (generic finite representation + pure-core boundary).** Added
  `FiniteRep<'value,'state>` and `General.lowerWith`; retained `General.lower` as the Set-default wrapper and
  verified the generic path with a list-backed harness representation. Replaced Optimized's `Map` codec index
  with ordered `(value, bit)` metadata plus a read-only-after-build `Dictionary` lookup. Removed the
  structural-preset placeholder and its witnesses/tests from the library vocabulary, renamed the deferred
  `Collapsed` event to `Resolved`, and recorded that domain-specialized topology and search policy remain
  outside the pure propagator/constraint core. Added a cross-face `[2; 1]` regression for authored candidate
  order, explicitly sealed the optimized array core so zero-given models initialize before search, and re-ran
  both F# scripts successfully.
- **2026-07-10 - Fable (Tier 0) second gate.** Accepted both 07-10 Codex passes (§10): LIFO discipline
  survived the `FiniteRep` refactor; the authored-order rows fixed a real latent cross-face divergence
  (general Set order vs optimized bit order on under-constrained models) and promote deterministic
  authored-order `solve` to a cross-face law; the Structural removal is a properly superseding boundary
  decision (WFC consumes the vocabulary from the application layer; §12's dependency line and the new
  library-vs-application line are distinct axes). A history-rewrite incident (dated entries edited in
  place to match the new boundary) was caught, restored verbatim, and fenced by the repo AGENTS.md
  append-only rule. Independently re-verified: both scripts pass under fsi.

## 12. The dependency boundary and the observation split

The core/outer line is drawn by *external dependency*, not by conceptual weight: if a construct needs a
`#r` it is an outer layer; if it is pure F# it is core. The test is mechanical (grep `#r`), and the line
cuts *across* the friendly/optimized faces rather than between them:

- **Both lowerings + the capability witnesses are core** — `Domain` / `Cell` / `Premise` / `Constraint`
  / `Model`, `General.lower`, `Optimized.lower`, and `Result<_, UnsupportedConstruct list>` are all pure
  F#. (This retracts the earlier notion that the portability apparatus was "outer.")
- **The friendly lattices split.** `float` / `decimal` and the self-contained interval (BitDecrement /
  Increment, no dep) are core; `BigRational` (MathNet) and rigorous IntSharpCore intervals are outer,
  plugging into the core `Lattice` extension point (meet + isBottom).
- **Search splits.** Trail, min-entropy heap, and the `OnChange` callback are core mechanism; a plain
  DFS `solve` is core; the Hansei-backed strategy drivers (`#r Hansei.Core.dll`) are outer plug-ins onto
  the trail.
- **Observation is the clean outer layer.** AsyncRx / Hopac is a real `#r`.

The pattern: the core owns the seams — the `Lattice` hook, the `OnChange` callback, the lowering targets
— and each outer layer brings exactly one dependency and plugs in.

**Observation, split in twain.** The mistake was giving the core an `AsyncObservable`-returning `watch`
verb; internally there is no observable, only callbacks. So expose the callback and remove only the Rx
*type* — write it as it already is internally, the `OnChange` hook:

```fsharp
// core (no #r): OnChange made public. Handler fired synchronously on each change, mid-propagation.
module Observe =
    val onCell : Net -> Cell<'a> -> (CellChange<'a> -> unit) -> System.IDisposable
    val onNet  : Net             -> (CellChange<'a> -> unit) -> System.IDisposable

// outer (#r AsyncRxHopac): one generic adapter, knows nothing about cells or the engine.
module ObserveRx =
    val ofCallback : (('e -> unit) -> System.IDisposable) -> AsyncObservable<'e>
```

They compose with zero glue, because the curried core primitive already *is* a callback source:

```fsharp
ObserveRx.ofCallback (Observe.onCell net cell)   // : AsyncObservable<CellChange<'a>>
```

`IDisposable` and the `CellChange` event DU are plain BCL / F#, so the core stays `#r`-free. The one
thing the core must *specify* (not implement) for the wrap to stay efficient is the handler's calling
contract: **synchronous, on the engine thread, mid-propagation** — the zero-alloc fast path where the
handler *is* the observer's push. Buffering, backpressure, and scheduling for a slow subscriber are the
wrapper's job in the outer layer; the core never pays for them. Exposing the hook also serves non-Rx
consumers directly — a test asserting on the change sequence, a Godot consumer pushing to a `TileMap`, a
logger — none of which touch AsyncRx.

## 13. The friendly UX — functions + methods, helpers supplied

**Reframe (Deen, 2026-07-06).** The task ahead is not the two-lowering equivalence proof (§9.3–4); it is a
working **single-face friendly UX**, and its proof is **re-authoring the existing worked examples in it** —
Celsius/Fahrenheit, the barometer, and 4×4 Sudoku. The ports *are* the acceptance test: if they read better
than the raw closure wiring and reproduce the same outputs, the surface is honest. The optimized face and
`Differential.solveBoth` wait behind this.

**Substrate.** The closure `Engine<'a>` of [propagator-number-types.fsx](../propagator-number-types.fsx)
(reused verbatim from [tutorial-propagation-part1.fsx](../tutorial-propagation-part1.fsx)) — it already *is*
the friendly face's character: clear rep, rich lattices, premises + `Retract` built in, and it runs all
three examples today. Sudoku's `Solve` rides its `Assert`/`Retract` as dependency-directed backtracking —
fork A's mechanism (§10), on this engine as-is. `module M` / DDB-at-scale is the deferred optimized concern.

**Form: functions + methods, not a custom-operation CE** (Deen's call, overriding A4's CE default for this
surface). Methods on the `net` object keep `.`-discoverability without the CE's restriction that custom
operations don't mix with `for` — so the bulk case (Sudoku's twelve `AllDifferent` groups) is a plain
`for grp in rows @ cols @ boxes do net.AllDifferent grp`, the one place the CE was awkward.

The surface removes the four friction points of the raw closure wiring
([propagator-number-types.fsx:165](../propagator-number-types.fsx) for the C↔F propagator,
[:583](../propagator-number-types.fsx) for the barometer fan-in): the `Top`/`Empty` guard, the
`{ value; support }` contribution literal, the manual support threading (`support = cC.support`, and the
fan-in's `union3 …`), and the trailing `AddProp … |> ignore`. Premises become **names**, not the demo's
`STOPWATCH = 1` integer constants.

```fsharp
// Pre-made lattices; cells let-bound; relations + drivers are methods.
let net = Domain.interval ()
let t, h = net.Cell "fall-time", net.Cell "height"

net.Convert(t, h, heightFromFall, fallFromHeight)            // two-way relation
net.Combine([bh; bShadow; bldShadow], h, heightFromShadow)   // fan-in; support auto-unioned
net.Assert("stopwatch", t, Iv(3.0, 3.2))                     // premise = a name
net.Retract "shadows"                                        // dependency-directed
net.Value h

// finite + search; bulk wiring is a plain loop (no CE restriction):
for grp in rows @ cols @ boxes do net.AllDifferent grp
net.Solve()                                                  // Some solution (DDB)
```

**Plus the monadic block (Option C), same core.** For a self-contained value with cells bound inside:

```fsharp
let cf = network (Domain.scalar<float> ()) {
    let! c = cell "celsius"
    let! f = cell "fahrenheit"
    do! convert c f fwd bwd           // same op as net.Convert, as a do!-step
    do! assume c 1.0
    return! read c
}
```

Two entry points — direct methods, and the `network { }` monad — over one engine.

**Conversion helpers are external / supplied, not core (Deen, 2026-07-06).** `net.Convert` takes general
`fwd`/`bwd` functions; a supplied *invertible* datum (one carrying its own inverse) may stand in for the
pair so an affine relation is authored once. But the affine sugar itself —

```fsharp
// SUPPLIED by the consumer / `Transform` helper module — NOT part of the core surface:
let scale k = fun x -> x * k
let shift b = fun x -> x + b
let affine k b = scale k >> shift b
type Affine =
    { k: float; b: float }
    member a.Apply x = a.k * x + a.b
    member a.Inverse = { k = 1.0/a.k; b = -a.b/a.k }
```

— lives outside the core. Arithmetic is domain plumbing the caller brings; the surface only defines the
seam that accepts it. This is §12's dependency-line discipline applied to conversions: the core owns the
`Convert` / invertible seam, the consumer supplies the `Transform` module.

**Rejected here:** the custom-operation CE (both Option A-as-CE and the Option B `edge`-primitive CE) — set
aside in favor of methods; and baking the affine helpers into the core.

**Status: BUILT + verified 2026-07-06 (Opus) — [propagator-friendly.fsx](../propagator-friendly.fsx), pure
F#, no `#r` (core per §12).** The domain factories (`Domain.scalar<'n>` / `interval` / `finite`), the
`Convert` / `Combine` / `AllDifferent` + `Assert` / `Retract` / `Value` / `Solve` methods, the invertible
seam, and the `network { }` monad are all in and the three examples run.

*As-built deltas from the sketch above:*
- A **`Domain<'a,'p>` descriptor** carries the lattice plus `payload : 'a -> 'p option` (None = uninformative,
  hides the Top/Empty guard), `inject : 'p -> 'a`, `isBottom`, and an optional `FiniteOps<'a>`. The `'p` is
  the **bare payload** the user computes on — `'n` for scalar, `Interval` for interval, `Set<int>` for finite
  — so `Convert`'s `fwd`/`bwd` and `Assert` take plain numbers/ranges and the `Val` wrapper never surfaces
  (`net.Assert(cC, 1.0)`, not `Val 1.0`). This is what actually retires friction points 1–3; it was implicit
  in "domain-shaped" but is the load-bearing piece.
- `net.Cell "name"` is the cell constructor (was omitted from the method list); names feed provenance display
  (`net.ShowSupport`).
- Monad ops live in **`module Ops`** (`Ops.cell` / `Ops.convert` / `Ops.given` / `Ops.assume` / …), qualified
  per the style guide (no `[<AutoOpen>]`), with `assume name c v` (retractable) vs `given c v` (anonymous,
  permanent) splitting the two `Assert` overloads. `network net { … }` threads one `Network` as a state monad.
- `AllDifferent` / `Solve` are finite-only (`failwith` on a non-finite domain — a misuse, not a supported
  path). `Solve` is the fork-A DDB driver (guess = `Assert` fresh premise, backtrack = `Retract`); on the
  easy 4×4 propagation finishes first, so it returns `true` having guessed nothing.

*Fidelity:* the interval C↔F (monad), the barometer (all four stages, incl. provenance `{stopwatch, shadows,
super}` and the √10 = 3.16228 backward-refinement payoff), and the Sudoku solution reproduce §4/§5/the
tutorial **bit-exact**. The raw-conversion scalar C↔F reproduces §1's spurious `C = BOT`.

*One honest finding.* Routing the scalar C↔F through the **supplied `Affine` helper** (fused `k*x+b` +
algebraic inverse) instead of the split `(y-32)*5/9` changes the float rounding: the round trip lands back on
`1.0` exactly, so the surface reports `C = 1` rather than §1's fabricated Bot. The helper changed the
*arithmetic*, not just the ergonomics — on-thesis for propagator-number-types' closure/enclosure point, and
recorded in the port as a labelled second run rather than hidden. Next: the deferred optimized face +
`Differential.solveBoth`.

The event payload `CellChange<'dom>` is a DU (narrowed / restored / collapsed / contradiction — not a
bare `before → after`, because DDB and the trail *widen* on backtrack and interactive edit wants
restores), carrying the rep-agnostic domain projection (remaining candidates), not the resolved singleton
`'a` and not the backend rep. The exact projection type is the one open payload detail, deferred to the
observation slice.
