using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.Apps;

/// <summary>The <see cref="KazoApps"/> façades of the intents of ADR 0096 and how they report each outcome.</summary>
public sealed class KazoAppsIntentTests
{
    [Fact]
    public async Task RevealFileAsync_sends_the_path()
    {
        FakeAppIntentGateway intents = new();
        using (Enter(intents))
        {
            await KazoApps.RevealFileAsync("/home/user/notes.txt");
        }

        Assert.Equal("reveal:/home/user/notes.txt", Assert.Single(intents.IntentRequests));
    }

    [Theory]
    [InlineData(AppIntentOutcome.NotFound, typeof(KazoFileNotFoundException))]
    [InlineData(AppIntentOutcome.AccessDenied, typeof(KazoUnauthorizedAccessException))]
    [InlineData(AppIntentOutcome.NoHandler, typeof(KazoAppLaunchException))]
    public async Task RevealFileAsync_turns_failures_into_sdk_exceptions(AppIntentOutcome outcome, Type expected)
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(outcome, ErrorCode: "intent.reveal-file.x") };
        using (Enter(intents))
        {
            Exception exception = await Assert.ThrowsAnyAsync<Exception>(() => KazoApps.RevealFileAsync("/home/user/notes.txt"));
            Assert.IsType(expected, exception);
        }
    }

    [Fact]
    public async Task EditFileAsync_sends_an_edit_request()
    {
        FakeAppIntentGateway intents = new();
        using (Enter(intents))
        {
            KazoOpenFileResult result = await KazoApps.EditFileAsync("/home/user/notes.txt");
            Assert.Equal(KazoOpenFileStatus.Opened, result.Status);
        }

        Assert.Equal("edit:/home/user/notes.txt", Assert.Single(intents.IntentRequests));
    }

    [Fact]
    public async Task OpenUriAsync_reports_the_candidates_when_the_user_must_choose()
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(AppIntentOutcome.ChooserRequired, ["org.a", "org.b"]) };
        using (Enter(intents))
        {
            KazoIntentResult result = await KazoApps.OpenUriAsync("gopher://hole.hack/");

            Assert.Equal(KazoIntentStatus.ChooserRequired, result.Status);
            Assert.Equal(["org.a", "org.b"], result.CandidateAppIds);
        }

        Assert.Equal("uri:gopher://hole.hack/", Assert.Single(intents.IntentRequests));
    }

    [Fact]
    public async Task OpenUriAsync_refuses_a_relative_uri_before_sending()
    {
        FakeAppIntentGateway intents = new();
        using (Enter(intents))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => KazoApps.OpenUriAsync("docs/page.html"));
        }

        Assert.Empty(intents.IntentRequests);
    }

    [Fact]
    public async Task ShowSettingsAsync_defaults_to_the_current_app()
    {
        FakeAppIntentGateway intents = new();
        using (Enter(intents))
        {
            await KazoApps.ShowSettingsAsync();
        }

        Assert.Equal("settings:org.example.caller", Assert.Single(intents.IntentRequests));
    }

    [Fact]
    public async Task ShowSettingsAsync_reports_an_unknown_app()
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(AppIntentOutcome.NotFound, ErrorCode: "intent.show-settings.not-found") };
        using (Enter(intents))
        {
            await Assert.ThrowsAsync<KazoAppLaunchException>(() => KazoApps.ShowSettingsAsync("org.nope"));
        }
    }

    [Fact]
    public async Task SendIntentAsync_passes_the_target_and_the_always_use_choice()
    {
        FakeAppIntentGateway intents = new();
        using (Enter(intents))
        {
            KazoIntentResult result = await KazoApps.SendIntentAsync("org.example.share.v1", "{}", "org.b", rememberChoice: true);
            Assert.Equal(KazoIntentStatus.Delivered, result.Status);
        }

        Assert.Equal("send:org.example.share.v1:org.b:True", Assert.Single(intents.IntentRequests));
    }

    [Fact]
    public async Task SendIntentAsync_turns_a_refused_intent_into_an_argument_exception()
    {
        FakeAppIntentGateway intents = new() { NextIntentResult = new AppIntentResult(AppIntentOutcome.Invalid, ErrorCode: "intent.custom.payload-invalid") };
        using (Enter(intents))
        {
            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
                () => KazoApps.SendIntentAsync("org.example.share.v1", "[1]"));
            Assert.Contains("intent.custom.payload-invalid", exception.Message, StringComparison.Ordinal);
        }
    }

    private static IDisposable Enter(FakeAppIntentGateway intents) =>
        KazoExecutionContext.Enter(new MinimalAppExecutionContext(AppDefinitionMapper.ToManifest(new CallerApp()), intents: intents));

    private sealed class CallerApp : AppDefinition
    {
        public override string Id => "org.example.caller";

        public override string Name => "Caller";

        public override AppKind Kind => AppKind.Window;

        public override Type EntryPoint => typeof(CallerApp);

        public override IReadOnlyList<string> Capabilities => [AppCapabilities.AppsLaunch];
    }
}
