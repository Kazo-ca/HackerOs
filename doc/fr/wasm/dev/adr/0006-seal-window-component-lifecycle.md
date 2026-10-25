# ADR 0006: Seal the Window Component Lifecycle

## Status

Accepted on 2026-08-01.

## Context

In a prior Blazor attempt, window apps overrode `OnAfterRenderAsync` without
calling the base method. The app compiled and buttons worked, but drag/resize JS
initialization never ran and failed silently.

## Decision

`WindowAppBase` seals framework lifecycle overrides and invokes named `OnApp*`
hooks for application customization. Framework post-render work always executes
before the app-specific asynchronous post-render hook.

## Consequences

- App developers cannot accidentally bypass window initialization.
- Platform lifecycle behavior has one testable owner.
- Existing components must rename lifecycle overrides to App SDK hooks.
- A developer cannot opt out of mandatory framework setup by omitting a base call.