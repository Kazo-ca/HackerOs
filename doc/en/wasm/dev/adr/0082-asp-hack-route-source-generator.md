# ADR 0082: ASP.Hack route table produced by a Roslyn source generator

## Status

Accepted on 2026-10-16, with the `web/6-mvc-core` work item.

- **Plan phase:** web phase ([`asp-hack-plan.md`](../plans/asp-hack-plan.md)).
- **Supersedes:** the sentence of [ADR 0076](0076-asp-hack-mvc.md) §3 "the route table is
  generated at build time *by the manifest generator*". The rest of
  ADR 0076 §3 (table generated at build time, no reflection at startup, conflicts
  as `HOSWEB0001` errors...) is kept.
- **Completes:** ADR 0076 §1, §3, §4 and §5 on the points listed below.
- **Documentation:** [`asp-hack-mvc.md`](../../framework/asp-hack-mvc.md).

## Context

The manifest generator (`HackerOs.Tools.ManifestGenerator`, ADR 0047) runs
**after** compilation: it loads the app's assembly and instantiates its
`[AppManifest]` classes. It therefore cannot add code to the assembly. Yet
a route table without reflection must **contain code**: the creation of the
controller (with its dependencies), the binding of each parameter and the call of
the action. Producing this code after the fact would mean recompiling the assembly, or
falling back to reflection at startup, which ADR 0076 excludes (trimming,
startup time in the web worker).

## Decision

1. **A Roslyn source generator.** `Tools/HackerOs.Tools.WebRouteGenerator`
   (`netstandard2.0`, `IIncrementalGenerator`) is plugged in as an *analyzer* on
   each app project by `build/HackerOs.Apps.targets` (never a runtime
   dependency; ignored by the SDK boundary check, like the manifest
   generator). For an app with no controller or `WebAppDefinition`,
   it produces nothing.
2. **What it produces.**
   - `HackerOs.AppSdk.Web.Generated.HackWebGeneratedRoutes.Table` (internal to
     the assembly): an `HackControllerRouteTable` that describes each action
     (`HackControllerActionDescriptor`) with three generated delegates:
     `CreateController` (longest public constructor, services resolved in
     the request scope), `BindArguments` (one typed call of
     `HackModelBinder` per parameter, `DataAnnotations` validation copied as
     code) and `Invoke` (direct call of the method, task awaited).
   - For each `WebAppDefinition` declared `partial`, the site's entry:
     `EntryPoint => typeof(<Name>WebHost)` and a class
     `<Name>WebHost : HackWebHost`. The manifest generator then reads this
     `EntryPoint` as for any app: nothing changes for it.
3. **A single template parser.** `Shared/HackerOs.AppSdk.Web/Routing/HackRouteTemplate.cs`
   is compiled into the library (the router uses it) **and** linked into the
   generator: build and runtime understand a template in exactly the same
   way.
4. **Build errors** (category `HackerOs.Web`, all errors):

   | Code | Case |
   |---|---|
   | `HOSWEB0001` | Two actions on the same template (up to parameter names) and the same HTTP method, same order; or two conventional actions at the same `{controller}/{action}`. |
   | `HOSWEB0002` | Invalid template (unclosed brace, complex segment, unknown constraint, optional parameter followed by a mandatory segment...). |
   | `HOSWEB0003` | Same route name (`Name =`) on two different templates. |
   | `HOSWEB0004` | Parameter that cannot be bound (type without `IParsable<T>`, two body sources...). |
   | `HOSWEB0005` | Controller without a public constructor. |
   | `HOSWEB0006` | `WebAppDefinition` that is not a top-level `partial` class and does not itself give its `EntryPoint`. |
   | `HOSWEB0007` | Unsupported action: derived HTTP method attribute that is not ASP.Hack's, `ref struct` or pointer return type, or action of an `HackApiController` without an attribute route (like `[ApiController]` in ASP.NET Core). Generic, static or `[NonAction]` methods are not actions, as in ASP.NET Core. |

5. **Adjustments to ADR 0076.**
   - §1: the library also depends on `HackerOs.App.Abstractions` (for
     `AppDefinition`) and on `Microsoft.Extensions.DependencyInjection` (§6).
   - §3: the constraints `double`, `decimal`, `float`, `datetime`, `required`,
     `range`, `minlength` and `maxlength` are added to the list; complex
     segments (`{nom}.{ext}`) are not supported (`HOSWEB0002`).
   - §4: the route values of an anonymous object (`new { id = 3 }`) go through
     generic overloads (`RedirectToAction<TValues>`, `Url.Action<TValues>`...)
     instead of an `object` parameter: trimming thus keeps the properties read.
   - §5: `View`, `PartialView`, anti-forgery and `UseStaticFiles` arrive
     with `web/7-razor-views`, not with this work item.
   - An action that returns an object is serialized with its runtime type, or
     with its **declared type** when the site's JSON context does not know the
     runtime type (`DeclaredResultType`, like ASP.NET Core's `ObjectResult.DeclaredType`):
     a list built by `[.. items]` has a compiler-internal type.

## Consequences

- No reflection to find or call an action: a site remains
  compatible with trimming (`IsTrimmable` and the trimming analyzer are
  active on the library).
- Route errors appear in the editor (IDE) and at build time, with
  the location of the faulty code, and not on first access to the site.
- The compiler in the OS (later) will be able to reuse the same generator,
  as ADR 0076 foresaw.
- A project outside `Apps/` (tests, for example) that contains a site must
  itself reference the generator as an *analyzer* (`OutputItemType="Analyzer"`).
