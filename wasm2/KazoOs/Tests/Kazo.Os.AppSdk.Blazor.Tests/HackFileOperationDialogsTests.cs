using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0161 §2: the copy and move rules of the SDK ask a name conflict through the dialog of the
/// <c>hackeros.file-dialogs</c> provider, modal to the app that runs the operation.
/// </summary>
public sealed class HackFileOperationDialogsTests
{
    [Theory]
    [InlineData(FileConflictDialogChoice.Replace, HackFileConflictChoice.Replace)]
    [InlineData(FileConflictDialogChoice.KeepBoth, HackFileConflictChoice.KeepBoth)]
    [InlineData(FileConflictDialogChoice.Skip, HackFileConflictChoice.Skip)]
    public async Task The_resolver_asks_the_dialog_for_the_owner_and_maps_its_answer(
        FileConflictDialogChoice answer, HackFileConflictChoice expected)
    {
        ConflictDialogs dialogs = new() { Answer = new FileConflictDialogResult(answer, ApplyToAll: true) };
        MinimalAppExecutionContext owner = new(Manifest());

        HackFileConflictResolution resolution = await dialogs.AsFileConflictResolver(owner)(
            new HackFileTransferConflict("/home/user/a.txt", "/home/user/docs/a.txt", SourceIsDirectory: false, DestinationIsDirectory: false));

        Assert.Equal(expected, resolution.Choice);
        Assert.True(resolution.ApplyToAll);
        Assert.Same(owner, dialogs.LastOwner);
        Assert.Equal(new FileConflictDialogRequest("/home/user/a.txt", "/home/user/docs/a.txt", false, false), dialogs.LastRequest);
    }

    [Fact]
    public async Task A_move_into_a_taken_name_replaces_it_when_the_dialog_says_so()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory("/home/user")
            .WithDirectory("/home/user/docs")
            .WithFile("/home/user/a.txt", "new")
            .WithFile("/home/user/docs/a.txt", "old");
        MinimalAppExecutionContext owner = new(Manifest(), fileSystem);
        ConflictDialogs dialogs = new() { Answer = new FileConflictDialogResult(FileConflictDialogChoice.Replace, ApplyToAll: false) };

        HackFileTransferResult result;
        using (HackExecutionContext.Enter(owner, "/home/user"))
        {
            result = await HackFileOperations.MoveAsync(["/home/user/a.txt"], "/home/user/docs", dialogs.AsFileConflictResolver(owner));
        }

        Assert.True(result.Succeeded);
        Assert.Equal("new", fileSystem.ContentOf("/home/user/docs/a.txt"));
        Assert.Equal(1, dialogs.Asked);
    }

    private static AppManifest Manifest() => new()
    {
        Id = "org.hackeros.test-file-operations",
        Name = "File Operations App",
        Version = "1.0.0",
        PublisherId = "org.hackeros",
        Description = "File operations dialog test app.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Kazo.Os.AppSdk.Blazor.Tests", "FileOperationsApp"),
        SdkCompatibility = new AppSdkCompatibilityManifest(HackerOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite]
    };

    /// <summary>A dialog service that only answers the name conflict question.</summary>
    private sealed class ConflictDialogs : IDialogService
    {
        public FileConflictDialogResult Answer { get; init; } = new(FileConflictDialogChoice.Skip, false);

        public IAppExecutionContext? LastOwner { get; private set; }

        public FileConflictDialogRequest? LastRequest { get; private set; }

        public int Asked { get; private set; }

        public ValueTask<FileConflictDialogResult> ResolveFileConflictAsync(
            IAppExecutionContext context, FileConflictDialogRequest request, CancellationToken cancellationToken = default)
        {
            LastOwner = context;
            LastRequest = request;
            Asked++;
            return ValueTask.FromResult(Answer);
        }

        public ValueTask<OpenFileDialogResult> OpenFileAsync(IAppExecutionContext context, OpenFileDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SaveFileDialogResult> SaveFileAsync(IAppExecutionContext context, SaveFileDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<SelectFolderDialogResult> SelectFolderAsync(IAppExecutionContext context, SelectFolderDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<MessageBoxDialogResult> MessageBoxAsync(IAppExecutionContext context, MessageBoxDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<TextInputDialogResult> TextInputAsync(IAppExecutionContext context, TextInputDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CredentialPromptDialogResult> CredentialPromptAsync(IAppExecutionContext context, CredentialPromptDialogRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
