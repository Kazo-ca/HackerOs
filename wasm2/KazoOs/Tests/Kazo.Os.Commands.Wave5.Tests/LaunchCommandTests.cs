using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Commands.Launch;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

public sealed class LaunchCommandTests
{
    [Fact]
    public async Task ExecuteAsync_DispatchesRealLaunchRequest_WithArguments()
    {
        FakeAppIntentGateway intents = new();
        TerminalExecutionContext context = CreateContext(intents, "org.kazoos.calculator", "--fresh");

        int exitCode = await new LaunchCommand(AppDefinitionMapper.ToManifest(new LaunchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(0, exitCode);
        (string appId, IReadOnlyList<string> arguments) = Assert.Single(intents.Requests);
        Assert.Equal("org.kazoos.calculator", appId);
        Assert.Equal(["--fresh"], arguments);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownApp_ReportsNotFound()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway()
            .WithResult("org.kazoos.missing", new AppIntentLaunchResult(AppIntentLaunchOutcome.NotFound, "intent.not-found"));
        TerminalExecutionContext context = CreateContext(intents, "org.kazoos.missing");

        int exitCode = await new LaunchCommand(AppDefinitionMapper.ToManifest(new LaunchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("no such application", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_CapabilityDenied_ReportsRealMessage()
    {
        FakeAppIntentGateway intents = new FakeAppIntentGateway().WithCapabilityDenied("org.kazoos.settings");
        TerminalExecutionContext context = CreateContext(intents, "org.kazoos.settings");

        int exitCode = await new LaunchCommand(AppDefinitionMapper.ToManifest(new LaunchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("apps.launch", context.StandardError.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_MissingArgument_ReturnsError()
    {
        FakeAppIntentGateway intents = new();
        TerminalExecutionContext context = CreateContext(intents);

        int exitCode = await new LaunchCommand(AppDefinitionMapper.ToManifest(new LaunchApp()))
            .ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Empty(intents.Requests);
    }

    private static TerminalExecutionContext CreateContext(
        FakeAppIntentGateway intents, params string[] arguments) => new(
        new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new LaunchApp()), intents: intents),
        arguments,
        new StringReader(string.Empty),
        new StringWriter(),
        new StringWriter(),
        "/home/user",
        new Dictionary<string, string>());
}
