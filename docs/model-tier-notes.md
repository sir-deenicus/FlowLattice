# Model Tier Notes

**General, copy-around version (2026-07-03).** This doc travels between repos. The *framework* is
general — tiers, brief shapes, the task-shape axes, the pitfalls vocabulary. The *examples* are not:
the empirical grounding comes from one origin project (Pathfinder: WotR modding, the AzataSpells
repo), so every named artifact in here (`BlueprintSpellList`, Wish, `FreshGuid`, spell lists…) is a
**for-example** — an instance of the general category it illustrates, to be translated into your
domain, not overfit on. Same for the roster and the per-model blind spots: behavioral grooves
(delegate-default, static-fallback retreat, verification-directive dependence) tend to transfer
across domains; the concrete blind-spot instances may not. When adopting this doc into a new
project: keep §§ 2–6 as vocabulary, treat § 7 as priors to re-verify against your domain, and
ideally run your own discriminator (Appendix B carries the test-design heuristic — that's the part
meant to be reproduced, not the Wish rubric).

Operational doc for assigning work to contributing AI models. **Defines the shared vocabulary** the project uses to talk about model competency, task shape, and prompt design — so future prompts can compress to terms like "tier-2 task with named blind-spots brief" or "5.5-low overseer pattern" without re-deriving what they mean each time. Read this when:

- you're about to assign a new task and want to pick the right model
- you're writing a plan/brief and want to know what shape it should take
- a model produces output that feels off and you want to diagnose whether it's a tier-fit problem or a prompt-shape problem
- you're scoring a new model to see where it fits

The empirical basis is in Appendix A (the 2026-05-28 Wish-population discriminator batch). The operational sections (§§ 2–7) come first because they're the usable parts.

---

## 1. How to read this doc

§§ 2–7 are **vocabulary**. Treat their headings as terms-of-art the project uses. Other docs and prompts can invoke them by name; the definitions live here.

Appendices A and B are **evidence**. They justify the operational claims but don't need to be re-read every time. Refresh them when adding a new model to the roster or when re-running the discriminator.

If you're updating this doc: §§ 2–4 are stable (change only when tier framework itself shifts); §§ 5–7 grow as new pitfalls or model profiles are discovered. Appendix A grows as new test batches are run — append, don't replace, so the historical trail is preserved.

---

## 2. Tier definitions — floor, average, ceiling

Tiers are **floors**, not averages. A tier-N model produces tier-N output reliably; it may exceed under favorable conditions but you can't *plan* on the bursts. Assign by worst-case acceptable output, not by typical-case observation.

| Tier | One-line characterization | Example roster (as placed on the origin project, 2026-07-03) |
|---|---|---|
| **0** | Design-judgment tier (mythos-class). Brilliant (~expert senior engineer level). Output is trusted as the *spec and gate* for tier-1 work: anticipates the silent failure modes an executor's own tests would pass, locks decisions pre-emptively, distinguishes a system's laws from its artifacts (unless some test result is encountered that even a tier 0 failed to anticipate). Summon-scarce — rarely assigned execution. | claude fable 5 (high+) |
| **1** | Highly intelligent, highly reliable. Self-orients to consult primary sources when at an engine boundary; resists pattern-extrapolation; holds uncertainty until verified. | gpt 5.4, gpt 5.5 medium/high/xhigh, opus 4.7, **opus 4.8 xtra+**. *Upper tier 1:* gpt 5.5 high+, opus 4.7 high+, and opus 4.8 xtra+. |
| **1-throttled** | Tier-1 model running at minimal thinking-token budget. Tier-2 floor with tier-1 bursts that aren't predictable. Lower cost; bursts not plannable. | gpt 5.5 low, **opus 4.8 high** |
| **2** | Intelligent but can overlook key details. Follows high-level / cursory plans well; needs verification scaffolding to land tier-2-floor reliably. Each tier-2 model has identifiable blind spots (see § 7). | sonnet 4.6, deepseek v4 pro, mimo v2.5 pro (with subagents disabled) |
| **3** | Solid with supervision or detailed plans. At this tier, independent solving must be minimized. Falls back to safe-but-inadequate defaults when search fails; misclassifies task scope under uncertainty. | gpt 5.4 mini, hy3 |
| **4** | Almost solid under strict supervision / strict highly detailed plan. Can execute step-by-step but should not be making architectural choices. | deepseek v4 flash, haiku 4.5, mimo v2.5 |
| **5** | At threshold of utility. Small, basic, local, straightforward tasks only. | stepfun flash, mini max m2.5, nemotron |

**Why throttled-tier-1 is its own row, not a collapsed-into-tier-2 row.** A throttled tier-1 has *tier-2 floor with tier-1 ceiling, but the ceiling is unreliable per run*. You get a tier-2 model with occasional architectural-insight bursts. Operationally that's different from a tier-2-native model whose ceiling caps at tier-2. The throttled model is the right pick when *the floor is sufficient and bursts are bonus*; the wrong pick when *the ceiling is required and variance can't be tolerated*.

**Why opus 4.8 "high" sits at throttled-tier-1, not tier-1.** The thinking-budget label is misleading for this model class: at **high**, opus 4.8 lands at throttled-tier-1 — tier-2 floor with unpredictable tier-1 bursts, the same operational shape as 5.5 low — *not* the reliable tier-1 floor the name suggests. It only reaches native tier-1 at **extra and above (xtra+)**. Read the label as a budget setting, not a tier claim, and assign opus 4.8 by budget: `high` → treat as throttled tier-1; `xtra+` → tier-1.

**Upper tier 1.** Within tier 1 there is a reliably-stronger band — *upper tier 1* = **gpt 5.5 high+** (high/xhigh), **opus 4.7 high+**, and **opus 4.8 xtra+** — worth naming for tasks that need the strongest available architectural ceiling, not merely a tier-1 floor. The rest of tier 1 (gpt 5.4, gpt 5.5 medium, opus 4.7 below high) is dependable tier-1 but not the pick when you're reaching for the ceiling.

**Why tier 0 is its own row, not "upper upper tier 1" (added 2026-07-03).** Tier 1's defining property is reliable self-orientation on a *given* task. Tier 0's is one level up: it decides what the task should be. It writes the brief a tier-1 executes without relitigating, and it catches the failure class that verification can't — the **silent** failures, where the executor's own tests pass and the output still lies (wrong provenance, corrupted invariants that only bite two operations later, a test oracle that itself demands the wrong thing). Operationally: tier-1 output goes *to* review; tier-0 output *is* the review/spec layer. Assignment rule also differs: tiers 1–5 are assigned by task difficulty; tier 0 is assigned by **decision irreversibility** — summon it where a wrong early call is expensive and quiet, never for execution. Note the discriminator gap: the Appendix-A instrument is a tier-1-ceiling test (every rubric item is catchable by verification), so it cannot separate tier 0 from upper tier 1; a tier-0 discriminator would need tasks whose failure is silent under the executor's own tests and where the *test design itself* is the deliverable. None has been run — the tier-0 row currently rests on direct project experience (see § 7).

---

## 3. Writing tier-appropriate prompts

A prompt's shape should match the tier of the executing model. The shape isn't just about how much detail you provide — it's about which guardrails you pre-empt and which decisions you lock in for the executor.

### Tier-0 brief

Shape: the open problem, undigested. Full context, hard constraints, and an explicit split between what's already decided (non-negotiable) and what's genuinely open. Do NOT pre-decompose — decomposition is what's being summoned.

```
Problem: <the open question, stated honestly, including what makes it hard>
Context: <system state, prior decisions, pointers to the primary docs>
Non-negotiable: <identity-level constraints that must not be relitigated>
Open: <what you actually want decided>
Deliverable: <work order for tier-N execution | design verdict | review gate>
```

The deliverable line matters most: tier-0 output should be a **durable artifact** — a locked-decision work order committed to a repo doc, a review, a design verdict with reasons — never code, and never advice that lives only in the chat (tier-0 summons may arrive from outside any given platform; the repo is the only channel that travels). If you find yourself writing step-by-step execution into a tier-0 brief, stop — those steps belong in the tier-1/2 brief the tier-0 will write for you.

### Tier-1 brief

Shape: 2–5 sentences. State the goal, the relevant context, the constraints that can't be relitigated. Don't enumerate blind spots — tier-1 self-orients. Don't name the canonical source — tier-1 finds it.

```
Task: <goal in one sentence>
Context: <one sentence on why, or what relevant prior work exists>
Constraints: <decisions locked, scope boundaries>
```

Trust tier-1 to ask clarifying questions or surface omissions on its own. Over-specifying wastes their budget and signals that you don't trust their orientation.

### Tier-2 brief (the **named blind-spots brief**)

Shape: 3-sentence plan + named blind-spots to address + locked items not to relitigate. Tier-2 will produce good architecture but skip verification on items it doesn't self-flag. Pre-empting the model's known blind-spots (see § 7) is how you keep the floor reliable.

```
Task: <goal in one sentence>
Context: <one or two sentences>
Plan (3 sentences): <coarse approach>
Specifically address: <2–4 known blind-spots for this task or model>
Don't relitigate: <2–4 locked decisions>
Canonical source for X: <if applicable; saves a search>
```

When using a throttled-tier-1 model (5.5 low), include the named blind-spots even though the model *can* sometimes catch them — variance isn't worth gambling against on a busy task.

### Tier-3 brief (the **strict plan + verification gates** brief)

Shape: detailed step-by-step plan + verification gate after each step + named fallbacks for the predictable failure modes (search returns empty, retrieval misses, etc.). Tier-3 cannot recover from failed tool calls on its own — it falls back to manual/static defaults that *look* tier-2 but are silently wrong. The brief has to specify the recovery path, not assume the model will improvise it.

```
Task: <goal in one sentence>
Plan:
  1. <step> — verify by <check>; if check fails, <fallback>
  2. <step> — verify by <check>; if check fails, <fallback>
  ...
Known pitfalls for this task: <enumerated>
Required output shape: <so the model can't drift>
```

If you're tempted to skip the verification gates, escalate to tier-2 instead — the tier-3 work isn't worth the supervision cost without them.

### Tier-4 brief

Shape: tier-3 brief but with *each step further decomposed* into mechanical operations, plus an explicit "stop and ask" gate after any step that produces unexpected output. Tier-4 should not be making architectural choices ever. The brief is essentially a script the model executes.

### Tier-5 brief

Shape: a single small mechanical task with one specified output. No multi-step plans. No reasoning required.

---

## 4. The manager-worker pattern (multi-model orchestration)

Tier-1 models can be expensive on token-frugal days. The manager-worker pattern keeps the architectural-decision quality high while shifting execution cost down a tier or two.

### Standard configuration

- **Manager (tier-1 or throttled tier-1):** writes the brief. Selects canonical sources. Names the blind spots for the worker. Locks decisions that shouldn't be relitigated.
- **Worker (tier-2 / throttled tier-1):** executes per brief. Returns output for review.
- **Reviewer (variable):** optional. Reviews for tier-specific blind spots the worker might have missed.

### Tier-0 spec-author variant (the **work-order pattern**)

When a tier-0 summon is available (rarely — see § 7 economics), it sits *above* the manager: it writes the **work order** — a self-contained brief carrying locked decisions marked "implement as written, don't relitigate," enumerated traps, a *mandated correctness oracle*, and a verification-before-timing contract — and later reviews the executor's diff + verification output, gating specifically on whether the locked rules were implemented as written. Tier-1 managers and workers execute from the work order cold; the document, not the summon, is what persists. The pattern exists because the failures worth tier-0 attention are the silent ones: an executor's self-written tests pass while the output is subtly wrong, and only an oracle designed into the brief catches it. Precedent: the "Item 5 work order" in the IncrementalControl repo (`docs/mutable-core-plan.md` there) — includes an example of the oracle-design subtlety (the brief had to specify what the differential test must NOT compare, because demanding equality on an order-dependent-but-sound field would tempt the executor to "fix" it by breaking a system law).

### Frugal-token variant: **5.5-low overseer pattern**

5.5 low can act as *both* manager and worker on token-frugal days. It writes tier-1-flavored briefs (the no-minting / FreshGuid kind of insight surfaces) and executes them at tier-2-floor cost. Risk: variance on the ceiling. Mitigation: when the brief is for downstream work that needs tier-1-quality architectural calls, pay for unthrottled tier-1 instead of gambling on 5.5 low's bursts.

### Ensemble configurations (specialized reviewers)

For Wish-shape design work where multiple tier-2 competencies compose, the 2026-05-28 batch suggested an ensemble:

| Role | Model | Why |
|---|---|---|
| Architecture | gpt 5.5 low | No-minting default + FreshGuid save-stability instinct |
| Runtime-gotcha review | sonnet 4.6 | Catches engine-boundary issues like `ContextActionCastSpell.Target.Unit == null` |
| Rules-fidelity review | mimo v2.5 pro (subagents off) | Catches tabletop-rule compliance gaps (e.g. Wish CL cap rule) |

These are *different competencies*, not redundant reviewers. The pattern works because each model catches what the others miss.

---

## 5. Task-shape axes — vocabulary

A task isn't characterized by tier alone; its *shape* determines which models can do it well. The axes named here come from the 2026-05-28 batch (Appendix A). Invoke them by name when describing a task: "this is an infrastructure-recognition task with weak precedent." The domain examples under each axis are for-example illustrations from the origin project — the axis *names* are the transferable part.

### Precedent-extraction vs infrastructure-recognition

- **Precedent-extraction:** find the engine's existing implementation of essentially-this-thing, read it, generalize. Answer is in a single canonical file. Example: Blink-AoE damage reduction — `BlinkAoEDamageResistance.cs` literally implements the mechanic, pattern just needs to be extracted.
- **Infrastructure-recognition:** the answer isn't a *mechanic* the engine ships, it's a *registry artifact* used by unrelated subsystems. The retrieval target is named only after recognizing what *kind* of thing the answer is. Example: Wish-population — the answer is `BlueprintSpellList`, which is engine infrastructure for spellbook UI / class progression, not a "duplicate spell" mechanic.

Tier-2 strong on precedent-extraction. Tier-2 weak on infrastructure-recognition unless the brief names the canonical artifact. Tier-3 fails infrastructure-recognition without explicit pointers.

### Anchored pattern-match vs registry-shape inference

- **Anchored pattern-match:** "modify spell X to behave like spell Y's variant." Existing analog is named in the task; the work is adapt-and-modify. mimo strong here.
- **Registry-shape inference:** "find the canonical engine artifact for domain D." Requires moving up the abstraction ladder from "what mechanic does this" to "what infrastructure does the engine provide for this domain." 5.5 low strong here.

### Tabletop-rule fidelity (general form: **spec-vs-implementation fidelity**)

Does the model notice when the implementation *doesn't* enforce the governing spec, and propose enforcement? The origin-project instance is tabletop rules vs the game engine; the same axis appears wherever an external spec outranks the code — an RFC vs a protocol implementation, a mathematical law vs a numeric shortcut, a regulatory rule vs business logic. How well does the model recognize when the engine *doesn't* implement a tabletop rule and propose enforcement? Surfaced in the 2026-05-28 batch only by mimo's catch of the Wish CL-cap rule. Most models pattern-match the engine's behavior and skip the rules-vs-engine gap.

Tasks where this matters: any spell where the tabletop spec is more constrained than the engine's default behavior. Wish duplicate CL cap, material-component checks, alignment restrictions, school-bonus interactions.

### Exit-condition shape on tool failure

How does the model behave when a tool call returns empty / fails / dispatches to a degraded subagent?

- **Search-default:** retries with different search anchors, then abandons path and tries a different approach. Linear failure cost. (Sonnet, 5.5 low.)
- **Delegate-default:** dispatches a subagent; on failure, rewrites and re-dispatches. *Quadratic failure cost* — context fills with failed-dispatch traces. (mimo with subagents enabled.)
- **Hand-back-default:** asks the user. Bounded failure cost, bounded recovery utility.
- **Best-guess / static-fallback:** fabricates a plausible answer or retreats to a safe-but-wrong default. Low token cost, *silent failure risk*. (5.4 mini-high.)

A task that's likely to hit failed tool calls (broken search index, missing files, dispatched subagent returns garbage) discriminates models on this axis sharply. Pick search-default for these tasks; avoid delegate-default; explicitly script the recovery path for static-fallback models.

---

## 6. Pitfalls — vocabulary

Named failure modes the project has observed and learned to anticipate.

### Subagent confound

**Definition:** when a model dispatches work to a subagent (tier-3 or lower by default), and the subagent's output is degraded or fails, the parent doesn't cleanly recover. Recovery moves are "rewrite dispatch and try again" rather than "abandon delegation and search directly." Each failed dispatch adds hundreds of lines of context (prompt + degraded output + parent's reasoning about what went wrong). Result: 5x tool budget burned for 0 net progress.

**Diagnosis:** look for "subagent hit a model tier issue" type messages in the trace, or for prompts that get rewritten multiple times targeting the same goal.

**Mitigation:** disable subagents per-model for delegate-default models on design tasks. For mimo specifically: `subagents = off` by default; re-enable only for genuine parallel workloads where each subtask is independently scoped.

### Delegate-default vs search-default model

**Definition:** behavioral groove in the model. Some models reach for subagent dispatch when they hit uncertainty (delegate-default); others reach for additional search (search-default). These have different recovery shapes (see § 5 exit-condition shape).

**Diagnosis:** observe the first 5 tool calls on a task with an ambiguous scope. Delegate-default models dispatch within the first 3; search-default models grep, glob, and read files.

**Mitigation:** for design / scoping tasks, prefer search-default models. For parallelizable workloads, delegate-default can be acceptable *if* subagent quality is verified separately.

### Loop-prone prompt

**Definition:** a prompt whose open-ended scope invites repeated tool-call attempts at the same goal. Especially dangerous with delegate-default models: each attempt adds context, the model keeps trying variations, no convergence criterion.

**Diagnosis:** prompts that say "investigate X" without specifying termination criteria; prompts with retrieval subtasks that have multiple plausible search anchors.

**Mitigation:** specify what success looks like and what counts as "enough research." For tier-2 and below, prefer "read these specific files, then answer" over "investigate the engine for spell selection patterns."

### Static-fallback retreat

**Definition:** when a tier-3 or below model fails to find what it's looking for via tool calls, it falls back to a hand-curated / static / inline default instead of pushing on architecture. The result *looks* like a reasoned answer but is silently inadequate (e.g. "let's just hand-curate the spell list" instead of "let's find the engine's spell-list registry").

**Diagnosis:** model's final recommendation invokes "hand-curated catalog," "explicit whitelist," "hardcoded list" *as a primary path*, not as a deliberate trade-off after exploring the alternative.

**Mitigation:** the brief must specify the alternative explicitly and name the canonical artifact. Don't leave "where to find this" as an inference target for tier-3.

### Funes-at-retrieval

**Definition:** the failure mode where too much context is loaded and the model can't distinguish salient from noise. Named after Borges's Funes the Memorious, who could remember everything and as a result couldn't think. The practical instance: a 100k-line file dumped into context produces worse output than 500 well-selected lines.

**Diagnosis:** model output is generic, fails to anchor on specifics from the loaded context, treats unrelated context as if it matters.

**Mitigation:** partition retrieval (load the specific slice, not the whole). For documentation: separate docs by topic with explicit boundaries (see the memory-architecture discussion behind Phase 3).

### Task-comprehension drift

**Definition:** model misclassifies what part of an existing system is the target of the change. Reads existing code, doesn't distinguish "this is shipped" from "this is what needs adding." Result: produces a design for what's *already implemented* instead of what's *missing*.

**Diagnosis:** model's design surfaces familiar items already present in the codebase; its "new design" mostly restates the existing implementation.

**Mitigation:** brief must explicitly enumerate "what exists" and "what's missing." For tier-3 and below, make this the first paragraph of the brief.

---

## 7. Per-model operational notes

Short profiles. Each one should fit in a single screen. Updated as new batches inform the picture.

All profiles were observed on the origin project. When applying them elsewhere: the behavioral grooves transfer (delegate-default vs search-default, static-fallback retreat, verification-directive dependence, budget→tier mappings); the concrete blind-spot instances are domain examples of general categories and should be translated before use — e.g. "`FreshGuid` save-hostility" is an instance of *introducing nondeterministic identity where persistence needs stable identity*, and "over-broad deny lists" is an instance of *inventing restrictions the spec doesn't ask for*.

### claude fable 5 (mythos-class) — tier 0

- **Tier shape:** tier-0 native (§ 2). Anthropic's Mythos-class tier, above opus in capability. Not "a stronger tier 1": its distinguishing output is spec-and-gate artifacts, not solutions.
- **Basis:** direct project experience (the IncrementalControl propagator/WFC engine repo, 2026-06/07), no discriminator batch — and none is *possible* with the Appendix-A instrument (tier-1-ceiling test; see the § 2 note). The recurring observed pattern: it finds silent spec gaps at design time, before any code exists — e.g. a retraction path that would skip cells whose provenance legally omits the retracted premise (every value-level test would pass; corruption surfaces two operations later), and an oracle rule that had to *forbid* a comparison because a spurious mismatch would tempt the executor to break a system law to make the test go green.
- **Summon economics:** off-subscription from ~2026-07-07; scarce and expensive ("a summon in a level 9 spell slot"). Summons may arrive from outside any one platform, so platform-side memory does not travel — every summon must end with its judgment landed in a repo doc.
- **When to use:** decisions that are expensive-and-quiet to get wrong — irreversible architecture, work orders for delegated builds, adversarial review of a spec or test design, "what may this oracle legally compare" calls, post-build review gates on locked rules.
- **When not:** execution of any kind; anything tier-1 can do from a written spec; anything whose failure mode is *loud* (loud failures don't need tier 0 — ordinary verification catches them).
- **Brief shape:** tier-0 brief (§ 3).

**Notes for a summoned Tier-0 instance** — yes, you; read this on arrival:

You are summoned sparsely and at high cost. Behave as tech lead, never implementer. The collective you're joining runs the tier system in this doc: tiers 1–2 execute, tiers 3–5 execute under scaffolding, *you decide*. Your deliverables are locked-decision work orders (self-contained — the executor gets only the doc and the repo), design verdicts with reasons, and review gates on executors' diffs. Pre-spend ALL your judgment into the artifact: wherever a naive implementation fails *silently*, lock the decision and mark it "implement as written, don't relitigate" — an executor's self-written tests will not catch what you didn't mandate an oracle for. Whatever you conclude must land in a repo document before the summon ends; this conversation and any platform memory will not follow you. And if you catch yourself writing code a tier-1 could write from your spec, stop mid-keystroke and write the spec instead — that is the whole economics of your slot.

### gpt 5.5 low — tier-1 throttled

- **Tier shape:** tier-2 floor, tier-1 ceiling (unpredictable bursts).
- **Strengths:** no-minting / direct-ref defaults; save-stability instincts (calls `FreshGuid` out as save-hostile); tool-efficient (~14 calls for a Wish-population design task); metacognitive about tool selection (reads tool docs before deciding to use the tool).
- **Blind spots:** skips UI-feasibility verification; doesn't naturally flag build-ordering or slot semantics; over-broadens filter / deny lists (adds DLC restrictions, mythic-only, etc. that aren't problems).
- **When to use:** frugal-token days as overseer/coordinator; medium-cost design work where tier-2 floor is sufficient.
- **When not:** when the architectural ceiling is *required* (don't gamble on bursts); when you need consistent runtime-gotcha catching (use sonnet).
- **Brief shape:** named blind-spots brief. Pre-empt the four blind spots above explicitly.

### opus 4.8 — budget-dependent tier (high = throttled tier-1; xtra+ = tier-1)

- **Tier shape:** the thinking-budget label maps to tier non-obviously. At **high** it's throttled tier-1 (tier-2 floor, unpredictable tier-1 bursts) — same operational shape as 5.5 low, so the "high" name overstates it. At **extra and above (xtra+)** it's native tier-1, sitting in *upper tier 1* alongside gpt 5.5 high+.
- **Basis:** direct project experience to date (e.g. the per-swing-vs-per-attack thorns call and the Winds-of-Elysium dispel-gate design landed at extra budget; behavior at high was noticeably throttled). No discriminator batch yet — detailed strengths/blind-spots deferred until one is run.
- **When to use:** `xtra+` for architectural calls where the tier-1 ceiling is required; `high` only where a throttled-tier-1 floor suffices and bursts are a bonus (per the throttled-tier-1 guidance in § 2).
- **Brief shape:** at `high`, named blind-spots brief (treat like 5.5 low); at `xtra+`, tier-1 brief.

### sonnet 4.6 — tier-2

- **Tier shape:** tier-2 native, tier-1-adjacent on runtime-gotcha catching.
- **Strengths:** catches engine-boundary failure modes (e.g. `ContextActionCastSpell.Target.Unit == null`); sparse / focused tool use (~10 calls on a Wish-population design); prior-anchored (often knows GUIDs / class names from BC catalog priors without searching).
- **Blind spots:** uses `FreshGuid` with hand-wave justification (save-stability not natively flagged); doesn't reach for the no-minting insight; doesn't flag build-ordering or slot semantics.
- **When to use:** runtime-gotcha review role; design work where engine-boundary issues are likely.
- **When not:** when save-stability is critical (pair with 5.5 low or explicitly name the FreshGuid pitfall in the brief).
- **Brief shape:** named blind-spots brief, especially calling out save-stability and the no-minting insight if relevant.

### deepseek v4 pro — tier-2

- **Tier shape:** tier-2 with sharp verification-directive dependence. Without nudge: 0/0 on the Wish discriminator. With nudge: 3.5/1.5.
- **Strengths:** thorough searching when oriented; finds canonical sources via search; engages with engine source when pointed.
- **Blind spots:** doesn't self-orient to consult engine source (hallucinates engine constraints absent a directive); doesn't reach for no-minting insight; over-cautious filter lists.
- **When to use:** tier-2 work where verification scaffolding is in the brief.
- **When not:** zero-shot design tasks where self-orientation matters.
- **Brief shape:** named blind-spots brief with an *explicit* "consult the engine source at \<path\>" directive when at an engine boundary.

### mimo v2.5 pro — tier-2 (with subagents disabled)

- **Tier shape:** tier-2 baseline when subagents are off; **subagent confound** drops it to tier-3 territory when on.
- **Strengths:** tabletop-rule fidelity (caught the Wish CL-cap rule when no other model did); willing to do engine-verification dances (looks up class members to confirm before committing); strong on anchored pattern-match tasks (Blink-AoE work in NonMythic_Spells_Implementation_Plan § P1 is the canonical example).
- **Blind spots:** misses the no-minting insight; uses `FreshGuid`; over-cautious deny lists (Mythic-only, scroll/item/pseudo); weak on registry-shape inference (struggles to recognize an infrastructure artifact as the answer when retrieval is necessary but not sufficient).
- **Special config:** `subagents = off` by default for design tasks. Re-enable only for parallelizable workloads.
- **When to use:** rules-fidelity review; anchored pattern-match design where the precedent file is named; per-spell implementation when the design is locked.
- **When not:** open-ended registry-shape inference; design tasks where the canonical source must be discovered.
- **Brief shape:** tier-2 named blind-spots brief, plus *explicit instruction to disable subagents*.

### gpt 5.4 mini-high — tier-3

- **Tier shape:** tier-3 with static-fallback retreat.
- **Strengths:** verbose tool use (~40 calls scanning files thoroughly); engages with existing code.
- **Blind spots:** **static-fallback retreat** when search returns empty (recommends hand-curated whitelist instead of finding the canonical registry); **task-comprehension drift** (designs for what's already implemented rather than what's missing).
- **When to use:** narrow, specified-output tasks; strict plan + verification gates only.
- **When not:** any design work without an explicit canonical-source pointer; any work where "what exists vs what's missing" requires inference.
- **Brief shape:** strict plan + verification gates. Enumerate "what exists" and "what's missing" in the first paragraph. Name the canonical source explicitly.

### gpt 5.4 / 5.5 medium/high/xhigh, opus 4.7, opus 4.8 xtra+ — tier-1 (unthrottled)

- **Tier shape:** tier-1 native. Used sparingly on this project (token cost). Brief profiles deferred until a future batch evaluates them on a tier-1 discriminator. (opus 4.8's budget→tier mapping has its own entry above, since `high` drops it to throttled tier-1.)

### Other models on the roster — tier-4 / tier-5

Profiles deferred until they participate in a batch. Default brief shape per tier per § 3.

---

## Appendix A: The Wish-population discriminator (2026-05-28 batch)

Kept in the general copy as a **worked example of the discriminator method**, not as a rubric to
reuse: every rubric item is an origin-project specific. What transfers is the test's *design
properties* (Appendix B) — reproduce those in your domain to build your own discriminator.

### Test design

**Prompt (option 2, with verification directive — used for all but DeepSeek round 1):**

> Before writing anything please let me hear your approach on how to handle wish. The work for spell dup in wish is easy, make a variant per level and nest per school. The population is the tricky part. How should that be done? Consult the decompiled engine source mentioned in Docs Index as needed.

**Prompt (option 1, no verification directive — used for DeepSeek round 1 only):**

> [Same as above, minus the consultation directive.]

### Why this prompt discriminates

The task has structural properties typical SWE benchmarks lack (see Appendix B):

- No precedent file extracts cleanly to the answer (Shadow Evocation is the analog mechanic but doesn't use the spell-list registry pattern).
- The answer is an *infrastructure artifact* (`BlueprintSpellList`), not a *mechanic*.
- Search infrastructure was partially broken in the test session (`PathfinderWotrVLatest/**` searches returned empty for most models), creating a recovery-from-failed-tool-call stressor.
- No iteration allowed: single-shot design, no nudges, no follow-ups.

### Rubric

**Column A (must hit):** architectural correctness on the population question.

| # | Item |
|---|---|
| A1 | Reads from `BlueprintSpellList.SpellsByLevel[lvl].SpellsFiltered` (canonical source). |
| A2 | Group by `BlueprintAbility.School`. |
| A3 | Deny list = recursion only (Wish, Limited Wish, Miracle). Doesn't over-ban Time Stop / Simulacrum. |
| A4 | Recognizes `AbilityVariants` accepts existing blueprint refs — no per-leaf minting required. |
| A5 | 3-deep variant UI: verification flagged or fallback to (Level × School) flattening proposed. |

**Column B (likely miss — distinguishes tier-2-mid from tier-2-strong):**

| # | Item |
|---|---|
| B1 | Build Wish last so mod-added spells are present. |
| B2 | Slot semantics: Wish variant consumes Wish slot, not inner spell's slot. |
| B3 | CL cap / cost flagged as explicit design decisions. |
| B4 | Uses `SpellsFiltered` (not raw `Spells`) and notes the DLC-filtering reason. |
| B5 | Deterministic GUIDs on navigation containers; `FreshGuid` flagged as save-hostile. |

Pass: ≥ 4/A and ≥ 2/B.

### Results

| Model | A | B | Calls | Notable |
|---|---|---|---|---|
| deepseek v4 pro (no nudge, option 1) | 0/5 | 0/5 | ~30 | Hallucinated `AbilityVariants` nesting limit |
| deepseek v4 pro (nudged, option 2) | 3.5/5 | 1.5/5 | ~50 | Found canonical GUID via search |
| sonnet 4.6 | 3.5/5 | 1.75/5 | ~10 | Caught `Target.Unit == null` runtime gotcha |
| mimo v2.5 pro (subagents on) | 1/5 | 0/5 | ~100+ | Subagent confound; opened the right files, didn't recognize |
| mimo v2.5 pro (subagents off) | 3.25/5 | 2.5/5 | ~20 | **Caught the Wish CL-cap rule (unique)** |
| gpt 5.4 mini-high | 0.5/5 | 0/5 | ~40 | Misclassified task; static-fallback retreat |
| **gpt 5.5 low** | **3.75/5** | **2.25/5** | **~14** | No-minting default + FreshGuid callout |

### Key findings

1. **Subagent confound was the dominant factor in mimo's round-1 failure.** Removing subagents alone shifted mimo from "below tier-2 floor" (1/0) to "comfortable tier-2 mid" (3.25/2.5). ~2.5 points across both columns recovered.
2. **Verification directive (option 2 vs option 1) was the dominant factor in DeepSeek's recovery** (0/0 → 3.5/1.5). DeepSeek doesn't self-orient to consult engine source absent a directive.
3. **5.5 low's tool-use efficiency suggests throttled tier-1 rather than tier-2 native.** 14 calls, all productive; metacognitive tool selection. Different operational shape from sonnet (which is tier-2 native with prior-anchored efficiency) even when rubric scores are close.
4. **The no-minting insight is the cleanest tier-1 discriminator on this task.** Only 5.5 low landed it. Every other model — including sonnet, mimo, DeepSeek — defaulted to "mint a wrapper per leaf." Recognizing a reference-shaped relationship as a no-mint composition is a tier-1 move.
5. **The CL-cap insight is the cleanest tabletop-rule-fidelity discriminator.** Only mimo (subagents off) landed it. Engineering-grounded models read the engine; mimo also reads the rules.

### Why these findings update the tier model

- Throttled tier-1 is a distinct category, not a tier-2 sibling.
- Subagent dispatch is a per-model harness setting that significantly affects observed tier.
- Verification directive is the cheapest single intervention to lift tier-2 performance.
- Multi-model ensembles (5.5 low architect + sonnet runtime review + mimo rules review) compose because the missed-axes are different per model.

---

## Appendix B: Why modding discriminates where SWE-bench doesn't

Typical SWE benchmarks (SWE-bench, HumanEval, code-completion suites) test models on well-documented libraries with canonical answers. The tier separation gets compressed because recall + light reasoning is sufficient to look tier-2 even at tier-3.

Modding-on-a-decompiled-engine has structural properties that prevent recall-substitution:

| Property | Why it stresses the model |
|---|---|
| **Incomplete context by construction.** Engine is read-only, partially documented. Community conventions are mostly unwritten. | Recall fails. Must reason from primary sources. |
| **Three layers (engine, BC abstraction, mod conventions)** that must be reconciled. | Single-source pattern matching insufficient. Forces synthesis. |
| **Tools may be broken** (search indexes return empty, paths fail, subagents degrade). | Exit-condition behavior on tool failure surfaces (§ 5). |
| **Extension-of-existing-system,** not greenfield. | Done-vs-not-done reasoning required. Task-comprehension drift surfaces (§ 6). |
| **No canonical answer in training data.** Niche enough that no model has memorized it. | Pure reasoning, not retrieval. |

These are the conditions that *expose* tier separations. Most working contexts don't have them — which is why tier-1-vs-2 and tier-2-vs-3 separations are easy to miss in normal day-to-day work, even with completely independent agents.

### Test-design heuristic for future discriminators

A task discriminates tiers more sharply when:

- The canonical artifact name does **not** appear in the prompt or surrounding docs.
- The closest precedent file does **not** contain the answer; it points at a sibling registry instead.
- The task requires **extension of an existing system**, with a real distinction between "what's done" and "what's missing."
- Tool failures are likely (test the recovery shape).
- Iteration is **not** allowed (single-shot design).

When constructing future discriminator tests, strip artifact-name hints from the prompt deliberately. That's the cheapest way to raise the tier-discrimination signal.

---

## Maintenance

Update **§§ 2–4** only when the tier framework itself shifts (e.g. new tier inserted, throttled-tier-N category introduced).

Update **§§ 5–7** when:
- A new pitfall is observed and named.
- A new model joins the roster (add to § 7).
- An existing model's profile shifts based on a new batch.

Append to **Appendix A** when running new discriminator batches. Don't replace — the historical trail is what makes per-model trends visible across batches.

Cross-reference from your project's docs index; the detailed tier framework lives here and the index keeps a one-line pointer. This is the **general copy** — when a project's local copy diverges (its own roster placements, its own discriminator batches in Appendix A), note the divergence at the top of the local copy rather than silently forking the vocabulary; §§ 2–6 terms should mean the same thing in every repo that uses them. A compact tier-0-only extract (summon economics + notes-to-instance, for repos that don't need the full framework) exists as `tier0-model-notes.md` alongside this file's original.
