# Login Progress Screen System

## Purpose
Provides a transitional login screen (`LoginProgressScreen`) that displays progress feedback during session startup, password verification, data integrity checks, and profile provisioning.

## Architecture
The login progress pipeline uses a scoped `using` pattern powered by `ILoginProgressTracker`:

```csharp
using (_loginProgressTracker.BeginStep("Loading Profile"))
{
    // Verification / KDF work
}
```

### Components & Services
1. `ILoginProgressTracker` / `LoginProgressTracker`:
   - Tracks active step description, completed steps, total step count, and percentage progress.
   - Raises `ProgressChanged` events to update Blazor components synchronously during async steps.
   - Returns disposable `ILoginStepScope` handles for `using (...)` statements.

2. `LoginProgressScreen.razor` & `LoginProgressScreen.razor.css`:
   - Renders a Gothic/Hacker aesthetic transitional screen with:
     - Glowing `H_` boot logo animation.
     - Progress bar with percentage indicator.
     - Active step description label.
     - Historical step log list.

3. `EcosystemHostState` & `App.razor`:
   - Adds `EcosystemHostView.LoginProgress` view state.
   - Transitions smoothly from `Login` -> `LoginProgress` -> `Desktop`.

## Task List
- [x] Design `ILoginProgressTracker` & `LoginProgressTracker` with disposable step scope support.
- [x] Integrate step scopes into `LocalSessionService` and `FileSystemSeeder`.
- [x] Create `LoginProgressScreen.razor` and `LoginProgressScreen.razor.css`.
- [x] Update `EcosystemHostState` and `App.razor` for `LoginProgress` view state.
- [x] Register `ILoginProgressTracker` in `EcosystemServiceCollectionExtensions`.
- [x] Add unit tests for `LoginProgressTracker` and verify all tests pass.
