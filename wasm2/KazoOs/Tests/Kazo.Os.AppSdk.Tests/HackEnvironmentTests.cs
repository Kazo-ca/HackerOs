using Kazo.Os.AppSdk.IO;
using Kazo.Os.AppSdk.Tests.IO;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests;

/// <summary>Contract tests for <see cref="HackEnvironment"/> (ADR 0050 section 6, ADR 0053).</summary>
public sealed class HackEnvironmentTests
{
    [Fact]
    public void Every_member_throws_outside_any_scope()
    {
        Assert.Throws<HackExecutionContextMissingException>(() => HackEnvironment.CurrentDirectory);
        Assert.Throws<HackExecutionContextMissingException>(() => HackEnvironment.GetEnvironmentVariable("HOME"));
        Assert.Throws<HackExecutionContextMissingException>(() => HackEnvironment.MachineName);
    }

    [Fact]
    public void Reads_the_process_identity_arguments_and_names()
    {
        FakeAppEnvironmentGateway environment = new(HackTestHost.Home, "alice", "black-ice", ["grep", "-i", "two words"]);
        using IDisposable scope = Enter(new FakeAppFileSystemGateway().WithDirectory(HackTestHost.Home), environment);

        Assert.Equal(["grep", "-i", "two words"], HackEnvironment.GetCommandLineArgs());
        Assert.Equal("alice", HackEnvironment.UserName);
        Assert.Equal("black-ice", HackEnvironment.MachineName);
        Assert.Equal(1, HackEnvironment.ProcessId);
        Assert.Equal("\n", HackEnvironment.NewLine);
        Assert.Equal(HackTestHost.Home, HackEnvironment.CurrentDirectory);
    }

    [Fact]
    public void Variables_can_be_set_replaced_and_removed()
    {
        using IDisposable scope = Enter(new FakeAppFileSystemGateway(), new FakeAppEnvironmentGateway(HackTestHost.Home));

        HackEnvironment.SetEnvironmentVariable("EDITOR", "nano");
        HackEnvironment.SetEnvironmentVariable("EMPTY", string.Empty);
        Assert.Equal("nano", HackEnvironment.GetEnvironmentVariable("EDITOR"));
        Assert.Equal(string.Empty, HackEnvironment.GetEnvironmentVariable("EMPTY"));

        HackEnvironment.SetEnvironmentVariable("EDITOR", null);
        Assert.Null(HackEnvironment.GetEnvironmentVariable("EDITOR"));
        Assert.Equal(["EMPTY", "HOME"], HackEnvironment.GetEnvironmentVariables().Keys.Order(StringComparer.Ordinal));

        Assert.Throws<ArgumentException>(() => HackEnvironment.SetEnvironmentVariable("A=B", "x"));
        Assert.Throws<ArgumentException>(() => HackEnvironment.SetEnvironmentVariable(string.Empty, "x"));
    }

    [Fact]
    public async Task SetCurrentDirectoryAsync_changes_where_relative_paths_resolve()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory(HackTestHost.Home)
            .WithDirectory("/home/alice/Documents")
            .WithFile("/home/alice/Documents/notes.txt", "hello");
        FakeAppEnvironmentGateway environment = new(HackTestHost.Home);
        using IDisposable scope = Enter(fileSystem, environment);

        await HackEnvironment.SetCurrentDirectoryAsync("Documents");

        Assert.Equal("/home/alice/Documents", HackEnvironment.CurrentDirectory);
        Assert.Equal("/home/alice/Documents", environment.CurrentDirectory);
        Assert.Equal("/home/alice/Documents/notes.txt", HackPath.GetFullPath("notes.txt"));
        Assert.Equal("hello", await HackFile.ReadAllTextAsync("notes.txt"));

        await HackEnvironment.SetCurrentDirectoryAsync("~");
        Assert.Equal(HackTestHost.Home, HackEnvironment.CurrentDirectory);
    }

    [Fact]
    public async Task SetCurrentDirectoryAsync_refuses_a_missing_path_or_a_file()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithDirectory(HackTestHost.Home)
            .WithFile("/home/alice/file.txt", "x");
        using IDisposable scope = Enter(fileSystem, new FakeAppEnvironmentGateway(HackTestHost.Home));

        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackEnvironment.SetCurrentDirectoryAsync("/nowhere"));
        HackIOException notDirectory = await Assert.ThrowsAsync<HackIOException>(() => HackEnvironment.SetCurrentDirectoryAsync("file.txt"));
        Assert.Equal(Simulation.Abstractions.FileSystem.FileSystemErrorCode.NotDirectory, notDirectory.ErrorCode);
        Assert.Equal(HackTestHost.Home, HackEnvironment.CurrentDirectory);
    }

    [Fact]
    public async Task A_change_of_directory_replaces_the_directory_pinned_by_the_scope()
    {
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway().WithDirectory(HackTestHost.Home).WithDirectory("/tmp");
        MinimalAppExecutionContext context = new(
            HackTestHost.Manifest, fileSystem: fileSystem, homeDirectory: HackTestHost.Home,
            environment: new FakeAppEnvironmentGateway(HackTestHost.Home));

        using (HackExecutionContext.Enter(context, "/var"))
        {
            Assert.Equal("/var", HackEnvironment.CurrentDirectory);
            await HackEnvironment.SetCurrentDirectoryAsync("/tmp");
            Assert.Equal("/tmp", HackEnvironment.CurrentDirectory);
        }
    }

    private static IDisposable Enter(FakeAppFileSystemGateway fileSystem, FakeAppEnvironmentGateway environment) =>
        HackExecutionContext.Enter(new MinimalAppExecutionContext(
            HackTestHost.Manifest, fileSystem: fileSystem, userId: "0f00", homeDirectory: HackTestHost.Home, environment: environment));
}
