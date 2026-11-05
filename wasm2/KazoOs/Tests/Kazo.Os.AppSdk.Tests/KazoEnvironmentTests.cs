using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>Contract tests for <see cref="KazoEnvironment"/> (ADR 0050 section 6, ADR 0053).</summary>
public sealed class KazoEnvironmentTests
{
    [Fact]
    public void Every_member_throws_outside_any_scope()
    {
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoEnvironment.CurrentDirectory);
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoEnvironment.GetEnvironmentVariable("HOME"));
        Assert.Throws<KazoExecutionContextMissingException>(() => KazoEnvironment.MachineName);
    }

    [Fact]
    public void Reads_the_process_identity_arguments_and_names()
    {
        FakeAppEnvironmentGateway environment = new(KazoTestHost.Home, "alice", "black-ice", ["grep", "-i", "two words"]);
        using IDisposable scope = Enter(new FakeAppFileSystemGateway().WithDirectory(KazoTestHost.Home), environment);

        Assert.Equal(["grep", "-i", "two words"], KazoEnvironment.GetCommandLineArgs());
        Assert.Equal("alice", KazoEnvironment.UserName);
        Assert.Equal("black-ice", KazoEnvironment.MachineName);
        Assert.Equal(1, KazoEnvironment.ProcessId);
        Assert.Equal("\n", KazoEnvironment.NewLine);
        Assert.Equal(KazoTestHost.Home, KazoEnvironment.CurrentDirectory);
    }

    [Fact]
    public void Variables_can_be_set_replaced_and_removed()
    {
        using IDisposable scope = Enter(new FakeAppFileSystemGateway(), new FakeAppEnvironmentGateway(KazoTestHost.Home));

        KazoEnvironment.SetEnvironmentVariable("EDITOR", "nano");
        KazoEnvironment.SetEnvironmentVariable("EMPTY", string.Empty);
        Assert.Equal("nano", KazoEnvironment.GetEnvironmentVariable("EDITOR"));
        Assert.Equal(string.Empty, KazoEnvironment.GetEnvironmentVariable("EMPTY"));

        KazoEnvironment.SetEnvironmentVariable("EDITOR", null);
        Assert.Null(KazoEnvironment.GetEnvironmentVariable("EDITOR"));
        Assert.Equal(["EMPTY", "HOME"], KazoEnvironment.GetEnvironmentVariables().Keys.Order(StringComparer.Ordinal));

        Assert.Throws<ArgumentException>(() => KazoEnvironment.SetEnvironmentVariable("A=B", "x"));
        Assert.Throws<ArgumentException>(() => KazoEnvironment.SetEnvironmentVariable(string.Empty, "x"));
    }

    [Fact]
    public async Task SetCurrentDirectoryAsync_changes_where_relative_paths_resolve()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory(KazoTestHost.Home)
            .WithDirectory("/home/alice/Documents")
            .WithFile("/home/alice/Documents/notes.txt", "hello");
        FakeAppEnvironmentGateway environment = new(KazoTestHost.Home);
        using IDisposable scope = Enter(fileSystem, environment);

        await KazoEnvironment.SetCurrentDirectoryAsync("Documents");

        Assert.Equal("/home/alice/Documents", KazoEnvironment.CurrentDirectory);
        Assert.Equal("/home/alice/Documents", environment.CurrentDirectory);
        Assert.Equal("/home/alice/Documents/notes.txt", KazoPath.GetFullPath("notes.txt"));
        Assert.Equal("hello", await KazoFile.ReadAllTextAsync("notes.txt"));

        await KazoEnvironment.SetCurrentDirectoryAsync("~");
        Assert.Equal(KazoTestHost.Home, KazoEnvironment.CurrentDirectory);
    }

    [Fact]
    public async Task SetCurrentDirectoryAsync_refuses_a_missing_path_or_a_file()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory(KazoTestHost.Home)
            .WithFile("/home/alice/file.txt", "x");
        using IDisposable scope = Enter(fileSystem, new FakeAppEnvironmentGateway(KazoTestHost.Home));

        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoEnvironment.SetCurrentDirectoryAsync("/nowhere"));
        KazoIOException notDirectory = await Assert.ThrowsAsync<KazoIOException>(() => KazoEnvironment.SetCurrentDirectoryAsync("file.txt"));
        Assert.Equal(Simulation.Abstractions.FileSystem.FileSystemErrorCode.NotDirectory, notDirectory.ErrorCode);
        Assert.Equal(KazoTestHost.Home, KazoEnvironment.CurrentDirectory);
    }

    [Fact]
    public async Task A_change_of_directory_replaces_the_directory_pinned_by_the_scope()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway().WithDirectory(KazoTestHost.Home).WithDirectory("/tmp");
        MinimalAppExecutionContext context = new(
            KazoTestHost.Manifest, fileSystem: fileSystem, homeDirectory: KazoTestHost.Home,
            environment: new FakeAppEnvironmentGateway(KazoTestHost.Home));

        using (KazoExecutionContext.Enter(context, "/var"))
        {
            Assert.Equal("/var", KazoEnvironment.CurrentDirectory);
            await KazoEnvironment.SetCurrentDirectoryAsync("/tmp");
            Assert.Equal("/tmp", KazoEnvironment.CurrentDirectory);
        }
    }

    private static IDisposable Enter(FakeAppFileSystemGateway fileSystem, FakeAppEnvironmentGateway environment) =>
        KazoExecutionContext.Enter(new MinimalAppExecutionContext(
            KazoTestHost.Manifest, fileSystem: fileSystem, userId: "0f00", homeDirectory: KazoTestHost.Home, environment: environment));
}
