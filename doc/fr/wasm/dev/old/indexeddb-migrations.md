# IndexedDB Migrations

## Purpose

Define the versioned, C#-owned migration chain for the `hackeros` IndexedDB database. JavaScript executes declarative browser operations only; it does not decide schema versions or recovery policy.

## Architecture

`IndexedDbMigrationPlan.CreateCurrent()` returns one contiguous step for every target version from `1` through `HackerOsIndexedDbSchema.CurrentVersion`. The JavaScript `onupgradeneeded` handler selects steps whose target version is greater than `oldVersion` and no greater than `newVersion`.

The upgrade transaction is aborted when the selected targets are missing, duplicated, or out of order. IndexedDB then preserves the previously committed database version. Opening an already-current database runs no migration, making ordinary reopen idempotent.

## Current Fixtures

> **Superseded (2026-10-17, ADR 0099 section 4).** The text below described the state before
> any version was released. Upgrade fixtures now live in `Tests/fixtures/indexeddb/`
> (`hackeros-v2.json`, `hackeros-v3.json`), are produced by booting the OS of an old commit, and
> are replayed by `HackerOs.UI.E2E.Tests.IndexedDbUpgradeFixtureTests`. Version 1 has no fixture:
> it never existed in any commit of the repository (the root commit `fdca68e` already declares
> version 2). Procedure and rule: [`building-and-testing.md`](../building-and-testing.md#fixtures-de-montée-de-version-indexeddb).

Schema version `0` is the browser-defined state where the database does not yet exist. The `0 -> 1` fixture is represented by an empty database and the version-one step creates all canonical stores and indexes. ~~There are no historical published schema versions yet.~~ (Historical versions 2 and 3 now exist; see the note above.)

When version `2` is introduced, its change must include:

- a target-version `2` migration step;
- a fixture created with the exact version-one schema;
- tests for `0 -> 2`, `1 -> 2`, current-version reopen, and aborted upgrade;
- updated backup compatibility and recovery documentation.

## Recovery Rules

Migration failure never triggers automatic database deletion. The failed version-change transaction rolls back, and the previous version remains authoritative. Destructive replacement is permitted only through the explicit validated restore/recovery workflow tracked by `P2-IDB-011` and `P2-IDB-012`.

## Validation

Browser-free tests validate chain contiguity, current-version termination, canonical schema content, and interop serialization. Real IndexedDB evidence for rollback, reload, and interrupted upgrades is tracked by `P2-IDB-014` and is required before `P2-IDB-006` can close.

## Task Status

- [x] Define a contiguous C#-owned migration model.
- [x] Apply only the steps required by `oldVersion` and `newVersion`.
- [x] Abort incomplete migration paths without implicit deletion.
- [x] Document the version-zero fixture and future fixture rules.
- [x] Validate initial creation, reopen, and interrupted upgrade in a real browser.
