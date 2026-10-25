# Platform Dialogs (Basic & File Dialogs)

## Purpose

Platform dialogs provide system-level modal interaction for HackerOS applications.
They include:
1. **File Dialogs** (`IFileDialogService`): App-scoped virtual filesystem dialogs (Open File, Save File, Select Folder).
2. **Basic Dialogs** (`IDialogService`): System-wide modal prompts (`MessageBox` and `TextInput`) available to all applications without special capability requirements.

## Architecture

`IDialogService` extends `IFileDialogService` to present a unified dialog API to window apps (`IDialogService : IFileDialogService`).
The underlying implementation separates concern across two dedicated services:

- **`FileDialogCoordinator`**: Implements `IFileDialogService` per authenticated session. Validates exact filesystem dialog capabilities (`dialogs.file-open`, `dialogs.file-save`, `dialogs.folder-select`).
- **`DialogCoordinator`**: Implements `IDialogService`. It receives `IFileDialogService` via dependency injection and delegates file dialog requests directly to it. Basic dialogs (`MessageBoxAsync`, `TextInputAsync`) are managed in a separate FIFO queue accessible to all applications.

Modal window presentation is projected into authoritative owner-modal windows by window adapters (`FileDialogWindowAdapter` and `DialogWindowAdapter`), rendering typed components (`MessageBoxDialog.razor`, `TextInputDialog.razor`, `OpenFileDialog.razor`, `SaveFileDialog.razor`, `FolderSelectDialog.razor`) inside `DesktopShell.razor`.

## Usage

### Calling MessageBox from a Window App

`WindowAppBase` provides a convenient `base.MessageBox` method:

```csharp
MessageBoxDialogResult result = await base.MessageBox(
    title: "Confirmation",
    content: "Voulez-vous vraiment effacer ce fichier?",
    dialogType: MessageBoxType.YesNo);

if (result.Result == MessageBoxResult.Yes)
{
    // Proceed with deletion
}
```

Supports both `MessageBoxType` / `MessageboxType` and `MessageBoxResult` / `MessageboxResult` for case flexibility.

### Calling TextInput from a Window App

```csharp
TextInputDialogResult result = await base.TextInput(
    title: "Nouveau Dossier",
    content: "Entrez le nom du dossier:",
    defaultValue: "NouveauDossier",
    placeholder: "Nom du dossier...");

if (result.Status == TextInputStatus.Submitted && !string.IsNullOrWhiteSpace(result.Value))
{
    // Process input
}
```

## Key Decisions

- `IFileDialogService` remains distinct from basic dialog management; `FileDialogCoordinator` only implements file dialog contracts.
- `DialogCoordinator` implements `IDialogService : IFileDialogService` and delegates file dialog calls to `IFileDialogService`.
- Basic dialogs (`MessageBox`, `TextInput`) do not require special capability grants in `AppCapabilities.cs`; all applications can invoke them.
- `MessageboxType` and `MessageboxResult` structs provide implicit conversion to `MessageBoxType` and `MessageBoxResult` for syntax convenience.
- UI styling for `MessageBoxDialog` and `TextInputDialog` strictly adheres to collocated `.razor.css` scoped styles.

## Task List

- [x] `DLG-001` Create dedicated Markdown documentation for Platform Dialogs and update `docs/README.md`.
- [x] `DLG-002` Implement `DialogContracts.cs` (`IDialogService`, `MessageBoxType`, `MessageBoxResult`, `MessageboxType`, `MessageboxResult`, `MessageBoxDialogRequest`, `MessageBoxDialogResult`, `TextInputDialogRequest`, `TextInputStatus`, `TextInputDialogResult`).
- [x] `DLG-003` Update `WindowAppBase` with `Dialogs` injection and helper methods (`base.MessageBox(...)`, `base.TextInput(...)`).
- [x] `DLG-004` Implement `DialogCoordinator` in `Platform/HackerOs.Platform.Blazor/Dialogs/`.
- [x] `DLG-005` Create `MessageBoxDialog.razor` and `MessageBoxDialog.razor.css` with dark/hacker aesthetic.
- [x] `DLG-006` Create `TextInputDialog.razor` and `TextInputDialog.razor.css` with dark/hacker aesthetic.
- [x] `DLG-007` Update `FileDialogWindowAdapter` / `DialogWindowAdapter` and `DesktopShell.razor` for dialog rendering.
- [x] `DLG-008` Update DI registration in `EcosystemServiceCollectionExtensions.cs`.
- [x] `DLG-009` Add unit tests in `HackerOs.AppSdk.Blazor.Tests` and `HackerOs.Platform.Blazor.Tests`.
- [x] `DLG-010` Verify full project build and zero inline CSS/JS violations.
