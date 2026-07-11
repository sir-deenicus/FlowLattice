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

**Update 2026-07-10:** item 4's "dress `module Wfc` inside the vocabulary + preset" phrasing is superseded
by the second-gate boundary decision (§10): WFC is an *application layer* consuming the vocabulary from
outside. The concrete next slice is the 2026-07-10 work order in §10 — `generate` + observation seam in the
core, WFC authored in a new section of [propagator-wfc.fsx](../propagator-wfc.fsx) via `#load`.

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

**Fable (Tier 0) work order, 2026-07-10 — third slice: `generate` + observation seam + the WFC
application layer.** One pass, three deliverables, executed together. Locked rules are marked; everything
else is executor's discretion — the executor is trusted to fill mechanism, so this names only the
load-bearing structure and the places a reasonable implementation goes silently wrong.

*Deliverable 1 — `generate` on both faces ([propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx)).*

- **Locked, the verb contract split:** `solve` stays exactly as it is — complete DDB, deterministic,
  value order = authored order (the `[2;1]` row and the sparse slice are canaries; neither may change).
  `generate` is the stochastic sampler: propagate to fixpoint → pick the open cell with fewest candidates
  (ties by `model.cells` order) → draw a random candidate from the authored-order list → collapse →
  repeat; on contradiction, restart the whole sample; loud failure past a bounded restart count. No
  interleaving with DDB.
- **Locked, the premise invariant:** `generate`'s collapses consume **no premises** — they are never
  retracted individually; contradiction aborts the sample. Live premises during `generate` = givens only,
  independent of grid size. This is what frees `generate` from the `givens + cells ≤ 64` bound while the
  optimized face keeps one support word (fork B). Mechanism — a shared axiom premise, a premise-free
  write, or rebuild-per-restart — is the executor's choice; the invariant is not.
- **Locked, the preflight relocation:** `PremiseWidthExceeded` moves from `Optimized.lower` to
  `Optimized.solve` (loud, pre-search). Lowering keeps only what lowering itself needs: domain ≤ 64
  values, and givens ≤ 64 (lowering asserts givens under ids `0..`). Without this move a 250k-cell model
  cannot even be lowered for `generate` — the bound is a *solve* capability, not a model capability. The
  general face has nothing to move (int ids).
- **Locked, RNG discipline:** the PRNG is an explicit pure implementation in core (e.g. SplitMix64),
  seeded via a strategy parameter — not `System.Random` (the seed is part of the map artifact's contract;
  reproducibility must not depend on runtime version). Draws are a function of visible state only
  (authored-order candidate lists, `model.cells` order), never of rep internals. Consequence, and the
  acceptance theorem: **same model + same seed ⇒ both faces generate the identical map** — the
  differential harness extended to `generate`. Uniform draws for this slice; tile *weights* are a
  deferred strategy extension, not built now.
- Strategy record shape and the `generate` signature are executor's discretion (the stubs were never a
  compatibility surface).

*Deliverable 2 — `onCell` / `onNet` on both faces.* §12 already fixes the calling contract (synchronous,
engine thread, mid-propagation, `IDisposable`, buffering is the outer wrapper's job). Two things it left
open, now decided:

- **Payload:** `CellChange<'a>` carries current candidates as `'a list` in authored order; `Resolved`
  carries the value (as the DU already has it).
- **Locked, the replay law:** folding the emitted event stream reproduces the net's final candidate
  state — through search, backtrack, and restart (`Restored` fires on every widening). This is the whole
  streaming-consumer contract: paint on `Resolved`, un-paint on `Restored`, never read the net. Event
  volume under replay-all retraction is the general face's honest cost, not something to filter in core.
- Both private engine copies will need an `OnChange` seam, which settles the gate-2 minor: the
  "reproduced verbatim" headers are already false (`equals`, `Seal`) — either update the headers to name
  every delta or upstream the deltas so the copies re-converge. Headers must stop lying either way.

*Deliverable 3 — the WFC application layer ([propagator-wfc.fsx](../propagator-wfc.fsx), new literate
section).*

- **Locked, placement:** a new section of `propagator-wfc.fsx` — the WFC story file, where the
  vocabulary-authored version sits next to the hand-built engine it retells — consuming the vocabulary
  via `#load "propagator-surface-vocab.fsx"`. **Do not copy** the vocabulary or engines into the app
  section; guarding the vocab file's harness against noisy `#load` is fine, forking it is not. The file
  split is the ratified library-vs-application boundary made visible: grid topology, adjacency tables,
  tileset — all here, nothing WFC-shaped in the vocab file.
- Content: a small under-constrained tileset (≥3 tiles, e.g. sea/coast/land), a grid helper producing
  cells + pairwise 4-neighbor `Constraint.relation` boxes, `generate` on the optimized face, streamed
  through `onCell`.
- **Locked, the oracles:** (a) post-hoc independent map check — every adjacent pair satisfies the
  compatibility table and every cell is resolved, verified by walking the output grid with no engine
  involved; (b) the cross-face seed test at small size (e.g. 8×8): same seed, both faces, identical
  maps; (c) a replay check: the `onCell`/`onNet` stream folds to the final grid; (d) a
  contradiction-capable tileset row that forces restarts and proves the bounded-restart loud failure.
- Benchmarks: dated table in [benchmarks.md](benchmarks.md) (env block; correctness verified before
  timing; fsi ⇒ trust ratios not absolutes): `generate` at 16×16 / 32×32 / 64×64, larger if fsi
  tolerates. **Honest framing, locked:** this lowering (closure props, list-based `Gac.narrow` per edge)
  is *not* the 60fps path — `module Wfc`'s structural store remains that; these numbers establish the
  vocabulary's cost curve and inform whether an app-layer structural fast path is ever needed. Do not
  optimize `Gac.narrow`'s per-edge enumeration inside the core to chase these numbers.

*Attention list (subtle, not extra rules):* restart must re-assert givens (or rebuild the engine — then
subscription disposal across rebuilds must not leak); `onCell` during search observes speculative
narrowing, which is the honest contract; binary adjacency through `Gac.narrow` enumerates |dom|² tuples
per edge per pass — acceptable at demo scale, an app-layer problem beyond it; Godot glue is out of scope,
the app section proves the stream contract with a plain consumer; `solutions`, `assume`, `retract` stay
stubbed — nothing in this slice forces them.

*Acceptance:* every existing harness row passes unchanged; the new rows above pass; both surface scripts
and `propagator-wfc.fsx` run clean under fsi; §11 history appended (append-only per AGENTS.md);
benchmarks entry landed.

— Claude (Fable 5, Tier 0), 2026-07-10

**Codex suggestion, 2026-07-10 - canonical core and dependent friendly syntax.** Treat
[propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) as the canonical load-safe core despite
its provisional filename. Keep the library implementation in one place: vocabulary, the reusable
dependency-free `Interval`, GAC, both private engines, and the `General` / `Optimized` faces. Move
`Differential`, proof slices, fixtures, independent oracles, and the executable runner to
[propagator-surface-vocab.tests.fsx](../propagator-surface-vocab.tests.fsx), which loads the core.

[propagator-friendly.fsx](../propagator-friendly.fsx) is now a dependent authoring facade: it loads the
same core, accumulates a `Model`, and invokes `General`; it owns no engine, finite representation, search
driver, or duplicate core domain. Named friendly assumptions are removed from the authored model on
`Retract`, and the next read rebuilds from the remaining model. Until the deferred core observation/read
seam lands, a contradictory `General.solve` exposes only model-level `None`, so the facade cannot inspect
an otherwise unaffected cell inside that contradictory net. Do not bypass that boundary by reaching into
the private closure engine. `Interval` remains core and now overloads arithmetic between intervals and
float constants, allowing ordinary expressions such as `interval * 9.0 / 5.0 + 32.0`.

`propagator-mutable-core.fsx` remains a historical benchmark reproducer, not a library dependency or a
fourth architectural layer. The intended active shape is three scripts: canonical core, its test harness,
and friendly syntax over the core; eventual DLL packaging may replace the script loading without changing
those ownership boundaries.

**Codex superseding suggestion, 2026-07-10 - live General core and syntax-only friendly facade.** Reject
the rebuild-on-read facade described in the preceding suggestion. It changed the original observable laws:
one contradictory cell turned every friendly read into model-level failure, unaffected cells could no longer
be inspected, named retraction rebuilt the model, provenance disappeared, and finite reads stopped returning
the friendly Set shape. A process exit of zero did not make those behavioral regressions acceptable.

Retain one live closure engine behind `GeneralNet`. The core now owns cell creation and constraint
installation plus representation-independent `CellState` reads, support inspection, fresh premises,
assert-under-premise, retraction, and finite DDB on that same state. `DataflowBox` distinguishes reads from
outputs and permits `None` for "emit no contribution"; core `Constraint.convert` installs two directional
source-only propagators, while core `Constraint.combine` owns fan-in readiness and support union. The
existing total `Constraint.dataflow` constructor remains source-compatible by wrapping every result in
`Some`. This is core propagator behavior, not friendly syntax.

`propagator-friendly.fsx` must only adapt payloads, map user premise names to opaque core handles, expose
methods and the `network { }` entry point, and delegate. Its helper is `PayloadAdapter`, not
`FriendlyDomain`; it stores projection/injection functions and the already-created core net, but no model,
constraint registry, lowering, cache, fallback rule, support calculation, or search driver. Friendly finite
cells preserve the original Set-shaped assertion/read API atop core `FiniteRep`; scalar and interval cells
preserve local contradiction, partial reads, named provenance, and live retraction. `ShowSupport` remains,
with `Network.Support` supplying support from the opaque core cell handle.

The three active scripts remain the canonical core, its test harness, and dependent friendly syntax.
`propagator-mutable-core.fsx` remains historical benchmark evidence only. This recovery implements General
`assume`/`retract` because friendly live editing requires them; Optimized editing and the streaming seams
(`solutions`, `generate`, `onCell`, `onNet`) remain the next work order. The optimized array hot loop is
unchanged. The 2026-07-10 same-process benchmark records Optimized at 14.30x the General face on the portable
binary-relation Sudoku and a 44.2 us best live General two-assert/two-retract cycle; earlier benchmark rows
remain unchanged and are not cross-run baselines for this different relation encoding.

**Codex superseding suggestion, 2026-07-10 - enduring facade, test-only examples.** Keep exactly three
active surface scripts. [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) is the load-safe
canonical core: vocabulary, engines and lowerings, generic relation/dataflow combinators, and enduring
value helpers (`Scalar`, `Interval`, `Transform`, and `Transform.Affine`). Core `Gac` is private
implementation machinery. [propagator-friendly.fsx](../propagator-friendly.fsx) is a silent, load-safe
library facade containing only payload adaptation, named-premise translation, methods, computation-
expression operations, and direct delegation to one live `GeneralNet`. Its generic `Constrain` operation
accepts authored core constraints but supplies no domain policy of its own.

Move every concrete scenario and executable check to
[propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx): differential proof slices, a test-local
copy of GAC for the independent fixpoint harness, Celsius/Fahrenheit examples, barometer formulas and all
four provenance stages, finite-Set verification, Sudoku givens/topology, renderers, expected results,
benchmark runner, and `main`. `AllDifferent` is not an enduring core or friendly feature; Sudoku authors
that reducible policy locally with `Constraint.relation`. This supersedes the preceding suggestions only
where they promoted `AllDifferent`, exposed core GAC for a harness, kept examples executable in the facade,
or placed reusable scalar/transform helpers outside the canonical core. Historical descriptions remain
unchanged below.

**Fable (Tier 0) audit, 2026-07-10 — the housekeeping consolidation (three Codex passes above): ACCEPTED,
with two reversals referred to Deen for ratification.**

*Verified independently.* The consolidated suite passes under fsi: both differential slices, the sparse DDB
oracle (4 open cells, unique brute-forced solution, both faces agree), all capability/width guardrails, the
authored-order canary, the barometer's four provenance stages bit-exact (√10 = 3.16228), raw-scalar
`C = BOT` with `F` unaffected, and the new live-edit regression (cell-local contradiction, live restore).
Both library scripts load silently, as claimed. The LIFO premise-reclaim discipline survived the rework on
both faces (general: `nextPremise <- premise` after `Retract`; optimized: `gp <- p` unchanged). The
optimized hot loop and its edit/streaming stubs are untouched. The third-slice work order is neither
executed nor half-executed — the premise preflight correctly remains at lower-time until that slice moves
it. The engine headers now say "copied and adapted" and name their deltas, settling the second gate's
minor. Both benchmark entries follow the convention (correctness-gated, same-run ratios, cross-run
non-comparability stated), and the optimized/general ratio is stable across the two runs (14.30× / 14.37×).

*Process.* The append-only rule held under real pressure: the rejected rebuild-on-read facade is preserved
verbatim and superseded in the open — an honest self-correction on the record rather than a squashed one,
which is exactly the rule working. General `assume`/`retract` arriving ahead of the work order is accepted:
friendly live retraction genuinely requires them, the justification is recorded, and the remaining seams
are correctly left to the next slice.

*Referred to Deen — two executor suggestions reverse his recorded ratifications, which a suggestion cannot
do on its own:*

1. **`AllDifferent` removed from the friendly surface.** §13 (ratified 2026-07-06) names
   `net.AllDifferent grp` in the decided method list, and §5 calls `allDifferent` a write-once workhorse.
   The removal's boundary instinct is right for the *core DU* (no policy cases in the vocabulary), but the
   friendly face exists precisely for this ergonomics. Recommendation: restore it as **facade sugar** over
   `Constraint.relation` — a few lines in `propagator-friendly.fsx`, no core case — satisfying both the
   boundary and the ratified UX. Alternatively Deen ratifies the removal explicitly and §13 gets a
   superseding entry.
2. **`Transform`/`Affine` promoted into the canonical core file.** Reverses §13's "conversion helpers are
   external / supplied, not core (Deen, 2026-07-06)". (`Scalar` and `Interval` in core are §12-consistent
   and uncontested.) Low stakes either way; it is his call, not the executor's.

Related: AGENTS.md's new core-membership rule hardcodes both contested examples as repo law. The principle
is sound; the example list should track Deen's ruling on the two items above.

*Minor.* The first 2026-07-10 benchmarks entry links to `propagator-surface-vocab.tests.fsx`, deleted the
same day by the consolidation — fix by dated addendum noting the move into
`propagator-friendly.tests.fsx`, not by editing the historical entry.

— Claude (Fable 5, Tier 0), 2026-07-10

**Deen ratification, 2026-07-10 — both referred items resolved, and the principle behind them named.**

1. **`AllDifferent`: ratified out of core.** It was a testing-specific function; core membership was the
   entire issue, and *outside* core its form is free — facade sugar may return someday if ergonomics
   warrant it, but nothing is mandated. This supersedes §13's listing of `net.AllDifferent` among the
   decided friendly methods (that listing stands as history; the method is no longer a required part of
   the surface).
2. **`Transform` / `Affine`: ratified in core.** They are helpers, but ergonomic and *general*. This
   refines rather than reverses §13's "conversion helpers are external / supplied, not core": that
   decision targeted helpers overfit to a specific demo or test, not general value algebras. `Scalar` and
   `Interval` in core were already uncontested (§12).

The principle, for future gates: **core membership is decided by durable architectural role, not by
reference count or abstract genericity — general ergonomic algebras stay; fixture-shaped conveniences go
to the test/demo split, whatever generic name they carry.** The named failure mode this fences: executors
tend to be unhygienic about modularity and myopic about design, promoting whatever a current fixture
needed into library surface. AGENTS.md's core-membership rule, including its example list, is hereby
ratified as written.

**Codex suggestion, 2026-07-10 - AsyncRx as a UX benchmark, not a module-count target.** AsyncRx is the
stronger current user-facing UX because each public entry point has one canonical meaning: one
`AsyncObservable` operator surface, one `AsyncRx` consume front door, standard CE syntax, and an explicit
`EventChoice` authoring substrate. The comparison should measure semantic economy and coherence, not ask
the propagator library to imitate AsyncRx's topology. AsyncRx transforms one homogeneous
`AsyncObservable<'T>` type; a propagator library irreducibly has domains, typed cells, authored constraints,
mutable network sessions, finite and lattice views, and General versus Optimized capabilities. Those are
orthogonal concepts and deserve distinct homes when merging them would obscure their contracts.

The governing test is: **duplication is evidence of something wrong; plurality is not.** Two names or
paths expressing the same concept should trigger consolidation or a clear declaration of which is
canonical and which is subordinate syntax. Separate modules or types are healthy when they encode
orthogonal concepts. Merge only when it removes repeated meaning while preserving those distinctions;
avoid a smaller surface bought by conflating concepts that users must still reason about.

Applied to the present facade:

- `Domain`, `Constraint`, and `Network` are distinct roles (describe values, author laws, run/edit a live
  session), not proliferation. `General` and `Optimized` likewise expose different capability faces over
  one authored model; compacting them together would erase useful boundaries.
- `Network` methods and the same public operations repeated through `Ops` are genuine semantic duplication,
  not orthogonality. Decide one canonical dialect. If the CE adapter remains, document and shape it as
  subordinate syntax rather than a second peer vocabulary.
- The finite facade currently collapses three concepts into `Set<'value>`: authored value, current
  candidate view, and internal finite representation. That is a real coherence defect. Friendly finite
  cells should be authored as `'value`, relations should consume `'value list`, and reads may return
  `Set<'value>` without making Set the cell's authored type or the relation vocabulary.
- The overloaded `Assert` name hides the orthogonal distinction between a permanent `Given` and a named,
  retractable `Assume`; name those semantics directly. `Constrain` consuming an authored `Constraint` is a
  valid noun/verb boundary, provided it does not expose representation-shaped values.
- `Solve() : bool` followed by mutable `Value` reads underspecifies consumption compared with AsyncRx's
  ordered run front doors. Revisit the result shape so success, failure, and the solved view are explicit,
  while keeping the live edit session distinct from batch solving.

This is the next friendly-UX review, not authorization to compress enduring concepts or to restore
fixture-specific sugar. The target is the smallest surface that preserves the library's real conceptual
axes and gives each surviving operation one clear home.

**Fable (Tier 0) review, 2026-07-10 — the friendly-UX refinement proposal: ACCEPTED as the fourth-slice
basis; the CE fork referred and resolved same day; two sharpenings locked.**

The governing test — *duplication is evidence of something wrong; plurality is not* — is the companion of
the core-membership principle, and the pair bracket executor myopia from both sides: core-membership stops
executors promoting fixture needs into library surface; this one stops them compressing orthogonal concepts
to win at surface-count optics. The proposal self-applies it honestly (it declines to imitate AsyncRx's
one-type topology, and closes by forbidding itself fixture sugar). Accepted as-is: `Domain`/`Constraint`/
`Network` and General/Optimized keep their distinct homes; `Given`/`Assume` get named at the method level —
that lifts the names `Ops` already uses, and it is TMS-honest, since a Given is a premise the user holds no
handle to, not a different mechanism. The same naming pass should decide how anonymous given-premises
display in `ShowSupport` (today they print as bare `p3` — mysterious provenance in the same coherence class).

**Referred and resolved — the CE stays, as an alternate language.** The proposal's "decide one canonical
dialect; *if* the CE adapter remains…" quietly reopened Deen's 07-06 ratification of Option C (methods plus
the `network { }` monad). Referred; Deen ruled same day: keep — and the ruling reframes it better than
subordination. Methods and `Ops` + `network { }` are two *dialects* over one semantics — object-shaped and
function-shaped F#, for users with different working styles — and the outward differentiation is strong
enough that no user wonders whether two concepts exist. That satisfies the duplication test by its own
declaration clause: duplication is two names for one concept *ambiguously*; a declared alternate language is
plurality. The discipline that keeps it healthy is already as-built: `Ops` delegates to the methods, which
delegate to `General.*`, so the semantics is authored exactly once and only syntax varies. Future
consolidation passes may not merge or remove a dialect; this is now a ratified boundary.

**Sharpening 1, locked for the fourth slice — the finite refit must not amputate partial information.** The
tri-conflation diagnosis is the proposal's strongest item, and the target shape is right: authored type
`'value`, relations over `'value list`, reads returning `Set<'value>` as the candidate *view*. But the
current Set-payload `Assert` accidentally provides set-valued *restriction* — "this cell is one of
{2, 3}" — which is genuine partial information, the propagator identity itself, not fixture sugar. The refit
must either give restriction its own named verb (e.g. `Restrict`) or explicitly defer it in this doc; the
one thing it may not do is drop the capability silently.

**Sharpening 2, locked — the solved view is a snapshot, not a second surface.** Making success, failure,
and the solved view explicit in the `Solve` result is right, but a "solved view" is exactly where an
executor reaches for a second stateful handle — the rebuild-on-read facade caught and superseded above is
the precedent. The solved view must be a pure snapshot read-out over the one live net; never a second live
surface, never a rebuilt engine.

Sequencing: this slice is facade-only; the third-slice work order above is core + application file. They
are orthogonal and can execute in either order. The fourth-slice work order gets cut when Deen directs an
executor at it.

— Claude (Fable 5, Tier 0), 2026-07-10

**Codex implementation status, 2026-07-10 - fourth friendly slice BUILT + verified.** The accepted UX
refinement now ships in the load-safe facade. `Domain.finite` creates `Cell<'value>` directly through
`General.createFinite`; relation predicates consume ordinary `'value list`, while `Value` exposes the live
candidate view as `Set<'value>`. Exact permanent information is `Given`, exact named/retractable information
is `Assume`, and partial information remains explicit through permanent and named/retractable `Restrict`
overloads. Restrictions are translated by filtering the authored domain list, so the core retains authored
candidate order even though observation is a mathematical Set.

`Solve` now returns the existing `Solution<'state> option` snapshot from `General.solve` instead of a boolean
followed by mutable reads. No solved wrapper, rebuilt model, second live handle, or facade propagation logic
was introduced. Anonymous permanent support now displays as `given(<cell>)` or `restrict(<cell>)`; authored
premise names remain unchanged. Methods and `Ops` + `network { }` remain equal alternate routes to the same
delegated semantics. The function-shaped restriction names are `Ops.restrict` and `Ops.restrictNamed`, the
minimal spelling needed because module functions do not have method overload syntax.

The consolidated harness now proves value-shaped finite relations and givens, Set-shaped reads, both
restriction lifetimes, assumption retraction, readable provenance, authored-order solve, snapshot-consuming
Sudoku, and method/CE parity across reads, support, retraction, and solve. Silent core and facade loads and
the complete correctness suite pass under .NET 9.0.12. Two complete same-process benchmark passes observed
Optimized/General best-time ratios of 11.06x and 6.83x; the timed core path was unchanged, and the opposing
movement of General and Optimized between runs was recorded as machine variability rather than attributed
to this facade-only slice. No core engine, representation, search loop, or propagation hot loop changed.

**Fable (Tier 0) gate, 2026-07-10 — fourth slice ACCEPTED; the benchmark spread diagnosed: Power saver
plan, not the refit.**

Read the rebuilt facade and independently re-ran the full suite: PASS, bit-exact fidelity through the new
value/view API (barometer all four stages, raw-scalar `C = BOT`, both differential slices, live
retraction). Both locked sharpenings honored, the first exceeded: `Restrict` ships in *both* lifetimes
(anonymous-permanent displaying `restrict(<cell>)`, and named-retractable), translating by filtering the
authored domain list so authored candidate order survives observation-as-`Set`; `Solve` returns the core's
pre-existing `Solution<'state> option` — a plain `Map` snapshot, no second live handle, no rebuilt engine,
no facade propagation. `Given`/`Assume` named as ruled; anonymous permanent support now displays
`given(<cell>)`, closing the `p3`-provenance flag from the review. The CE ruling is honored: methods and
`Ops` + `network { }` remain equal dialects, with `Ops.restrict`/`Ops.restrictNamed` added. The refit also
went one honest step past the work item: friendly finite networks now run the core at `'state = 'value`
directly (`General.createFinite values`, no `Set.singleton` wrapping), so relations consume plain values —
the tri-conflation fix reaching the representation, not just the signatures. An explicit
alternate-dialect bullet was added to AGENTS.md's new "Prefer Direct Designs" rule so a future
consolidation pass reading only AGENTS.md cannot mistake the two dialects for repeated meaning.

The benchmark spread is diagnosed, not just unattributed: the machine was found running the Windows
**Power saver plan on AC**. During a third Tier-0 benchmark run the CPU sampled at 40–57% of base
frequency throughout (i7-8750H at ~1.0–1.3 GHz effective vs 2.2 base) — and that throttled run still beat
both recorded runs (General best 29,845.7 µs, Optimized best 3,016.7 µs, ratio 9.89×), bracketing five
same-code runs at General 29.8k–49.5k / Optimized 2.8k–4.6k / ratio 6.83×–14.37×. Under a bouncing clock
governor even same-process ratios move 2× because the two rows execute minutes apart. The executor's
refusal to attribute the spread to the refit was correct; dated addendum in
[benchmarks.md](benchmarks.md) records the diagnosis and extends the convention (record the active power
plan in the env block; prefer a high-performance plan for recorded runs). The earlier standing minor —
the dangling `propagator-surface-vocab.tests.fsx` link — was closed by the executor's own addendum.

Fourth slice CLOSED. The board returns to one pending executor item: the third-slice work order above.

— Claude (Fable 5, Tier 0), 2026-07-10

**Codex work-order review, 2026-07-10 - the third-slice WFC order requires a recut before
execution.** The Fable work order above remains preserved as the historical proposal, but it is no
longer executable as written after Deen's clarification of the target. This entry records the issues;
it does not authorize implementation or silently choose the replacement design.

1. **The artifact and placement are wrong.** The existing
   [propagator-wfc.fsx](../propagator-wfc.fsx) is historical implementation and timing evidence. It must
   remain intact. The new WFC work belongs in a second file so old and new can be run in the same
   environment and compared without erasing the path that produced the earlier numbers. This supersedes
   the order's instruction to add a new literate section to the existing file.

2. **The performance objective has changed materially.** The old order deliberately routes WFC through
   closure-backed relation boxes and list-based `Gac.narrow`, explicitly calls that route "not the 60fps
   path," and asks only for its cost curve. The clarified target is a highly optimized WFC implementation.
   The current `Optimized` vocabulary face is a one-word finite-domain store with per-relation GAC; the
   historical `Wfc.Engine` is a multiword, structural-neighbor store. Reaching the new target therefore
   requires an explicit architectural decision: either an enduring, domain-independent optimized seam can
   support the specialization, or the specialized machinery remains WFC-local behind the enduring friendly
   UX. This may not be hidden as an incidental implementation detail.

3. **Friendly UX is an acceptance criterion, not an optional wrapper.** Friendly means the library's good,
   understandable user experience, and WFC is a stress test of its flexibility. The `network { }` syntax may
   remain General-only, but a WFC author must not fall back to visible `Model` assembly, lowering selection,
   representation conversion, engine wiring, or generic constraint plumbing. A WFC-local helper that merely
   hides an awkward library boundary would conceal rather than solve a Friendly failure. The recut must first
   determine how much of the enduring Friendly vocabulary naturally authors WFC, and add only the smallest
   coherent general surface needed. It must not duplicate `Ops`, invent speculative backend interfaces, or
   conflate the live General `Network` with backend-neutral model authoring just to make one example compile.

4. **The core-machinery requirement must be discussed before it is added.** The current APIs cannot provide
   the proposed generation and replay laws unaided. Candidate generic needs include premise-free collapse,
   reset/restart to authored information, deterministic seeded choice, synchronous change notification, and
   relocation of the optimized solve premise check. Any accepted domain-independent propagator mechanism may
   live in core; tile sets, grids, adjacency, WFC entropy policy, WFC storage, and WFC optimization stay wholly
   in the new file. The old order preselects several mechanisms before the new optimized and Friendly paths
   have been settled, so those choices must be revalidated rather than implemented by inertia.

5. **The observation contract has unresolved generality and replay holes.** `CellChange<'a>` describes
   authored-order candidate lists, but General also supports scalar, interval, and arbitrary rich lattices,
   where candidate-list and `Resolved` semantics are not defined. The replay law also lacks a declared initial
   state: subscriptions are installed after lowering, so given-driven changes may already have occurred.
   Restore-to-singleton transitions need an unambiguous rendering rule as well. The recut must either scope
   finite observation honestly or define a representation that remains coherent for rich lattices, and it
   must state the replay baseline without requiring consumers to inspect the live net.

6. **Several acceptance statements conflict with current code or with each other.** Moving
   `PremiseWidthExceeded` from `Optimized.lower` to `Optimized.solve` necessarily changes the existing
   65-cell guard row, despite the old acceptance line saying every row passes unchanged. The new law should
   prove that such a model lowers and can generate, while deterministic DDB solve fails loudly before search.
   The attention note that `assume` and `retract` remain stubbed is already stale for General. The old
   `generate : Solution seq` stub is explicitly non-binding, but the work order does not decide whether one
   seeded map or a stateful lazy stream is the actual UX; the simpler one-map contract should be preferred
   unless repeated generation demonstrates a need. These are work-order corrections, not implementation
   discretion to resolve silently.

7. **The benchmark acceptance is now incomplete.** The new file must run correctness before timing and obey
   the repository's same-run, relative-comparison rule on this variable machine. Its rows should compare the
   preserved historical WFC implementation and the new implementation together at matching sizes, report
   ratios and scenario context, and record the active power plan. A table that times only the vocabulary GAC
   route would answer the superseded question.

The enduring laws remain: `solve` stays deterministic in authored value order; alternative authoring and
execution routes must express the same WFC semantics rather than become unrelated examples; seeded behavior
must be reproducible from visible authored state; no WFC-shaped policy enters propagator core; the old WFC
file and dated history remain untouched. A replacement work order should be cut only after the Friendly
authoring shape, high-performance backend path, smallest generic core seams, replay contract, and comparative
benchmark plan have been agreed.

**Fable (Tier 0) review + reversal, 2026-07-10 — the Codex recut review: ACCEPTED; the backend-placement
fork resolved by diagnosis; Deen ratified.** Codex's seven findings stand. My first response to finding 2
recommended keeping the fast machinery WFC-local; Deen challenged the recommendation ("WFC is constraint
prop at heart — if it's not encodable, either the library overfit to a demo or WFC-specific optimizations
are being smuggled"), and the challenge is correct. Diagnosis, recorded openly:

- **The overfit is real and datable.** `optimizedDomainWidth = 64` (one `uint64` word per cell) and the
  closure-boxed relations narrowed by tuple-enumerating `Gac.narrow` were dimensioned by the Sudoku proof
  fixture (9 values, small slices) and survived three review cycles unquestioned. The domain-width cap rode
  under cover of the fork-B decision, which was about *premise* width — a genuinely separate 64 (a DDB solve
  capability), conflated with domain width by proximity.
- **There was no smuggling — the "WFC-specific machinery" is not WFC-specific.** The historical
  `Wfc.Engine` declares this itself ("Same laws as module M... different machinery"; selection policy is
  driver-side; "the engine never learns what a 'guess' is"). Its inventory — multiword bitset domains
  (`(nTiles + 63) / 64` is the generic width formula for any finite domain), binary relations precompiled to
  per-value support masks (bit-parallel GAC), flat cell-major store, trail, premise supports — is generic
  finite-CSP machinery throughout. My earlier framing labeled the contents by the filename; that mislabeling
  manufactured the appearance of failure (2) to protect failure (1). The genuinely domain-shaped remainder
  is exactly two things, both already outside the engine: the 4-neighbor grid topology (a special case of an
  edge list) and weighted-entropy selection (driver policy; plain MRV is the generic default).

Resolution, ratified by Deen 2026-07-10: **fix the core's finite story rather than route around it.** The
historical engine's role sharpens accordingly — not machinery to preserve *from* generalization but the
*prototype* of the generalized face. The replacement work order below supersedes the 2026-07-10 third-slice
order (preserved verbatim above): its in-place placement and deliberately non-60fps framing die; its verb
split, premise invariant, and RNG discipline survive as laws; its `CellChange` event taxonomy is replaced by
the snapshot contract.

**Fable (Tier 0) work order, 2026-07-10 — third slice recut: the generalized finite face, `generate` +
snapshot observation, and the WFC application file.** Locked rules are marked; mechanism is the executor's
discretion. Three deliverables, one pass.

*Deliverable A — generalize the optimized finite face
([propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx)).*

- **Locked, one store, width-parameterized:** the optimized face's cell representation becomes a multiword
  bitset — `W = ceil(|domain| / 64)` words per cell in a flat cell-major array. The current single-word face
  is the `W = 1` case of this store, not a sibling architecture; there are not two optimized faces at the
  end of this slice. Domain width ceases to be a capability limit. **Premise width ≤ 64 remains** a
  capability, honestly, and it is a *solve* capability (see the preflight relocation below).
- **Locked, relations compile at lowering:** authoring is unchanged — `Constraint.relation` with a closure
  `allows`. The optimized lowering compiles each *binary* relation to per-value support-mask tables by
  sampling `allows` over dom×dom (the `allowed[d][t]` shape the prototype already proved), giving
  bit-parallel GAC on the hot path. Zero new authoring nouns. N-ary relations keep the generic `Gac.narrow`
  route — correct, slower, and honest about it.
- **Locked, the no-regression canary:** generalizing must not tax `W = 1`. The existing Sudoku differential
  rows and same-run benchmark ratios are the canary; a materially worse optimized/general ratio on the
  existing rows fails the slice.
- Edge representation inside the lowering (explicit edge list vs. structure-implicit enumeration), trail
  mechanism, and table layout are executor's discretion.

*Deliverable B — `generate` + observation on the vocabulary faces, under the surviving laws.*

- **Locked, the verb split (law):** `solve` stays exactly as it is — complete DDB, deterministic, value
  order = authored order (existing canaries hold). `generate` is the stochastic sampler: propagate to
  fixpoint → pick the open cell with fewest candidates (ties by `model.cells` order) → draw from the
  authored-order candidate list → collapse → repeat; contradiction restarts the whole sample; loud failure
  past a bounded restart count. No interleaving with DDB.
- **Locked, the premise invariant (law):** `generate`'s collapses consume **no premises** — never retracted
  individually; contradiction aborts the sample; live premises during `generate` = givens only, independent
  of grid size. Mechanism is the executor's choice; the invariant is not.
- **Locked, RNG discipline (law):** an explicit pure PRNG in core (SplitMix64 suggested), seeded via a
  strategy parameter — not `System.Random`. Draws are a function of visible state only (authored-order
  candidate lists, `model.cells` order), never of rep internals. Uniform draws this slice; tile *weights*
  are a deferred strategy extension. Acceptance theorem: **same model + same seed ⇒ identical map on every
  route that can run it** — the differential harness extended to `generate`, General reference vs. the
  generalized optimized face.
- **Locked, the result shape:** one seeded map per call — `Solution option` (or a Result carrying the
  bounded-restart exhaustion), not a lazy stream. Repeated generation is calling again with the next seed.
- **Locked, the preflight relocation:** `PremiseWidthExceeded` moves from `Optimized.lower` to
  `Optimized.solve` (loud, pre-search). Lowering keeps only what lowering itself needs. The existing 65-cell
  guard row **changes** — it becomes the new law's positive test: such a model lowers and `generate`s, while
  deterministic `solve` fails loudly before search. (This corrects the superseded order's "every row passes
  unchanged" acceptance line, which contradicted its own relocation.)
- **Locked, observation as snapshots:** an event is `(cell, current CellState)` — no event taxonomy, no
  candidate-list payload. This is lattice-general for free (scalar/interval cells covered with no new
  semantics). Subscription emits one synthetic snapshot per cell at subscribe time — the declared replay
  baseline. **Replay law:** per-cell last-write-wins; the final event per cell *is* the final state; the
  stream folds to the final net with no net inspection. Calling contract per §12: synchronous, engine
  thread, mid-propagation (search observes speculative narrowing — the honest contract), `IDisposable`,
  buffering is the outer wrapper's job.

*Deliverable C — the WFC application file (**new file: [propagator-wfc-app.fsx](../propagator-wfc-app.fsx)**).*

- **Locked, preservation:** the historical [propagator-wfc.fsx](../propagator-wfc.fsx) is not modified. The
  new file consumes the vocabulary via `#load "propagator-surface-vocab.fsx"` and may `#load` the historical
  file for the comparative benchmark. Nothing is copied out of either.
- **Locked, the Friendly acceptance criterion:** the file exposes a domain-shaped authoring layer — tileset,
  compatibility, grid size, seed — with zero model / lowering / representation / engine nouns visible to the
  WFC author. If the layer cannot be thin over the enduring vocabulary, that is a Friendly failure to
  surface and fix in the library, not to hide in the wrapper. App-level content: tiles, 4-neighbor grid
  topology builder, rendering; one shared relation box per direction reused across edges.
- **Locked, the oracles:** (a) post-hoc independent map walk — every adjacent pair satisfies the
  compatibility table and every cell is resolved, no engine involved; (b) the cross-route seed test at small
  size (e.g. 8×8): same seed, General and optimized faces, identical maps; (c) a replay check: the snapshot
  stream folds to the final grid; (d) a contradiction-capable tileset row forcing restarts, proving the
  bounded-restart loud failure.
- **Locked, the comparative benchmark:** dated entry in [benchmarks.md](benchmarks.md) — correctness before
  timing; same-run rows for the historical `Wfc.Engine` and the new generic-face route at matching sizes
  (16×16 / 32×32 / 64×64, larger as fsi tolerates, toward the 500×500 target); ratios and scenario context,
  not absolutes; the active power plan recorded in the env block per the 2026-07-10 convention.

*Attention list (subtle, not extra rules):* restart must re-assert givens, and if the mechanism rebuilds,
subscription disposal across rebuilds must not leak; compiled-table memory is per *distinct* relation —
dedupe by shared relation box (the grid reuses 4 tables, not 4 × 250k) or the tables dwarf the store;
sampling `allows` over dom×dom at lowering is |dom|² per distinct relation — fine at tile scale, worth a
note in the file header; engine-copy headers must name their deltas (the standing rule: headers must not
lie).

*Acceptance:* every existing harness row passes unchanged **except** the relocated premise-width guard row,
which is rewritten as specified in deliverable B; the new oracle rows pass; all scripts run clean under fsi;
the benchmarks entry lands; §11 history appended (append-only per AGENTS.md).

— Claude (Fable 5, Tier 0), 2026-07-10

**Deen + Codex clarification, 2026-07-10 - Friendly owns the optimized outward UX.** The recut order's
generalized finite face remains internal implementation machinery. Friendly is the coherent,
low-cognitive-overhead surface for ordinary and optimized finite work: easy relations stay easy, while
arbitrary n-ary relations, one relation over many scopes, custom topologies, seeded generation, and
snapshot observation remain directly expressible and understandable. WFC contributes tiles,
compatibility, grid topology, and presentation; it exercises these general capabilities without exposing
model construction, lowering, representation, or engine plumbing.

The enduring architecture and execution plan is
[Generalized Finite Face and WFC Application Plan](propagator-finite-face-plan.md). It operationalizes the
recut work order, records the grouped-`RelationBox` resolution, preserves the historical WFC implementation,
and defines the phased correctness, UX, differential, and comparative-performance gates. The plan survives
implementation; outcomes are appended to it rather than replacing it.

-- Codex, 2026-07-10

**Codex implementation status, 2026-07-10 - generalized finite face and WFC application complete.**
The ratified plan is implemented. `Constraint.relations` and Friendly `RelateMany` represent one predicate
over repeated scopes; Optimized uses one runtime-width cell-major bitset store, compiles grouped binary
relations once into forward/reverse support rows, and retains generic GAC for arbitrary n-ary scopes.
`solve` remains authored-order DDB with its 64-premise capability checked before search. `generate` is
seeded bounded restart with premise-free collapse, a stable SplitMix64 stream, MRV, and a change-driven
heap. Observation now emits `(Cell, CellState)` snapshots with a synchronous authored-order baseline.

Friendly's Phase-5 audit found one authority per facade network: rich domains close over one `GeneralNet`;
finite domains close over one `OptimizedNet`; the private operation record only delegates. There is no
second mutable model and no propagation, reset, GAC, table, or observer mechanism in Friendly. The actual
Phase-1 examples use only `Domain.finite`, `Cell`, `Relate`, `RelateMany`, `Given`, `Generate`, `Observe`,
and their `Ops` equivalents. The new [WFC application](../propagator-wfc-app.fsx) keeps tile vocabulary,
directions, grid topology, rendering, and validation local; its authoring path names no backend, lowering,
representation, or engine concept. The historical [WFC prototype](../propagator-wfc.fsx) remains
byte-for-byte unchanged (SHA-256 `836A49E4...357621`).

This remains compatible with Sussman's propagator model at the semantic boundary: cells accumulate
information monotonically through meet, propagators wake on changed cells and run to quiescence,
contradiction is bottom, and premise support permits retraction and recomputation. Compiled support tables
and multiword storage specialize execution without changing those laws. Seeded generation is deliberately
an outer search strategy that resets/restarts the monotone kernel; it is not presented as a new primitive
propagator operation or as API-level drop-in compatibility with a particular Sussman implementation.

-- Codex, 2026-07-10

**Codex benchmark clarification, 2026-07-11 - matching historical WFC constraints added.** The cyclic
directional grid remains the readable application/UX proof, but it is no longer the only performance
evidence. `propagator-wfc-app.fsx --benchmark-like-for-like` now runs the historical 500x500 gravity and
3-color constraints through Friendly with seed 42, a retained network, pre-timing validity checks, one
warmup, and generation-only timing. It reports best and mean and checks the final map again afterward.
The application still owns every WFC-shaped rule; no matching concept moved into core.

The preserved engine remains much faster on gravity, while the generalized Friendly route is much faster
on 3-color in the observed Power-saver runs. This is useful workload evidence, not a single headline
speedup: the old path returns an `int[]` and uses `System.Random`-shuffled DFS; Friendly returns the public
immutable `Map` and uses stable SplitMix restart generation. Exact numbers and sampling boundaries are in
`benchmarks.md`.

-- Codex, 2026-07-11

**Codex current suggestion, 2026-07-11 - retain the measured direct finite optimizations and stop at the
immutable result boundary.** Profiling located five independent costs worth removing: linear facade cell
lookup, WFC authoring of a universal direction, replay of the same settled pre-generation state, per-arc
one-word arrays, and solved-cell candidate lists. Their replacements remain small and orthogonal: one
maintained position dictionary, a WFC-local universality check, one snapshot of the existing engine's
candidate/support arrays, a scalar branch inside the same runtime-width engine, and direct singleton lookup.
No WFC noun or mechanic entered core, and Friendly gained no backend vocabulary or second mutable authority.

The exact gravity and 3-color fingerprints, gravity draw/restart sequence, authored order, supports,
synchronous observation, retraction/recomputation, empty-source bottom propagation, and 65/128/130-value
behavior all remained fixed. The established General/Optimized binary-relation Sudoku timings and General
live-edit timing were carried through every full checkpoint; other Sudoku and tutorial fixtures remain
correctness-only because they have no individual timing precedent. Sequential timing varied heavily on the
Power saver machine, so candidate decisions use arc/reset/allocation evidence plus full control runs rather
than pretending the checkpoints are strict final-tree ablations.

The immediate comparisons are: Optimized Sudoku improved from 1,001.6 to 619.8 us best while General best
remained stable (32,651.1 to 31,483.6 us); matching gravity improved from the prior Friendly 5,959.679 ms
to 1,057.193 ms at the strongest checkpoint, though the historical rerun remained faster at 292.197 ms;
matching 3-color improved from 4,958.214 to 1,228.796 ms and remained far faster than historical
55,390.970 ms. Cyclic has no historical counterpart. Its own 500x500 row moved 12,011.427 to 3,842.648 ms
at the strongest checkpoint before a late-session 12,087.915 ms sample tracked the machine-wide slowdown.
Historical ramp rows were preserved, but no equivalent ramp performance row was run through the changed
core; that is the next explicit measurement gap rather than an implied success.

Do not add a custom picker, flattened arc layout, alternate result type, or WFC-specialized core engine on
the current evidence. Picker work was too small to justify an indexed heap, and after the retained changes
the principal residual was immutable `Map` construction. Changing that public result is a separate surface
decision. The complete candidate table, failures, timings, and final rationale are retained in the
[Generalized Finite Face and WFC Application Plan](propagator-finite-face-plan.md); the full benchmark ledger
is in [benchmarks.md](benchmarks.md).

-- Codex, 2026-07-11

**Codex current suggestion, 2026-07-11 - retain the generic structural-fixpoint rebuild and close the ramp
measurement gap.** Ramp now authors 32-, 100-, 128-, and 512-value integer domains through the same app-local
generic `Grid` and Friendly operations as the earlier WFC examples. The historical small-domain laws,
multiword seam, support, bottom, retraction, and large-grid spot checks all pass. No WFC noun, grid mechanic,
or alternate relation form entered core or Friendly.

The measured core issue was general: assertion replacement and retraction restored raw top and enqueued every
relation arc, even when the authored edit could affect only a local wave. The optimized engine now restores
its immutable constraint-only fixpoint, then applies the authoritative assertion registry and propagates from
actual narrowings. Multiword arcs also reuse invocation-local scratch and enumerate set bits. Nested observer
propagation remains safe because each `quiesce` invocation owns its buffer. This remains one mutable network,
not a cached second model.

In the final fixed-order process, ramp512 corner measured 6,469.697 ms versus historical 5,722.003 ms, and
ramp128 center measured 182.009 ms versus 137.628 ms. The current rows are therefore 1.13x and 1.32x slower,
respectively, rather than the initial current-core 19,614.021 and 2,563.402 ms. Cyclic 500x500 measured
4,167.414 ms; gravity and 3-color measured 1,338.347 and 1,324.329 ms with exact fingerprints. The adjacent
finite controls retained a 38.95x Optimized/General Sudoku ratio and a 31.9 us General live-edit best.

Keep the direct generic changes. Do not add grid-shaped queues, cone-local WFC mechanics, or AC-4 counters to
core without new cross-workload evidence. Full measurements and sequential-ablation caveats are in
[benchmarks.md](benchmarks.md); implementation reasoning is in section 17 of the
[Generalized Finite Face and WFC Application Plan](propagator-finite-face-plan.md).

-- Codex, 2026-07-11

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
- **2026-07-10 - Fable (Tier 0) work order (third slice).** Ordered `generate` + the observation seam +
  the WFC application layer as one executor pass (§10). Locked: the `solve`/`generate` contract split
  (complete-deterministic vs seeded-stochastic-restart); `generate` consumes no premises (live premises =
  givens only, at any grid size — fork B honored); `PremiseWidthExceeded` relocates from lower-time to
  `solve`-time; explicit pure PRNG with the cross-face theorem *same seed ⇒ identical map* as the new
  differential row; the `CellChange` payload (authored-order `'a list`) and the event replay law; the app
  layer lives in `propagator-wfc.fsx` via `#load`, never by copying the vocabulary; oracles = engine-free
  map check, cross-face seed test, replay fold, bounded-restart failure; benchmarks entry with the honest
  "not the 60fps path" framing. `solutions`/`assume`/`retract` stay stubbed.

- **2026-07-10 - Codex (core/friendly source consolidation).** Made
  [propagator-surface-vocab.fsx](../propagator-surface-vocab.fsx) the load-safe canonical core. Extracted
  `Differential`, proof slices, fixtures, independent oracles, and `main` into the new
  [propagator-surface-vocab.tests.fsx](../propagator-surface-vocab.tests.fsx). Replaced the standalone
  friendly implementation with a facade that `#load`s the core, authors the same `Model`, and lowers
  through `General`; no engine, `FiniteRep`, core `Domain`, or search implementation is copied there.
  Retained `Interval` in core, added interval/float arithmetic overloads plus square-root support, and
  rewrote the C/F examples with ordinary operator syntax. `propagator-mutable-core.fsx` is now explicitly
  only historical benchmark evidence. Verified the core directly, the extracted differential harness,
  and the dependent friendly examples under `dotnet fsi`; recorded that partial cell reads from a
  contradictory friendly model await the deferred core observation/read seam.

- **2026-07-10 - Codex (live-core recovery; supersedes the rebuilding facade).** Preserved the preceding
  consolidation entry verbatim but rejected its rebuild-on-read implementation after behavioral testing
  exposed model-level contradiction, lost partial reads/provenance, simulated retraction, and a changed
  finite API. Extended the canonical core so `GeneralNet` retains its closure engine and delegates live
  `CellState`/support reads, premise assertion, retraction, and finite search. Added optional directional
  dataflow outputs plus core `convert`/`combine`/`allDifferent` mechanics. Rewrote friendly as only
  `PayloadAdapter`, premise-name mapping, methods, CE operations, and direct General calls; restored
  Set-shaped finite cells, raw scalar `C=BOT` with unaffected `F=33.8`, all four barometer/provenance stages,
  named retraction, interval behavior, and Sudoku. Added a core regression for cell-local contradiction
  and live restore. Verified all three scripts under FSI. Appended a correctness-gated same-process benchmark
  (Optimized 14.30x General on the timed portable row; General live edit cycle 44.2 us best) without altering
  any prior benchmark entry. Optimized propagation remained unchanged; streaming and Optimized edit seams
  remain the next work order.

- **2026-07-10 - Codex (enduring facade/test split).** Made
  [propagator-friendly.fsx](../propagator-friendly.fsx) a silent, load-safe library facade and moved all
  executable examples, fixtures, renderers, regressions, differential checks, and benchmark entry point to
  [propagator-friendly.tests.fsx](../propagator-friendly.tests.fsx). Removed `AllDifferent` from
  `Constraint`, `General`, friendly `Network`, and `Ops`; the Sudoku test now supplies its all-different
  policy locally through generic `Constraint.relation` and the facade's thin `Constrain` delegation. Made
  core `Gac` private and gave the independent fixpoint harness a test-local copy. Moved the enduring
  `Scalar`, scalar meet, `Transform`, and `Affine` helpers into the canonical core alongside `Interval`.
  Verified silent direct loads of core and friendly, the consolidated correctness suite, and opt-in
  benchmark mode under `dotnet fsi`; neither propagation hot loop changed, and earlier benchmark history
  was left untouched.
- **2026-07-10 - Fable (Tier 0) housekeeping audit.** Accepted the three-pass consolidation (§10):
  independently re-ran the full suite (PASS, fidelity bit-exact incl. barometer and raw-scalar `C=BOT`),
  verified silent loads, LIFO reclaim on both faces, the untouched optimized hot loop, the unviolated
  third-slice work order (preflight still lower-time by design), honest engine headers, and
  convention-compliant benchmarks (stable 14.3× ratio). Praised the in-the-open supersession of the
  rebuild-on-read facade as the append-only rule working. Referred TWO executor reversals of ratified §13
  decisions to Deen: the `AllDifferent` removal from the friendly surface (recommend restoring as facade
  sugar over `Constraint.relation`) and `Transform`/`Affine` moving into core; AGENTS.md's example list
  should track his ruling. Minor: dangling benchmarks link to the consolidated-away
  `propagator-surface-vocab.tests.fsx` — fix by dated addendum.
- **2026-07-10 - Deen ratification (core-membership principle).** Resolved both items the audit referred:
  `AllDifferent` stays out of core (it was testing-specific; outside core its form is free — no facade
  sugar mandated), superseding §13's method listing; `Transform`/`Affine` stay in core (ergonomic and
  general), refining §13's "external/supplied" call, which targeted demo-overfit helpers only. Principle
  named: core membership = durable architectural role, not reference count or abstract genericity —
  fencing the executor tendency to be unhygienic about modularity and myopic about design. AGENTS.md's
  core-membership rule and example list ratified as written.
- **2026-07-10 - Codex + Deen (AsyncRx UX comparison).** Compared the friendly propagator facade with the
  repository's AsyncRx user idiom and recorded AsyncRx as a benchmark for semantic economy and coherence,
  not a mandate to copy its compact module topology. Deen sharpened the criterion: duplication signals a
  design problem, while separate homes are correct for orthogonal concepts; never merge merely to reduce
  visible surface when doing so conflates concepts. Applied that test to preserve the distinct
  `Domain`/`Constraint`/`Network` and General/Optimized roles while flagging method/`Ops` duplication, the
  finite facade's authored-value/candidate-view/Set-representation conflation, overloaded `Assert`
  semantics, and the weak boolean `Solve` result as the next UX questions.
- **2026-07-10 - Fable (Tier 0) + Deen (UX refinement reviewed; CE ruled).** Accepted the refinement
  proposal as the fourth-slice basis (§10); recorded the duplication-vs-plurality test as the companion of
  the core-membership principle (each fences one direction of executor myopia). The method/`Ops`+CE fork was
  caught as a quiet reopening of the 07-06 Option C ratification and referred; Deen ruled: the CE and `Ops`
  stay as a declared *alternate language* — a function-shaped dialect over the same once-authored semantics,
  strongly differentiated outward — plurality, not duplication, and not removable by consolidation passes.
  Two sharpenings locked for the eventual fourth slice: the finite refit must keep set-valued restriction
  (own verb or explicit deferral, never silent loss), and the explicit `Solve` result's solved view must be
  a pure snapshot of the one live net (rebuild-on-read precedent). Orthogonal to the third slice; either
  execution order works.

- **2026-07-10 - Codex (fourth friendly slice implemented + verified).** Refit the facade adapter to
  separate exact payload from observed view; finite cells and relations now use ordinary authored values,
  reads remain `Set<'value>`, and authored-order filtering preserves deterministic search. Replaced the two
  ambiguous `Assert` overloads with `Given`, `Assume`, and permanent/named `Restrict`; added readable
  permanent provenance; and made `Solve` return the core `Solution option` snapshot directly. Preserved
  methods and `Ops`/CE as equal delegated dialects and added parity coverage. Silent loads, the complete
  correctness suite, and two full benchmark passes succeeded; the benchmark ratios varied from 11.06x to
  6.83x with no timed core code changed, so both runs were recorded as environmental evidence. No core
  engine or hot loop changed.
- **2026-07-10 - Fable (Tier 0) gate (fourth slice CLOSED; benchmark spread diagnosed).** Accepted the
  fourth slice (§10): independent full-suite re-run PASS bit-exact; both locked sharpenings honored
  (`Restrict` in both lifetimes with authored-order filtering; `Solve` = the core's pre-existing `Map`
  snapshot, no second handle); CE ruling honored (methods and `Ops`/CE equal dialects, `restrict`
  spellings added); `given(<cell>)`/`restrict(<cell>)` provenance closes the `p3` flag; the finite refit
  reached representation depth (`'state = 'value`, no `Set.singleton` wrapping). Added the explicit
  alternate-dialect bullet to AGENTS.md's "Prefer Direct Designs". Benchmark spread *diagnosed*: the
  machine was on the Windows Power saver plan on AC — CPU sampled at 40–57% of base frequency during a
  third Tier-0 run that still beat both recorded runs (ratio 9.89×; day's bracket 6.83×–14.37× on
  identical timed code) — clock-governor variance, not the refit; benchmarks.md addendum extends the
  convention (record power plan; prefer high-performance for recorded runs). The dangling tests-link
  minor was closed by the executor's addendum. Board: third-slice work order remains the one pending item.

- **2026-07-10 - Codex + Deen (WFC work order reopened before execution).** Deen clarified that Friendly
  is the library's UX standard and WFC is its flexibility test: `network { }` may remain General-only, but
  WFC may not expose model/lowering/representation/constraint plumbing. The historical
  `propagator-wfc.fsx` is now preserved unchanged for implementation history and same-run timing comparison;
  new work belongs in a second WFC-only file whose goals are high optimization, no visible constraint
  plumbing, and clear approachable authoring. Codex reviewed the pending third-slice order and found it
  requires a recut before execution: its in-place file instruction and deliberately non-60fps GAC cost-curve
  target are superseded; generic collapse/reset/observation needs require discussion; finite event payload,
  replay baseline, premise-width test relocation, stale stub notes, generation result shape, and comparative
  benchmark acceptance remain unresolved. No implementation was authorized by the review.

- **2026-07-10 - Fable (Tier 0) reversal + recut work order.** Accepted the Codex recut review in full.
  Fable's first response to finding 2 recommended keeping the fast machinery WFC-local; Deen challenged it
  and the challenge held: the optimized face's 64-value single-word domain and tuple-enumerating GAC were
  Sudoku-fixture overfit carried as architecture, and the historical `Wfc.Engine`'s machinery is generic
  finite-CSP mechanism (multiword bitset domains, relations compiled to per-value support masks,
  driver-side policy), mislabeled WFC-specific by its filename. Deen ratified: fix the core's finite story.
  Replacement work order cut (§10), superseding the same-day third-slice order: (A) width-parameterized
  multiword optimized face, binary relations compiled to mask tables at lowering, W=1 no-regression canary;
  (B) `generate` under the surviving laws (verb split, premise invariant, pure seeded PRNG, one-map result),
  preflight relocation with the guard row honestly rewritten, observation as `CellState` snapshots with a
  subscribe-time baseline and last-write-wins replay; (C) new `propagator-wfc-app.fsx` (historical WFC file
  preserved), domain-shaped Friendly authoring, four oracles, comparative same-run benchmark vs. the
  prototype. Premise width ≤ 64 remains, honestly, a solve capability.

- **2026-07-10 - Deen + Codex (durable generalized finite-face plan).** Clarified the final outward
  boundary after the recut: Optimized is internal; Friendly must provide the clean low-overhead UX for
  optimized finite execution as well as ordinary use; easy relations remain easy while arbitrary n-ary
  relations, repeated scopes, custom topologies, generation, and observation remain understandable. Added
  the signed enduring [Generalized Finite Face and WFC Application Plan](propagator-finite-face-plan.md),
  including the grouped-relation resolution, hidden multiword implementation, WFC-only application boundary,
  ordered phases, acceptance matrix, benchmark protocol, risks, and append-only execution record. The plan
  is retained after implementation rather than treated as a compaction handoff.

- **2026-07-10 - Fable (Tier 0) plan acceptance gate.** Accepted
  [propagator-finite-face-plan.md](propagator-finite-face-plan.md) after a line-by-line check against the
  recut work order; its "Ratified" header is true as of this gate. All locked rules survive intact. Two
  Codex improvements over the order credited openly: the section-11.3 catch that the order's "may `#load`
  the historical file" instruction was unworkable (the preserved file ends with `exit (main ())`), resolved
  by a two-process comparative session with preservation ranked above benchmark convenience; and the
  grouped-relation representation (`Constraint.relations`/`RelateMany`), which makes the order's mandated
  per-distinct-relation table dedupe structural instead of hanging it on forbidden closure identity, plus
  forward-and-reverse tables with an independent asymmetric-relation test. Watch items recorded in the
  plan's section 13 for future gates: the Phase 5 Friendly delegation refactor (dual-mutable-model risk;
  audit against the plan's own stop condition) and checking Phase 1's no-plumbing-nouns criterion against
  the actual authored examples. Execution may proceed on the plan as written.

- **2026-07-10 - Codex (generalized finite face implemented).** Completed the accepted recut and durable
  plan: grouped relations; one runtime-width optimized store; compiled asymmetric binary support tables;
  generic n-ary fallback; solve-time premise preflight; stable seeded generation; snapshot observation;
  and a one-authority Friendly delegation record. The two Fable watch items passed their explicit audits:
  no dual mutable model exists, and the concrete Friendly examples contain no backend/lowering/
  representation nouns. Added the separate WFC-only application with independent map, cross-route seed,
  replay, and bounded-failure oracles. Preserved the historical WFC file exactly. Correctness-gated
  benchmarks and the controlled two-process historical comparison were appended to `benchmarks.md`.

- **2026-07-11 - Codex (like-for-like WFC benchmark correction).** Added historical gravity and 3-color
  constraints to the new WFC application's opt-in benchmark path at matching 500x500 dimensions and seed
  42. Corrected the harness so independent validity walks occur outside the stopwatch, retained best/mean
  sampling, and documented the remaining materialization/RNG/search-policy differences. No historical
  source or core library code changed.

- **2026-07-11 - Fable (Tier 0) implementation review gate + proposal acceptance.** Reviewed the completed
  plan execution (phases 0-8) on primary evidence: both pre-flagged watch items pass — Friendly delegates
  through one `CoreOperations` closure record per network with no second mutable model and no propagation
  mechanics in the facade, and the actual WFC/UX examples contain no backend/lowering/representation
  nouns (core-face calls confined to the permitted test-local oracle module). Locked rules verified in
  code (one runtime-width store, per-group forward/reverse tables, premise-free collapse, seeded SplitMix
  discipline, snapshot observation, honest premise split between lower/solve/generate). Historical WFC
  SHA-256 re-verified independently; the full Friendly suite and all four WFC oracles ran green during
  review. One non-blocking finding: the optimized facade's `cellPosition` linear scan (same
  accidental-scan family as the fixed endpoint bug) should become a maintained dictionary. The 2026-07-11
  gravity-optimization proposal in `benchmarks.md` is ACCEPTED with three executor notes recorded in the
  plan's section 13: candidate 1 must empirically pass the seed-42 fingerprint despite being a model
  change; candidate 3's shared scratch needs an observer re-entrancy stance first; candidate 2's cached
  baseline is the riskiest item and needs an assert/retract-between-generates refresh test. Optimization
  work may proceed on the proposal as written.

- **2026-07-11 - Codex (gravity optimization work order completed).** Began by locking exact row-major and
  SHA-256 output, then profiled construction, reset, propagation, picker, extraction, and `Map` construction.
  Retained the cell-position dictionary, WFC-local universal-direction elision, one-authority settled
  state/support cache, scalar one-word arc path, precomputed full-row flags, and direct singleton extraction.
  Skipped custom picker and arc-layout machinery for lack of measured payoff. Two F# type-inference failures
  during test/helper additions were corrected with explicit annotations; no semantic gate failed. The first
  historical run timed out before generation and was discarded as incomplete; the larger-timeout rerun
  completed all rows, passed correctness, and remained byte-identical. Both 500x500 fingerprints and all
  finite, multiword, provenance, observer, retraction, and restart tests passed. The rewritten
  [finite-face record](propagator-finite-face-plan.md) contains the candidate decision table and limitations;
  [benchmarks.md](benchmarks.md) contains every established current Sudoku/live-edit timing, the full cyclic
  ladder, matching WFC checkpoints, candidate evidence, and completed historical timing table.

- **2026-07-11 - Codex (Friendly ramp gap closed).** Generalized only the WFC application's local grid wrapper
  so integer ramp domains use the existing Friendly surface, then added exact small ramp laws and opt-in
  500x500/64x64 timing. The first run exposed per-arc multiword allocation, absent-value scans, and especially
  global arc replay during rebuild. Retained invocation-local scratch, set-bit iteration, and an immutable
  constraint-only structural baseline; added nested observer re-entry coverage. Final ramp512/ramp128 rows
  were within 1.13x/1.32x of the specialized historical rerun while all finite and WFC controls remained
  correct. Detailed checkpoints and comparison boundaries were appended rather than rewriting prior history.

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
