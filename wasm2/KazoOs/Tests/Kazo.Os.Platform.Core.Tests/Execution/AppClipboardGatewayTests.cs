using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Platform.Core.Events;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>Contract tests of the session clipboard behind <c>KazoClipboard</c> (ADR 0056, section 12).</summary>
public sealed class AppClipboardGatewayTests
{
    private static readonly SessionId Session = SessionId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SessionId OtherSession = SessionId.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task A_file_list_cut_in_one_app_is_read_by_another_app_of_the_session()
    {
        SystemClipboard clipboard = new();
        AppClipboardGateway explorer = Gateway(clipboard, Session, AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite);
        AppClipboardGateway other = Gateway(clipboard, Session, AppCapabilities.ClipboardRead);

        await explorer.SetFileDropListAsync(new KazoClipboardFileList(KazoClipboardFileOperation.Cut, ["/home/alice/a b.txt", "/home/alice/dir"]));

        KazoClipboardFileList? read = await other.GetFileDropListAsync();
        Assert.NotNull(read);
        Assert.Equal(KazoClipboardFileOperation.Cut, read.Operation);
        Assert.Equal(["/home/alice/a b.txt", "/home/alice/dir"], read.Paths);
        // GNOME offers the paths as text/plain too.
        Assert.Equal("/home/alice/a b.txt\n/home/alice/dir", await other.GetTextAsync());
        Assert.Equal(
            "cut\nfile:///home/alice/a%20b.txt\nfile:///home/alice/dir",
            clipboard.Get(Session, KazoClipboardFileList.GnomeCopiedFilesMediaType));
    }

    [Fact]
    public async Task Sessions_do_not_share_their_clipboard()
    {
        SystemClipboard clipboard = new();
        await Gateway(clipboard, Session, AppCapabilities.ClipboardWrite).SetTextAsync("secret");

        Assert.Null(await Gateway(clipboard, OtherSession, AppCapabilities.ClipboardRead).GetTextAsync());
    }

    [Fact]
    public async Task Text_replaces_a_file_list()
    {
        SystemClipboard clipboard = new();
        AppClipboardGateway gateway = Gateway(clipboard, Session, AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite);
        await gateway.SetFileDropListAsync(new KazoClipboardFileList(KazoClipboardFileOperation.Copy, ["/etc/hostname"]));

        await gateway.SetTextAsync("hello");

        Assert.Null(await gateway.GetFileDropListAsync());
        Assert.Equal("hello", await gateway.GetTextAsync());
    }

    [Fact]
    public async Task Reading_needs_clipboard_read_and_writing_needs_clipboard_write()
    {
        SystemClipboard clipboard = new();
        AppClipboardGateway readOnly = Gateway(clipboard, Session, AppCapabilities.ClipboardRead);
        AppClipboardGateway writeOnly = Gateway(clipboard, Session, AppCapabilities.ClipboardWrite);

        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => readOnly.SetTextAsync("x").AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => readOnly.ClearAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => writeOnly.GetTextAsync().AsTask());
        await Assert.ThrowsAsync<AppGatewayAccessDeniedException>(() => writeOnly.GetFileDropListAsync().AsTask());
    }

    [Fact]
    public async Task Changed_reaches_other_apps_of_the_session_only()
    {
        SystemClipboard clipboard = new();
        AppClipboardGateway writer = Gateway(clipboard, Session, AppCapabilities.ClipboardWrite);
        AppClipboardGateway sameSession = Gateway(clipboard, Session);
        AppClipboardGateway otherSession = Gateway(clipboard, OtherSession);
        int same = 0, other = 0;
        EventHandler onSame = (_, _) => same++;
        sameSession.Changed += onSame;
        otherSession.Changed += (_, _) => other++;

        await writer.SetTextAsync("one");
        sameSession.Changed -= onSame;
        await writer.ClearAsync();

        Assert.Equal(1, same);
        Assert.Equal(0, other);
    }

    [Fact]
    public async Task Logout_empties_the_session_clipboard()
    {
        InMemoryEventBus bus = new();
        using SystemClipboard clipboard = new(bus);
        await Gateway(clipboard, Session, AppCapabilities.ClipboardWrite).SetTextAsync("kept until logout");

        bus.Publish(new SessionLoggedOutEvent(Session, LocalUserId.FromGuid(Guid.NewGuid()), DateTimeOffset.UnixEpoch));

        Assert.Null(clipboard.Get(Session, SystemClipboard.TextMediaType));
    }

    [Fact]
    public async Task KazoClipboard_resolves_relative_paths_and_maps_denials()
    {
        SystemClipboard clipboard = new();
        AppManifest manifest = Manifest(AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite);
        MinimalAppExecutionContext context = new(
            manifest,
            homeDirectory: "/home/alice",
            clipboard: Gateway(clipboard, Session, AppCapabilities.ClipboardRead, AppCapabilities.ClipboardWrite));

        using (KazoExecutionContext.Enter(context, "/home/alice/Documents"))
        {
            await KazoClipboard.SetFileDropListAsync(["report.txt", "~/notes.md"], KazoClipboardFileOperation.Copy);
            KazoClipboardFileList? list = await KazoClipboard.GetFileDropListAsync();

            Assert.Equal(["/home/alice/Documents/report.txt", "/home/alice/notes.md"], list!.Paths);
            Assert.True(await KazoClipboard.ContainsFileDropListAsync());
            await KazoClipboard.ClearAsync();
            Assert.False(await KazoClipboard.ContainsFileDropListAsync());
        }

        MinimalAppExecutionContext denied = new(manifest, homeDirectory: "/home/alice", clipboard: Gateway(clipboard, Session));
        using (KazoExecutionContext.Enter(denied))
        {
            KazoUnauthorizedAccessException error = await Assert.ThrowsAsync<KazoUnauthorizedAccessException>(() => KazoClipboard.GetTextAsync());
            Assert.Equal(AppCapabilities.ClipboardRead, error.RequiredCapability);
        }
    }

    [Theory]
    [InlineData("copy\nfile:///tmp/x", KazoClipboardFileOperation.Copy, "/tmp/x")]
    [InlineData("cut\r\nfile:///home/a%20b", KazoClipboardFileOperation.Cut, "/home/a b")]
    public void Gnome_copied_files_parse(string content, KazoClipboardFileOperation operation, string path)
    {
        KazoClipboardFileList? list = KazoClipboardFileList.TryParseGnomeCopiedFiles(content);

        Assert.Equal(operation, list!.Operation);
        Assert.Equal([path], list.Paths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("move\nfile:///tmp/x")]
    [InlineData("copy\n/tmp/x")]
    public void Malformed_gnome_copied_files_are_rejected(string content) =>
        Assert.Null(KazoClipboardFileList.TryParseGnomeCopiedFiles(content));

    private static AppClipboardGateway Gateway(SystemClipboard clipboard, SessionId session, params string[] granted) =>
        new(clipboard, new FixedCapabilities(granted), session);

    private static AppManifest Manifest(params string[] capabilities) => new()
    {
        Id = "org.test.clipboard",
        Name = "Clipboard test",
        Version = "1.0.0",
        PublisherId = "org.test",
        Description = "Clipboard contract test application.",
        Kind = AppKind.Window,
        EntryPoint = new AppEntryPointManifest("Test", "Test.Window"),
        SdkCompatibility = new AppSdkCompatibilityManifest(KazoOsSdk.Version),
        Presentation = new PresentationManifest("test", AppLaunchVisibility.Visible, []),
        Resources = AppResourceProfileManifest.None,
        Capabilities = capabilities
    };

    private sealed class FixedCapabilities(params string[] granted) : ICapabilityChecker
    {
        public CapabilityPolicyEvaluation Evaluate(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
            throw new NotSupportedException();

        public void Require(
            string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
        {
            if (!granted.Contains(capability, StringComparer.Ordinal))
            {
                throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
            }
        }
    }
}
