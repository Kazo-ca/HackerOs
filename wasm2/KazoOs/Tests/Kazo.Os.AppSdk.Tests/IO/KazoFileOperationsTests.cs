using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>
/// ADR 0058, section 1, ADR 0161 §2: the transfers behind paste and drop — Nautilus copy names, the move conflict
/// choices, folders never copied into themselves, per-item failures, host file import and trash drops.
/// </summary>
public sealed class KazoFileOperationsTests
{
    private const string Home = "/home/alice";

    [Theory]
    [InlineData("report.txt", false, "report (copy).txt")]
    [InlineData("report (copy).txt", false, "report (copy 2).txt")]
    [InlineData("report (copy 3).txt", false, "report (copy 4).txt")]
    [InlineData(".bashrc", false, ".bashrc (copy)")]
    [InlineData("archive.d", true, "archive.d (copy)")]
    [InlineData("Makefile", false, "Makefile (copy)")]
    public void GetCopyName_follows_Nautilus(string name, bool isDirectory, string expected) =>
        Assert.Equal(expected, KazoFileOperations.GetCopyName(name, isDirectory, _ => false));

    [Fact]
    public void GetCopyName_skips_taken_names()
    {
        HashSet<string> taken = ["report.txt", "report (copy).txt", "report (copy 2).txt"];

        Assert.Equal("report (copy 3).txt", KazoFileOperations.GetCopyName("report.txt", isDirectory: false, taken.Contains));
    }

    [Fact]
    public async Task Copying_into_the_same_folder_twice_adds_copy_suffixes()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/report.txt", "v1");

        KazoFileTransferResult first = await KazoFileOperations.CopyAsync([Home + "/report.txt"], Home);
        KazoFileTransferResult second = await KazoFileOperations.CopyAsync([Home + "/report.txt"], Home);

        Assert.True(first.Succeeded && second.Succeeded);
        Assert.Equal("v1", fs.ContentOf(Home + "/report (copy).txt"));
        Assert.Equal("v1", fs.ContentOf(Home + "/report (copy 2).txt"));
        Assert.Equal(Home + "/report (copy 2).txt", second.Completed.Single().DestinationPath);
    }

    [Fact]
    public async Task Copying_a_folder_copies_its_whole_tree()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Home + "/src/lib").WithFile(Home + "/src/lib/a.cs", "class A {}").WithDirectory(Home + "/backup");

        KazoFileTransferResult result = await KazoFileOperations.CopyAsync([Home + "/src"], Home + "/backup");

        Assert.True(result.Succeeded);
        Assert.Equal("class A {}", fs.ContentOf(Home + "/backup/src/lib/a.cs"));
        Assert.True(fs.Exists(Home + "/src/lib/a.cs"));
    }

    [Fact]
    public async Task A_folder_is_never_copied_or_moved_into_itself_and_the_others_still_go()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Home + "/projects/inner").WithFile(Home + "/notes.txt", "n");

        KazoFileTransferResult copy = await KazoFileOperations.CopyAsync([Home + "/projects", Home + "/notes.txt"], Home + "/projects/inner");
        KazoFileTransferResult move = await KazoFileOperations.MoveAsync([Home + "/projects"], Home + "/projects", resolveConflict: null);

        Assert.Equal("cannot copy a folder into itself", copy.Failures.Single().Reason);
        Assert.False(fs.Exists(Home + "/projects/inner/projects"));
        Assert.True(fs.Exists(Home + "/projects/inner/notes.txt"));
        Assert.Equal("cannot move a folder into itself", move.Failures.Single().Reason);
        Assert.Contains("Could not copy 'projects': cannot copy a folder into itself.", copy.DescribeFailures("copy"));
    }

    [Fact]
    public async Task Moving_into_the_source_folder_does_nothing()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/a.txt", "a");

        KazoFileTransferResult result = await KazoFileOperations.MoveAsync([Home + "/a.txt"], Home, resolveConflict: null);

        Assert.Equal([Home + "/a.txt"], result.Skipped);
        Assert.Empty(result.Completed);
        Assert.Equal("a", fs.ContentOf(Home + "/a.txt"));
    }

    [Theory]
    [InlineData(KazoFileConflictChoice.Replace, "new", null)]
    [InlineData(KazoFileConflictChoice.KeepBoth, "old", "new")]
    [InlineData(KazoFileConflictChoice.Skip, "old", null)]
    public async Task A_move_conflict_follows_the_users_choice(KazoFileConflictChoice choice, string kept, string? copy)
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Home + "/in").WithFile(Home + "/in/a.txt", "new").WithFile(Home + "/a.txt", "old");
        KazoFileTransferConflict? asked = null;

        KazoFileTransferResult result = await KazoFileOperations.MoveAsync(
            [Home + "/in/a.txt"],
            Home,
            conflict =>
            {
                asked = conflict;
                return Task.FromResult(new KazoFileConflictResolution(choice));
            });

        Assert.True(result.Succeeded);
        Assert.Equal(Home + "/a.txt", asked!.DestinationPath);
        Assert.Equal("a.txt", asked.Name);
        Assert.Equal(kept, fs.ContentOf(Home + "/a.txt"));
        Assert.Equal(copy is null ? null : (string?)fs.ContentOf(Home + "/a (copy).txt"), copy);
        Assert.Equal(choice == KazoFileConflictChoice.Skip, fs.Exists(Home + "/in/a.txt"));
    }

    [Fact]
    public async Task Apply_to_all_asks_once()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Home + "/in")
            .WithFile(Home + "/in/a.txt", "1").WithFile(Home + "/in/b.txt", "2")
            .WithFile(Home + "/a.txt", "x").WithFile(Home + "/b.txt", "y");
        int questions = 0;

        await KazoFileOperations.MoveAsync(
            [Home + "/in/a.txt", Home + "/in/b.txt"],
            Home,
            _ =>
            {
                questions++;
                return Task.FromResult(new KazoFileConflictResolution(KazoFileConflictChoice.Replace, ApplyToAll: true));
            });

        Assert.Equal(1, questions);
        Assert.Equal("1", fs.ContentOf(Home + "/a.txt"));
        Assert.Equal("2", fs.ContentOf(Home + "/b.txt"));
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_others()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/a.txt", "a").WithDirectory(Home + "/dest");

        KazoFileTransferResult result = await KazoFileOperations.MoveAsync([Home + "/gone.txt", Home + "/a.txt"], Home + "/dest", resolveConflict: null);

        Assert.Equal(new KazoFileTransferFailure(Home + "/gone.txt", "no longer exists"), result.Failures.Single());
        Assert.Equal("a", fs.ContentOf(Home + "/dest/a.txt"));
        Assert.False(fs.Exists(Home + "/a.txt"));
    }

    [Fact]
    public async Task Host_files_are_imported_with_copy_names()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/photo.png", "old");

        KazoFileTransferResult result = await KazoFileOperations.ImportAsync([HostFile("photo.png", "new"), HostFile("../evil", "x")], Home);

        Assert.Equal("new", fs.ContentOf(Home + "/photo (copy).png"));
        Assert.Equal("old", fs.ContentOf(Home + "/photo.png"));
        Assert.Equal("invalid file name", result.Failures.Single().Reason);
    }

    [Fact]
    public async Task Dropping_on_the_trash_moves_to_the_trash()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/old.txt", "bye");

        KazoFileTransferResult result = await KazoFileOperations.MoveToTrashAsync([Home + "/old.txt"]);

        Assert.True(result.Succeeded);
        Assert.False(fs.Exists(Home + "/old.txt"));
        Assert.Equal("bye", fs.ContentOf(Home + "/.local/share/Trash/files/old.txt"));
    }

    private static KazoHostFile HostFile(string name, string content)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        return new KazoHostFile(name, bytes.Length, "text/plain", (_, _) => Task.FromResult<Stream>(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task A_stopped_transfer_keeps_what_is_done_and_reports_the_rest()
    {
        // ADR 0069: the app was asked to stop while the first item moved; it ends after it.
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory(Home + "/out").WithFile(Home + "/out/a.txt", "old")
            .WithFile(Home + "/a.txt", "A").WithFile(Home + "/b.txt", "B").WithFile(Home + "/c.txt", "C");
        using CancellationTokenSource stop = new();

        KazoFileTransferResult result = await KazoFileOperations.TransferAsync(
            [Home + "/a.txt", Home + "/b.txt", Home + "/c.txt"],
            Home + "/out",
            KazoFileTransferMode.Move,
            conflict =>
            {
                stop.Cancel();
                return Task.FromResult(new KazoFileConflictResolution(KazoFileConflictChoice.Replace));
            },
            stop.Token);

        Assert.True(result.Cancelled);
        Assert.Equal("A", fs.ContentOf(Home + "/out/a.txt"));
        Assert.Equal([Home + "/b.txt", Home + "/c.txt"], result.Failures.Select(static failure => failure.Path));
        Assert.All(result.Failures, failure => Assert.Equal("stopped before this item", failure.Reason));
        Assert.True(fs.Exists(Home + "/b.txt") && !fs.Exists(Home + "/out/b.txt"));
    }

    [Fact]
    public async Task Trash_moves_and_imports_stop_cleanly_too()
    {
        using IDisposable scope = Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile(Home + "/a.txt", "A");
        using CancellationTokenSource stop = new();
        await stop.CancelAsync();

        KazoFileTransferResult trashed = await KazoFileOperations.MoveToTrashAsync([Home + "/a.txt"], stop.Token);

        Assert.True(trashed.Cancelled);
        Assert.Empty(trashed.Completed);
        Assert.True(fs.Exists(Home + "/a.txt"));
    }

    private static IDisposable Enter(out FakeAppFileSystemGateway fileSystem)
    {
        fileSystem = new FakeAppFileSystemGateway().WithDirectory(Home);
        MinimalAppExecutionContext context = new(
            new AppManifest
            {
                Id = "org.hackeros.test-transfer",
                Name = "Transfer test",
                Version = "1.0.0",
                PublisherId = "org.hackeros",
                Description = "KazoFileOperations contract test application.",
                Kind = AppKind.Window,
                EntryPoint = new AppEntryPointManifest("Test", "Test.Window"),
                SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
                Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
                Resources = AppResourceProfileManifest.None
            },
            fileSystem: fileSystem,
            userId: "alice",
            homeDirectory: Home,
            logging: new FakeAppLoggingGateway(),
            clock: new FakeAppClockGateway(new DateTimeOffset(2026, 10, 11, 4, 0, 0, TimeSpan.Zero)));
        return KazoExecutionContext.Enter(context);
    }
}
