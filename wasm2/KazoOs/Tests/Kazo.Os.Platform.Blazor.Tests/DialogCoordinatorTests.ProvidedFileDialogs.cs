using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.Platform.Blazor.Dialogs;

namespace Kazo.Os.Platform.Blazor.Tests;

/// <summary>
/// ADR 0155 §5: the OS is only the proxy of the file dialogs contract. It queues the request with the
/// requesting app's context, shows the provider, and hands back the result the provider completes it
/// with; a cancellation or a wrong answer gives the dialog's "nothing happened" result.
/// </summary>
public sealed partial class DialogCoordinatorTests
{
    [Fact]
    public async Task Open_with_is_queued_with_the_caller_context_and_returns_the_provider_result()
    {
        using DialogCoordinator coordinator = new(new FakeFileDialogService());
        TestExecutionContext context = new(TestSessionId);
        OpenWithDialogRequest request = new("/home/user/notes.md");

        Task<OpenWithDialogResult> pending = coordinator.OpenWithAsync(context, request).AsTask();

        ProvidedFileDialogPresentation active = Assert.IsType<ProvidedFileDialogPresentation>(coordinator.ActiveRequest);
        Assert.Equal(KazoFileDialogs.OpenWith, active.Dialog);
        Assert.Same(request, active.Request);
        Assert.Same(context, active.Context);

        Assert.True(coordinator.CompleteProvided(active.Id, new OpenWithDialogResult(OpenWithDialogStatus.Selected, "org.hackeros.text-editor", AlwaysUse: true)));

        OpenWithDialogResult result = await pending;
        Assert.Equal(OpenWithDialogStatus.Selected, result.Status);
        Assert.Equal("org.hackeros.text-editor", result.AppId);
        Assert.True(result.AlwaysUse);
        Assert.Null(coordinator.ActiveRequest);
    }

    [Fact]
    public async Task A_cancelled_provided_dialog_returns_its_nothing_happened_result()
    {
        using DialogCoordinator coordinator = new(new FakeFileDialogService());
        TestExecutionContext context = new(TestSessionId);

        Task<OpenWithDialogResult> openWith = coordinator.OpenWithAsync(context, new OpenWithDialogRequest("/a.txt")).AsTask();
        Assert.True(coordinator.CompleteProvided(coordinator.ActiveRequest!.Id, null));
        Assert.Equal(OpenWithDialogStatus.Cancelled, (await openWith).Status);

        Task<FilePropertiesDialogResult> properties = coordinator.ShowFilePropertiesAsync(context, new FilePropertiesDialogRequest("/a.txt")).AsTask();
        Assert.True(coordinator.Cancel(coordinator.ActiveRequest!.Id));
        Assert.Equal(FilePropertiesDialogStatus.Closed, (await properties).Status);

        Task<FileConflictDialogResult> conflict = coordinator.ResolveFileConflictAsync(
            context, new FileConflictDialogRequest("/a.txt", "/b/a.txt", false, false)).AsTask();
        Assert.True(coordinator.Cancel(coordinator.ActiveRequest!.Id));
        FileConflictDialogResult skipped = await conflict;
        Assert.Equal(FileConflictDialogChoice.Skip, skipped.Choice);
        Assert.False(skipped.ApplyToAll);
    }

    [Fact]
    public async Task A_result_of_the_wrong_type_closes_the_dialog_as_cancelled()
    {
        using DialogCoordinator coordinator = new(new FakeFileDialogService());
        TestExecutionContext context = new(TestSessionId);

        Task<FileConflictDialogResult> conflict = coordinator.ResolveFileConflictAsync(
            context, new FileConflictDialogRequest("/a.txt", "/b/a.txt", false, false)).AsTask();
        Guid id = coordinator.ActiveRequest!.Id;

        // A provider answering a conflict with an "Open with" result must not leave the copy waiting.
        coordinator.CompleteProvided(id, new OpenWithDialogResult(OpenWithDialogStatus.Selected, "x", false));

        Assert.Equal(FileConflictDialogChoice.Skip, (await conflict).Choice);
        Assert.Null(coordinator.ActiveRequest);
    }

    [Fact]
    public async Task The_conflict_choice_and_apply_to_all_reach_the_caller()
    {
        using DialogCoordinator coordinator = new(new FakeFileDialogService());
        TestExecutionContext context = new(TestSessionId);

        Task<FileConflictDialogResult> conflict = coordinator.ResolveFileConflictAsync(
            context, new FileConflictDialogRequest("/a", "/b/a", true, true)).AsTask();
        ProvidedFileDialogPresentation active = Assert.IsType<ProvidedFileDialogPresentation>(coordinator.ActiveRequest);
        Assert.Equal(KazoFileDialogs.Conflict, active.Dialog);

        coordinator.CompleteProvided(active.Id, new FileConflictDialogResult(FileConflictDialogChoice.KeepBoth, ApplyToAll: true));

        FileConflictDialogResult result = await conflict;
        Assert.Equal(FileConflictDialogChoice.KeepBoth, result.Choice);
        Assert.True(result.ApplyToAll);
    }

    [Fact]
    public async Task A_service_without_the_provided_dialogs_reports_them_unavailable()
    {
        IDialogService legacy = new LegacyDialogService();
        TestExecutionContext context = new(TestSessionId);

        Assert.Equal(OpenWithDialogStatus.Unavailable, (await legacy.OpenWithAsync(context, new OpenWithDialogRequest("/a"))).Status);
        Assert.Equal(FilePropertiesDialogStatus.Unavailable, (await legacy.ShowFilePropertiesAsync(context, new FilePropertiesDialogRequest("/a"))).Status);
        Assert.Equal(FileConflictDialogChoice.Skip, (await legacy.ResolveFileConflictAsync(context, new FileConflictDialogRequest("/a", "/b", false, false))).Choice);
    }

    /// <summary>A dialog service written before ADR 0155: the interface's defaults answer for it.</summary>
    private sealed class LegacyDialogService : IDialogService
    {
        private readonly FakeFileDialogService _files = new();

        public ValueTask<OpenFileDialogResult> OpenFileAsync(AppSdk.IAppExecutionContext context, OpenFileDialogRequest request, CancellationToken cancellationToken = default) =>
            _files.OpenFileAsync(context, request, cancellationToken);

        public ValueTask<SaveFileDialogResult> SaveFileAsync(AppSdk.IAppExecutionContext context, SaveFileDialogRequest request, CancellationToken cancellationToken = default) =>
            _files.SaveFileAsync(context, request, cancellationToken);

        public ValueTask<SelectFolderDialogResult> SelectFolderAsync(AppSdk.IAppExecutionContext context, SelectFolderDialogRequest request, CancellationToken cancellationToken = default) =>
            _files.SelectFolderAsync(context, request, cancellationToken);

        public ValueTask<MessageBoxDialogResult> MessageBoxAsync(AppSdk.IAppExecutionContext context, MessageBoxDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TextInputDialogResult> TextInputAsync(AppSdk.IAppExecutionContext context, TextInputDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CredentialPromptDialogResult> CredentialPromptAsync(AppSdk.IAppExecutionContext context, CredentialPromptDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
