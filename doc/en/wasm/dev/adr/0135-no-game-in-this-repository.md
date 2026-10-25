# ADR 0135: no game in this repository

## Status

Accepted on 2026-10-18 (Benoit, thread "ADR for the step 5 follow-ups", answer
to question E4).

- **Replaces:** [ADR 0092](0092-game-in-a-separate-repository.md) (which
  itself replaced ADR 0023), and any mention of a game or a
  "game repository" in the ADRs accepted before this one (0098, 0114, 0117,
  0132 and the others): these mentions no longer decide anything.

## Context

ADR 0092 removed the game code from this repository, but the documents still
spoke of a game repository, its SDK version and what it would do. Benoit,
on 2026-10-18: "the game's version does not belong to this repository. We must
eliminate it from the memories, from the documentation and replace it with a mention
**No game in this repository**".

## Decision

1. **No game in this repository.** This repository builds the platform: the
   simulated OS, its SDK, its hosts and its base apps.
2. The documentation describes no game, no game repository, nor its versions or its
   needs. Where a document must speak of a consumer of the ecosystem,
   it says "another repository" or "a third-party app".
3. Historical names stay as they are when they are identifiers
   (branch `v3/2-game-removal`, file name of ADR 0092, ADR 0023):
   history is not rewritten.
4. Accepted ADRs are not modified (AGENTS.md rule); this ADR
   replaces them on this point.

## Consequences

- Current documents cleaned up in the same change (`project-overview.md`,
  `building-on-hackeros.md`, `sdk-packages.md`, `ecosystem-rebuild-plan.md`,
  `wasm-v3-completion-plan.md`, `wasm-v3-final-status.md`,
  `execution-followups-plan.md`, measurement documents, `README.md` of
  `wasm2/HackerOs` and indexes); a few code comments too.
- The project memory keeps only the rule.

## References

- ADR 0023, 0092.
