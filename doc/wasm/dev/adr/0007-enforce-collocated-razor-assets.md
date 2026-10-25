# ADR 0007: Enforce Collocated Razor Assets

## Status

Accepted on 2026-08-01.

## Context

HackerOS components must keep markup, styles, and scripts maintainable and
packageable for build-known and future runtime-loaded apps. Review guidance alone
cannot reliably prevent inline assets.

## Decision

All Razor projects run a shared MSBuild validation target. Inline `<style>`,
`<script>`, `style=`, and raw JavaScript event attributes fail the build.
Component-owned assets use `Component.razor.css` and `Component.razor.js`.

## Consequences

- Violations fail locally and in CI before Razor compilation completes.
- Blazor `@event` bindings remain supported.
- Shared static assets are allowed only as dedicated files.
- Runtime package validation can rely on explicit component asset files.