# E2E "Test Demo" mode

## Purpose

Playwright E2E scenarios need a known, deterministic set of files and folders on
the virtual filesystem (a plain text file, an editable file, a nested folder) so
tests can assert exact zip contents and exact file contents instead of only
"something downloaded" / "something saved". Building that fixture tree by
clicking through File Explorer in every test would be slow and would make test
failures hard to read.

"Test Demo" is a single button, rendered only for E2E runs, that seeds this
fixture tree in one click.

## How it is gated

The button (`#btn-test-demo` in `Platform/HackerOs.Platform.Blazor/Shell/DesktopShell.razor`)
only renders when `DesktopShell.TestDemoEnabled` is `true`. That parameter is
threaded from a single source of truth: a `--test-demo` command-line argument
passed to the ASP.NET Core test-harness process (`test/test/test.csproj`).

```
dotnet run test-harness-process
    → test/test/Program.cs reads args, builds TestHarnessOptions.FromArgs(args)
    → registered as a singleton DI service
    → test/test/Components/App.razor (@inject TestHarnessOptions) reads it once,
      server-side, while rendering the static page shell (prerender:false only
      skips the *child* WASM component, not this outer shell)
    → passed as a typed Razor parameter into <HackerOs.Ecosystem.App TestDemoEnabled="..." />
    → HackerOs.Ecosystem.App passes it down to <DesktopShell TestDemoEnabled="..." />
```

Because the flag only exists when the host process itself was started with
`--test-demo`, a normal `dotnet run` (no extra args), a normal published
deployment, or a normal browser visit to the app never sees the button — there
is no query string, cookie, or client-side toggle that can turn it on.

`dotnet run` requires an explicit `--` separator before arguments meant for the
application itself, otherwise it tries to parse `--test-demo` as one of its own
options and fails. The E2E tests that want the button spawn the harness like
this:

```csharp
startInfo.ArgumentList.Add("run");
startInfo.ArgumentList.Add("--configuration");
startInfo.ArgumentList.Add("Release");
startInfo.ArgumentList.Add("--project");
startInfo.ArgumentList.Add("test/test/test.csproj");
startInfo.ArgumentList.Add("--urls");
startInfo.ArgumentList.Add(address);
startInfo.ArgumentList.Add("--");
startInfo.ArgumentList.Add("--test-demo");
```

## What clicking it does

`DesktopShell.RunTestDemoAsync` calls
`Platform/HackerOs.Platform.Core/FileSystem/TestDemoFixtureSeeder.SeedAsync`,
which writes directly through the trusted `IFileSystemService` (the same way
`FileSystemSeeder` provisions `/home/{user}` at login — a system operation, not
a sandboxed app call) to create, under the signed-in user's
`Documents/e2e-fixtures`:

| Path                              | Purpose                                                    |
|------------------------------------|-------------------------------------------------------------|
| `alpha.txt`                        | Plain fixture file with known content for zip/content checks |
| `editable.txt`                     | Seeded with known "before" content; the Text Editor E2E test opens it via the Open... dialog, rewrites it, and saves |
| `notes/beta.md`                    | Nested file, used to assert folder-download zip structure    |

Next to it, `Documents/e2e-site` holds a small static HTML site (`index.html`,
`style.css`, `app.js`, `about.html`) that the Browser E2E test opens as
`file://` (ADR 0075, `docs/browser-html-rendering.md`). It is a sibling of
`e2e-fixtures`, not a child, so the folder-download zip keeps its exact content.

The exact paths and contents are exposed as `public const` fields on
`TestDemoFixtureSeeder` so tests reference the same constants instead of
duplicating literal strings.

Seeding is idempotent — re-running it (e.g. once per test run) does not fail if
the fixture already exists; it overwrites file contents and leaves existing
directories alone.

The button shows `role="status"` output (`#test-demo-status`) with either
`Seeded:{path}` or `Error:{message}` once the operation completes, so a
Playwright test can `WaitForAsync` on it instead of guessing a timeout.
