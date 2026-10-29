> [Version française](../../../../fr/wasm/dev/adr/0192-features-overview-and-ported-apps.md)

# ADR 0192: features overview and named ported apps

## Status

Accepted on 2026-10-29 by Benoit, in the thread "Présenter les
fonctionnalités de HackerOS".

- **Refines:** [ADR 0135](0135-no-game-in-this-repository.md) §2, for the
  [`features.md`](../../features.md) document only.

## Context

Benoit asked for an overview page of HackerOS, linked from the root
`readme.md`, that will later be used to build the product web site. It lists
the available features, the goal of the ecosystem, the ways to deploy the OS,
and an "In development" section with ADRs and issues. He asked to list the
apps ported in their own repositories (`Kazo-ca/HackerOs-App-*`) as optional
features in development, and chose to also name Chess and EmulatorJS, despite
ADR 0135 §2.

## Decision

1. The overview lives in `doc/fr/wasm/features.md` (original) and
   `doc/en/wasm/features.md` (translation), linked from `readme.md`,
   `readme-fr.md` and the `doc/<language>/wasm/` index.
2. "Available" means merged into `main`. The "In development" section is one
   table per theme (Feature, Description, ADR, Issue); an ADR not merged yet
   is linked through its PR.
3. **Exception to ADR 0135 §2**: `features.md` may name the apps ported in
   their own repositories, Chess and EmulatorJS included, as optional apps
   installed separately. The rest of ADR 0135 holds: no game code in this
   repository, and no other document describes a game.
4. Every PR to `main` that ships, changes or plans a feature updates
   `features.md` in both languages. The PR template
   (`.github/pull_request_template.md`) has a checkbox as a reminder, and the
   rule is written in `AGENTS.md` and `CONTRIBUTING.md`.

## Consequences

- A reader finds in one page what HackerOS offers and what is coming.
- The "In development" section relies on PR discipline: the PR template
  checkbox is a reminder, not an automated check.
