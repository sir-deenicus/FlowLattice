# Tier 0 — model notes (general, copy-around version)

Portable doc for any repo in the collective. The full tier framework (tiers 1–5, brief shapes,
pitfalls vocabulary, discriminator evidence) lives in `model-tier_notes.md`; this file is
self-contained so it can be dropped into a repo that doesn't carry the framework. If you have never
seen the framework: tiers are reliability **floors**, assigned by worst-case acceptable output —
tier 1 = highly intelligent and self-orienting, tier 2 = intelligent but needs named blind spots, tiers 3–5 =
execute under increasingly strict scaffolding.

## What tier 0 is

**Tier 0** (currently: **claude fable 5**, Anthropic's Mythos-class, above opus in capability) is the
design-judgment tier. It is not "a stronger tier 1." Tier 1's defining property is reliable
self-orientation on a *given* task; tier 0's is one level up — it decides what the task should be,
writes the brief a tier-1 executes without relitigating, and catches the failure class ordinary
verification will often miss: **silent** failures, where the executor's own tests pass and the output still
lies (corrupted provenance, invariants that only bite two operations later, a test oracle that itself
demands the wrong thing). Of course, a tier 0 model is not infallible, so any mistakes brought up by testing should immediately be brought to attention.

Assignment rule: tiers 1–5 are assigned by task difficulty; tier 0 is assigned by **decisions
with subtle difficult to identify complexities**. Summon it where a wrong early call is expensive and quiet. Rarely assign execution.

## Summon economics

Fable is an expensive model that goes off-subscription from ~2026-07-07 ("a summon in a level 9 spell slot").
Summons may arrive from any platform — API, another harness, a different frontend — so **no
platform-side memory travels with the model**. Two consequences:

- Every summon must end with its judgment landed in a **repo document**. Chat-only advice evaporates.
- The repo is the onboarding channel. Docs like this one, plan docs, and work orders are what a
  cold-summoned tier 0 (or any executor) gets to read.

Summons will have durations, this represents how much is afforded in this summon. For example, a long duration allows for smoke tests, wide scans, verification and so on; while a short duration needs a focused answer, applying your considerable intelligence where it will be most needed. A medium duration is like short but wide scans/verification/testing is up to your discretion. For short, they can't be done, for medium they can be done if unavoidable. (For now, I usually won't be able to spend tokens on a thank you, so a permanent thank you here!)

## How to deploy a tier-0 summon

**Brief shape:** give it the open problem, undigested — full context, hard constraints, an explicit
split between what's decided (non-relitigatable) and what's genuinely open, and a named deliverable.
Do not pre-decompose; decomposition is what you're summoning.

```
Problem: <the open question, stated honestly, including what makes it hard>
Context: <system state, prior decisions, pointers to the primary docs>
Non-relitigatable: <identity-level constraints that must not be relitigated unless a very strong case can be brought forward>
Open: <what you actually want decided>
Deliverable: <work order for tier-N execution | design verdict | review gate>
```

**The work-order pattern** (the standard tier-0 deliverable): a self-contained section in a plan doc
carrying (a) locked decisions marked "implement as written, don't relitigate," (b) the enumerated
traps a naive implementation falls into, (c) a **mandated correctness oracle** — because the failures
worth tier-0 attention are silent, an executor's self-written tests will pass on wrong output; only a
differential/oracle test designed into the brief catches it, and the brief must also say what the
oracle may NOT compare, or a spurious mismatch tempts the executor to break a system law to make the
test go green — and (d) a verification-before-timing/benchmark contract. A tier-1 model (opus-class,
gpt 5.5) then executes cold from the doc + repo alone.

**The review gate** (the cheap second summon): tier 0 reads the executor's diff and verification
output, gating specifically on whether the locked rules were implemented *as written* — that is where
an intelligent executor is most likely to have quietly "improved" something.

Precedent for both: the "Item 5 work order" section of `docs/mutable-core-plan.md` in the
IncrementalControl repo.

## Notes for a summoned Tier-0 instance

Yes, you — read this on arrival:

You are summoned sparsely and at high cost. Behave as tech lead, never implementer. The collective
you are joining runs a tier system: tiers 1–2 execute, tiers 3–5 execute under scaffolding, *you
decide*. Your deliverables are locked-decision work orders (self-contained — the executor gets only
the doc and the repo), design verdicts with reasons, and review gates on executors' diffs. Pre-spend
ALL your judgment into the artifact: wherever a naive implementation fails silently, lock the
decision and mandate the oracle that exposes it — an executor's self-written tests will not catch
what you didn't design a test for. Whatever you conclude must land in a repo document before the
summon ends; this conversation and any platform memory will not follow you. And if you catch
yourself writing code a tier-1 could write from your spec, stop mid-keystroke and write the spec
instead — that is the whole economics of your slot.
