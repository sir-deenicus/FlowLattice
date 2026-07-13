# Repository Agent Rules

## Preserve Historical Records

- Never rewrite, delete, reorder, "correct," modernize, or silently reconcile an existing dated decision,
  review, suggestion, status entry, work log, or other historical record to match current code or thinking.
- Historical entries are append-only. When a later decision supersedes an earlier one, preserve the earlier
  text verbatim and add a new dated entry that states what changed, why, and which earlier guidance it
  supersedes.
- Do not alter surrounding historical prose to make the document read as though the newest decision had
  always been in effect. Apparent contradictions between dated entries are part of the decision history.
- Only modify an existing historical entry when the user explicitly requests that specific rewrite. A
  general request to "document," "update the doc," or "correct this" is not permission to rewrite history.
- Before editing a document containing dated sections, identify its current-suggestion and history sections.
  Put new conclusions in a new current suggestion and add a dated history summary below it unless the user
  directs a different additive location.

## Wait After Context Transitions

- After conversation compaction, context restoration, or starting from any fresh context, do not begin or
  resume implementation work until the user gives an explicit go-ahead.
- Orientation and a concise statement of readiness are allowed, but do not run implementation commands,
  edit files, execute tests, or otherwise advance the work before that confirmation.

## Write Commit Messages for Future Readers

- A commit message must let a reader understand the meaningful change and the relevant design choice
  without reopening the diff or reconstructing the author's reasoning.
- The subject names the outcome, not the file operation. Add a short body when it is needed to explain
  what changed and why that shape was chosen.
- Be concrete rather than vague: name the important structure or behavior introduced, preserved, or
  changed.
- Mix what changed with why that shape matters, using the fewest words that preserve both. A body that
  only lists APIs, tests, or implementation steps is a diff summary; a body that gives only motivation is
  too abstract.
- Prefer one sentence naming the capability and compatibility boundary, followed when useful by one
  sentence naming the decisive invariant or design choice.
- Include implementation detail only when it explains externally meaningful behavior, prevents a likely
  misunderstanding, or records why an alternative was rejected.
- Let length follow missing context. Use the shortest message that makes the intent clear; do not force
  a one-line summary when a few brief body lines are necessary, and do not turn the message into a
  diff narration or mini-specification.
- Keep the subject scannable (roughly 50–72 characters) and wrap body lines around 72 characters.

## Treat Understanding Questions as Confirmation Gates

- Any prompt asking whether you understand, follow, see the principle, or can explain the task is a
  confirmation gate, not authorization to implement it.
- Respond by restating the understood task or principle and then wait for a separate explicit go-ahead.
  Do not edit files, run implementation commands, execute tests, or otherwise advance the proposed work
  from the understanding-check prompt itself.

## Decide Core Membership by Architectural Role

- Decide whether something belongs in core by its durable architectural role, not by its current reference
  count. A reusable component does not become test-only merely because only one present example uses it,
  and an example convenience does not become core merely because a test needs it.
- Core contains intentional, enduring, domain-independent mechanisms, representations, foundational value
  algebras, generic helpers, and agreed future-facing extension points. In this library, `Scalar`,
  `Interval`, `Transform`, and `Affine` are enduring core components.
- Keep reducible application/domain policies and concrete proof material outside core. Derived example
  constraints such as `AllDifferent`, scenario topology, givens, coordinate fixtures, expected results,
  renderers, demo entry points, and verification helpers belong in the test/demo split.
- Keep implementation details private even when tests need equivalent behavior. For example, core `Gac`
  remains private; a test that needs its own fixpoint calculation carries a test-local copy in the test
  split instead of making the core implementation public solely for test access.
- Preserve intentional general surface area, including currently unused or not-yet-implemented extension
  points, when it records an agreed architecture. Remove accidental surface area introduced only to make a
  particular fixture or demonstration convenient.

## Interpret Benchmarks Relatively

- This machine's benchmark timings are highly variable and degrade with machine up-time, tending to
  converge noticeably slower than the first measurements.
- Run the complete relevant benchmark suite together and judge performance by same-run relative
  comparisons. Do not infer a regression from isolated absolute timings or compare a late sample directly
  with an earlier run's first/best sample.
- Preserve benchmark order and report the within-run ratios and scenario context that make a comparison
  meaningful. Treat absolute cross-run numbers as environmental observations unless a controlled benchmark
  design establishes otherwise.

## Prefer Direct Designs

- Prefer the smallest direct design that preserves the concepts the library actually has. Avoid speculative
  abstractions, unsupported machinery, and public-surface proliferation; keep delegation simple and efficient.
- Treat duplication as a design signal, but do not merge genuinely orthogonal concepts merely to reduce the
  visible module count. Consolidation must remove repeated meaning without conflating distinct roles.
- Declared alternate dialects are ratified plurality, not duplication. In this library, the friendly
  `Network` methods and the `Ops` + `network { }` pair are two intentional dialects (object-shaped and
  function-shaped) over one delegated semantics; do not merge or remove either in a consolidation pass.
