# ADR 0001: Target .NET 10

## Status

Accepted on 2026-08-01.

## Context

HackerOS v3 is a clean Blazor WebAssembly migration with a long-lived App SDK.
The development environment provides .NET SDK 10.0.302, and current architecture
research was performed against ASP.NET Core 10 documentation.

## Decision

All v3 projects initially target `net10.0`. `global.json` requests the .NET
10.0.100 SDK family and permits roll-forward to the latest installed .NET 10
feature band.

## Consequences

- The migration starts on one consistent runtime and language baseline.
- Release, trimming, lazy-loading, and PWA tests must use .NET 10 behavior.
- Developers require a compatible .NET 10 SDK.
- Changing target framework requires a new ADR and compatibility review.