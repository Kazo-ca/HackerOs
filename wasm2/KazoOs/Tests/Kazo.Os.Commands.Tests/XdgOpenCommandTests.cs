using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.XdgOpen;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Tests;

/// <summary><c>xdg-open</c> (ADR 0096, section 3): paths become OpenFile, URIs OpenUri, with xdg-open's exit codes.</summary>
public sealed class XdgOpenCommandTests
{
    [Fact]
    public async Task A_relative_file_is_opened_with_its_association()
    {
        Run run = await RunAsync(new FakeAppIntentGateway(), "notes.txt");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("/home/alice/notes.txt", Assert.Single(run.Intents.OpenFileRequests).Value);
        Assert.Null(Assert.Single(run.Intents.OpenFileMediaTypes));
    }

    [Fact]
    public async Task A_folder_is_opened_as_inode_directory()
    {
        Run run = await RunAsync(new FakeAppIntentGateway(), "~/Documents");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("/home/alice/Documents", Assert.Single(run.Intents.OpenFileRequests).Value);
        Assert.Equal("inode/directory", Assert.Single(run.Intents.OpenFileMediaTypes));
    }

    [Fact]
    public async Task A_missing_file_exits_4_without_sending_anything()
    {
        Run run = await RunAsync(new FakeAppIntentGateway(), "missing.txt");

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("does not exist", run.Stderr, StringComparison.Ordinal);
        Assert.Empty(run.Intents.OpenFileRequests);
    }

    [Fact]
    public async Task A_file_no_app_can_open_exits_3()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway()
            .WithOpenResult("/home/alice/notes.txt", new AppIntentOpenFileResult(AppIntentOpenFileOutcome.NoHandler));

        Run run = await RunAsync(intents, "notes.txt");

        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public async Task Several_handlers_and_no_default_exit_4_and_list_them()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway()
            .WithOpenResult("/home/alice/notes.txt", new AppIntentOpenFileResult(
                AppIntentOpenFileOutcome.ChooserRequired, ["org.hackeros.text-editor", "org.hackeros.code-editor"]));

        Run run = await RunAsync(intents, "notes.txt");

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("org.hackeros.text-editor, org.hackeros.code-editor", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_uri_is_sent_as_open_uri()
    {
        FakeAppIntentGateway intents = new();

        Run run = await RunAsync(intents, "https://example.hackeros/page");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal("uri:https://example.hackeros/page", Assert.Single(intents.IntentRequests));
        Assert.Empty(intents.OpenFileRequests);
    }

    [Fact]
    public async Task A_scheme_nobody_handles_exits_3()
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(AppIntentOutcome.NoHandler) };

        Run run = await RunAsync(intents, "gopher://hole.hackeros/");

        Assert.Equal(3, run.ExitCode);
        Assert.Contains("'gopher:'", run.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData]
    [InlineData("a.txt", "b.txt")]
    public async Task A_bad_command_line_exits_1(params string[] arguments)
    {
        Run run = await RunAsync(new FakeAppIntentGateway(), arguments);

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("usage: xdg-open", run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_denied_launch_exits_4_with_the_reason()
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(AppIntentOutcome.AccessDenied, ErrorCode: "intent.capability-denied") };

        Run run = await RunAsync(intents, "https://example.hackeros/");

        Assert.Equal(4, run.ExitCode);
        Assert.Contains("intent.capability-denied", run.Stderr, StringComparison.Ordinal);
    }

    private static async Task<Run> RunAsync(FakeAppIntentGateway intents, params string[] arguments)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new XdgOpenApp());
        FakeAppFileSystemGateway fileSystem = new FakeAppFileSystemGateway()
            .WithFile("/home/alice/notes.txt", "hello")
            .WithDirectory("/home/alice/Documents");
        MinimalAppExecutionContext app = new(
            manifest, fileSystem: fileSystem, intents: intents, homeDirectory: "/home/alice",
            environment: new FakeAppEnvironmentGateway("/home/alice"));
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app, arguments, new StringReader(string.Empty), new StringWriter(), stderr,
            "/home/alice", new Dictionary<string, string> { ["HOME"] = "/home/alice" });

        using IDisposable scope = KazoExecutionContext.Enter(app);
        int exitCode = await new XdgOpenCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return new Run(exitCode, stderr.ToString(), intents);
    }

    private sealed record Run(int ExitCode, string Stderr, FakeAppIntentGateway Intents);
}
