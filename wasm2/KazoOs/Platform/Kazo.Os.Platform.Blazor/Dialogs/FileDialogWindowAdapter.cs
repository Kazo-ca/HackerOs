using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Platform.Core.Globalization;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Blazor.Dialogs;

/// <summary>
/// Projects active platform basic and file dialogs into owner-modal windows, one per owning
/// window rather than one system-wide slot, so independent windows can each have their own
/// active dialog at the same time.
/// </summary>
public sealed class FileDialogWindowAdapter : IDisposable
{
    private readonly FileDialogCoordinator _fileCoordinator;
    private readonly DialogCoordinator? _basicCoordinator;
    private readonly WindowRuntime _windows;
    private readonly IStringLocalizer _text;
    private readonly Dictionary<Guid, WindowId> _projected = [];
    private bool _disposed;

    /// <summary>Creates an adapter over session dialog coordinators and their window runtime.</summary>
    /// <param name="fileCoordinator">The session's file dialogs.</param>
    /// <param name="windows">The window runtime the dialogs are projected into.</param>
    /// <param name="basicCoordinator">The session's basic dialogs (message box, input, chooser); optional.</param>
    /// <param name="locale">
    /// The session language of the default dialog titles (ADR 0104); <see langword="null"/> gives English.
    /// </param>
    public FileDialogWindowAdapter(
        FileDialogCoordinator fileCoordinator,
        WindowRuntime windows,
        DialogCoordinator? basicCoordinator = null,
        SessionLocaleService? locale = null)
    {
        _fileCoordinator = fileCoordinator ?? throw new ArgumentNullException(nameof(fileCoordinator));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _basicCoordinator = basicCoordinator;
        _text = Strings.For(() => locale?.Culture ?? KazoLocale.DefaultLanguage);

        _fileCoordinator.Changed += Synchronize;
        if (_basicCoordinator is not null)
        {
            _basicCoordinator.Changed += Synchronize;
        }

        Synchronize();
    }

    /// <summary>Raised after the adapter changes the window runtime.</summary>
    public event Action? Changed;

    /// <summary>Gets whether a window is a projected platform file or basic dialog.</summary>
    public bool IsDialogWindow(WindowId windowId) => _projected.ContainsValue(windowId);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fileCoordinator.Changed -= Synchronize;
        if (_basicCoordinator is not null)
        {
            _basicCoordinator.Changed -= Synchronize;
        }
    }

    private void Synchronize()
    {
        if (_disposed)
        {
            return;
        }

        IReadOnlyList<WindowRuntimeState> windows = _windows.Windows;
        Dictionary<WindowOwnerId, Guid> desiredByOwner = [];
        foreach (WindowOwnerId ownerInstanceId in windows.Select(window => window.OwnerInstanceId).Distinct())
        {
            if (_fileCoordinator.ActiveRequestFor(ownerInstanceId.Value) is FileDialogPresentation filePresentation)
            {
                desiredByOwner[ownerInstanceId] = filePresentation.Id;
            }
            else if (_basicCoordinator?.ActiveRequestFor(ownerInstanceId.Value) is DialogPresentation basicPresentation)
            {
                desiredByOwner[ownerInstanceId] = basicPresentation.Id;
            }
        }

        HashSet<Guid> desiredRequestIds = [.. desiredByOwner.Values];
        bool changed = RemoveStaleWindows(desiredRequestIds);
        changed |= CreateMissingWindows(desiredByOwner);

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    private bool RemoveStaleWindows(HashSet<Guid> desiredRequestIds)
    {
        bool changed = false;
        foreach (Guid requestId in _projected.Keys.Where(id => !desiredRequestIds.Contains(id)).ToArray())
        {
            WindowId windowId = _projected[requestId];
            _projected.Remove(requestId);
            if (_windows.Windows.Any(window => window.Id == windowId))
            {
                _windows.Apply(new ForceWindowCloseCommand(windowId));
            }

            changed = true;
        }

        return changed;
    }

    private bool CreateMissingWindows(Dictionary<WindowOwnerId, Guid> desiredByOwner)
    {
        bool changed = false;
        foreach ((WindowOwnerId ownerInstanceId, Guid requestId) in desiredByOwner)
        {
            if (_projected.ContainsKey(requestId))
            {
                continue;
            }

            WindowRuntimeState? owner = FindOwner(ownerInstanceId);
            if (owner is null)
            {
                CancelRequest(requestId);
                continue;
            }

            if (_fileCoordinator.ActiveRequestFor(ownerInstanceId.Value) is FileDialogPresentation filePresentation
                && filePresentation.Id == requestId)
            {
                CreateFileDialogWindow(filePresentation, owner);
                changed = true;
            }
            else if (_basicCoordinator?.ActiveRequestFor(ownerInstanceId.Value) is DialogPresentation basicPresentation
                && basicPresentation.Id == requestId)
            {
                CreateBasicDialogWindow(basicPresentation, owner);
                changed = true;
            }
        }

        return changed;
    }

    private WindowRuntimeState? FindOwner(WindowOwnerId ownerInstanceId) => _windows.Windows
        .Where(window => window.OwnerInstanceId == ownerInstanceId)
        .OrderByDescending(window => window.ZOrder)
        .FirstOrDefault();

    private void CreateFileDialogWindow(FileDialogPresentation presentation, WindowRuntimeState owner)
    {
        WindowId dialogId = WindowId.FromGuid(presentation.Id);
        RenderFragment content = BuildFileDialogContent(presentation);
        Func<Task> onRequestClose = () =>
        {
            _fileCoordinator.Cancel(presentation.Id);
            return Task.CompletedTask;
        };

        CreateWindow(dialogId, presentation.Id, presentation.AppId, owner, FileTitle(presentation), (720, 560), content, onRequestClose);
    }

    private void CreateBasicDialogWindow(DialogPresentation presentation, WindowRuntimeState owner)
    {
        WindowId dialogId = WindowId.FromGuid(presentation.Id);
        RenderFragment content = BuildBasicDialogContent(presentation);
        Func<Task> onRequestClose = () =>
        {
            _basicCoordinator!.Cancel(presentation.Id);
            return Task.CompletedTask;
        };

        (int width, int height) dimensions = presentation switch
        {
            MessageBoxPresentation => (460, 220),
            TextInputPresentation => (480, 240),
            CredentialPromptPresentation => (420, 300),
            AppChooserPresentation => (460, 380),
            ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.OpenWith } => (480, 460),
            ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.Properties } => (480, 380),
            ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.Conflict } => (520, 300),
            _ => (480, 240)
        };

        CreateWindow(dialogId, presentation.Id, presentation.AppId, owner, BasicTitle(presentation), dimensions, content, onRequestClose);
    }

    private void CreateWindow(
        WindowId dialogId,
        Guid requestId,
        string appId,
        WindowRuntimeState owner,
        string title,
        (int width, int height) dimensions,
        RenderFragment content,
        Func<Task> onRequestClose)
    {
        WindowRuntimeState state = new(
            dialogId,
            appId,
            owner.OwnerInstanceId,
            title,
            iconAssetPath: null,
            new WindowBounds(owner.Bounds.X + 40, owner.Bounds.Y + 40, dimensions.width, dimensions.height),
            restoreBounds: null,
            zOrder: 0,
            WindowVisualState.Normal,
            new WindowConstraints(isResizable: false, minWidth: 320, minHeight: 180),
            WindowModality.OwnerModal,
            owner.Id,
            isFocused: false,
            content: content,
            onRequestClose: onRequestClose);
        _windows.Apply(new CreateWindowCommand(state));
        _projected[requestId] = dialogId;
    }

    private void CancelRequest(Guid requestId)
    {
        if (_fileCoordinator.ActiveRequest?.Id == requestId)
        {
            _fileCoordinator.Cancel(requestId);
        }
        else if (_basicCoordinator?.ActiveRequest?.Id == requestId)
        {
            _basicCoordinator.Cancel(requestId);
        }
    }

    private RenderFragment BuildFileDialogContent(FileDialogPresentation presentation) => presentation switch
    {
        OpenFileDialogPresentation open => builder =>
        {
            builder.OpenComponent<OpenFileDialog>(0);
            builder.AddAttribute(1, nameof(OpenFileDialog.Presentation), open);
            builder.AddAttribute(2, nameof(OpenFileDialog.Coordinator), _fileCoordinator);
            builder.CloseComponent();
        },
        SaveFileDialogPresentation save => builder =>
        {
            builder.OpenComponent<SaveFileDialog>(0);
            builder.AddAttribute(1, nameof(SaveFileDialog.Presentation), save);
            builder.AddAttribute(2, nameof(SaveFileDialog.Coordinator), _fileCoordinator);
            builder.CloseComponent();
        },
        SelectFolderDialogPresentation folder => builder =>
        {
            builder.OpenComponent<FolderSelectDialog>(0);
            builder.AddAttribute(1, nameof(FolderSelectDialog.Presentation), folder);
            builder.AddAttribute(2, nameof(FolderSelectDialog.Coordinator), _fileCoordinator);
            builder.CloseComponent();
        },
        _ => throw new NotSupportedException($"Unsupported file dialog presentation '{presentation.GetType()}'."),
    };

    private RenderFragment BuildBasicDialogContent(DialogPresentation presentation) => presentation switch
    {
        MessageBoxPresentation messageBox => builder =>
        {
            builder.OpenComponent<MessageBoxDialog>(0);
            builder.AddAttribute(1, nameof(MessageBoxDialog.Presentation), messageBox);
            builder.AddAttribute(2, nameof(MessageBoxDialog.Coordinator), _basicCoordinator);
            builder.CloseComponent();
        },
        TextInputPresentation textInput => builder =>
        {
            builder.OpenComponent<TextInputDialog>(0);
            builder.AddAttribute(1, nameof(TextInputDialog.Presentation), textInput);
            builder.AddAttribute(2, nameof(TextInputDialog.Coordinator), _basicCoordinator);
            builder.CloseComponent();
        },
        CredentialPromptPresentation credentialPrompt => builder =>
        {
            builder.OpenComponent<CredentialPromptDialog>(0);
            builder.AddAttribute(1, nameof(CredentialPromptDialog.Presentation), credentialPrompt);
            builder.AddAttribute(2, nameof(CredentialPromptDialog.Coordinator), _basicCoordinator);
            builder.CloseComponent();
        },
        AppChooserPresentation chooser => builder =>
        {
            builder.OpenComponent<AppChooserDialog>(0);
            builder.AddAttribute(1, nameof(AppChooserDialog.Presentation), chooser);
            builder.AddAttribute(2, nameof(AppChooserDialog.Coordinator), _basicCoordinator);
            builder.CloseComponent();
        },
        ProvidedFileDialogPresentation provided => builder =>
        {
            builder.OpenComponent<ProvidedFileDialog>(0);
            builder.AddAttribute(1, nameof(ProvidedFileDialog.Presentation), provided);
            builder.AddAttribute(2, nameof(ProvidedFileDialog.Coordinator), _basicCoordinator);
            builder.CloseComponent();
        },
        _ => throw new NotSupportedException($"Unsupported dialog presentation '{presentation.GetType()}'."),
    };

    private string FileTitle(FileDialogPresentation presentation) => presentation switch
    {
        OpenFileDialogPresentation open => open.Request.Title ?? _text["FileDialog.OpenFile"],
        SaveFileDialogPresentation save => save.Request.Title ?? _text["FileDialog.SaveFile"],
        SelectFolderDialogPresentation folder => folder.Request.Title ?? _text["FileDialog.SelectFolder"],
        _ => _text["FileDialog.Title"],
    };

    private string BasicTitle(DialogPresentation presentation) => presentation switch
    {
        MessageBoxPresentation msgBox => !string.IsNullOrWhiteSpace(msgBox.Request.Title) ? msgBox.Request.Title : _text["Dialog.Message"],
        TextInputPresentation input => !string.IsNullOrWhiteSpace(input.Request.Title) ? input.Request.Title : _text["Dialog.Input"],
        CredentialPromptPresentation credential => !string.IsNullOrWhiteSpace(credential.Request.Title) ? credential.Request.Title : _text["Dialog.AuthorizationRequired"],
        AppChooserPresentation chooser => !string.IsNullOrWhiteSpace(chooser.Request.Title) ? chooser.Request.Title : _text["Dialog.OpenWith"],
        ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.OpenWith } => _text["Dialog.OpenWith"],
        ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.Properties } => _text["Dialog.Properties"],
        ProvidedFileDialogPresentation { Dialog: KazoFileDialogs.Conflict } => _text["Dialog.FileConflict"],
        _ => _text["Dialog.Title"],
    };
}
