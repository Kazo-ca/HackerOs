using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Platform.Core.Tests.Settings;
using Kazo.Os.Simulation.Abstractions.Clipboard;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Clipboard;

/// <summary>Text shared between the OS clipboard and the real machine's clipboard (ADR 0065).</summary>
public sealed class HostClipboardBridgeTests
{
    [Fact]
    public async Task Text_an_app_copies_is_written_to_the_host()
    {
        (SettingsFilesHarness harness, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();

        clipboard.SetText(session, "hello");
        await bridge.FlushAsync();

        Assert.Equal([("hello", true)], channel.Syncs);
        Assert.Equal((true, true), channel.Directions);
        Assert.Same(bridge, channel.Sink);
        GC.KeepAlive(harness);
    }

    [Fact]
    public async Task A_file_list_reaches_the_host_only_as_its_paths()
    {
        (_, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();

        clipboard.SetFileDropList(session, new KazoClipboardFileList(KazoClipboardFileOperation.Cut, ["/home/alice/a.txt", "/home/alice/b"]));
        await bridge.FlushAsync();

        Assert.Equal([("/home/alice/a.txt\n/home/alice/b", true)], channel.Syncs);
    }

    [Fact]
    public async Task A_host_paste_becomes_the_current_entry_and_is_not_echoed_back()
    {
        (_, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();
        clipboard.SetFileDropList(session, new KazoClipboardFileList(KazoClipboardFileOperation.Copy, ["/etc/hostname"]));
        await bridge.FlushAsync();
        channel.Syncs.Clear();
        long before = clipboard.GetSequenceNumber(session);

        bridge.OnHostPaste("from the host", []);
        await bridge.FlushAsync();

        SystemClipboardEntry entry = clipboard.GetEntry(session)!;
        Assert.Equal("from the host", entry.Text);
        Assert.Equal(SystemClipboardOrigin.Host, entry.Origin);
        Assert.Equal(before + 1, entry.SequenceNumber);
        Assert.False(entry.Targets.ContainsKey(KazoClipboardFileList.GnomeCopiedFilesMediaType));
        Assert.Empty(channel.Syncs);

        bridge.OnHostPaste("from the host", []);
        Assert.Equal(before + 1, clipboard.GetSequenceNumber(session));
    }

    [Fact]
    public async Task Host_files_pasted_are_recorded_by_name()
    {
        (_, SystemClipboard clipboard, _, HostClipboardBridge bridge, SessionId session) = await StartAsync();

        bridge.OnHostPaste(null, ["photo.png", "notes.txt"]);

        SystemClipboardEntry entry = clipboard.GetEntry(session)!;
        Assert.Null(entry.Text);
        Assert.Equal("photo.png\nnotes.txt", entry.Targets[HostClipboardBridge.HostFilesMediaType]);
        Assert.Equal(SystemClipboardOrigin.Host, entry.Origin);
    }

    [Fact]
    public async Task A_native_copy_becomes_the_current_entry_without_a_second_host_write()
    {
        (_, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();

        bridge.OnNativeCopy("selected in a text field");
        await bridge.FlushAsync();

        Assert.Equal(SystemClipboardOrigin.GuestNative, clipboard.GetEntry(session)!.Origin);
        Assert.Equal("selected in a text field", clipboard.Get(session, SystemClipboard.TextMediaType));
        Assert.Empty(channel.Syncs);
    }

    [Theory]
    [InlineData("guest-to-host", true, false)]
    [InlineData("host-to-guest", false, true)]
    [InlineData("disabled", false, false)]
    public async Task The_sharing_key_decides_each_direction(string sharing, bool toHost, bool toGuest)
    {
        (_, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) =
            await StartAsync($"sharing={sharing}\n");

        clipboard.SetText(session, "guest text");
        await bridge.FlushAsync();
        bridge.OnHostPaste("host text", ["file.bin"]);

        Assert.Equal((toHost, toGuest), channel.Directions);
        // The page always learns the guest text (it pastes it into Kazo OS fields); only the host write depends on the mode.
        Assert.Equal([("guest text", toHost)], channel.Syncs);
        Assert.Equal(toGuest ? "host text" : "guest text", clipboard.Get(session, SystemClipboard.TextMediaType));
    }

    [Fact]
    public async Task A_locked_machine_value_wins_over_the_user_file()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        await harness.WriteAsRootAsync("/etc/kazoos/clipboard.config", "sharing=disabled\n[locked]\nsharing=true\n");
        AuthenticatedPrincipal principal = await harness.LoginAsync(harness.Alice);
        await harness.WriteAsRootAsync("/home/alice/.config/kazoos/clipboard.config", "sharing=bidirectional\n");
        using SystemClipboard clipboard = new(harness.EventBus);
        FakeChannel channel = new();
        await using HostClipboardBridge bridge = new(clipboard, harness.Session, harness.Settings, channel);

        await bridge.StartAsync();
        bridge.OnHostPaste("host text", []);

        Assert.Equal(ClipboardSharingMode.Disabled, bridge.Mode);
        Assert.Null(clipboard.GetEntry(principal.SessionId));
    }

    [Fact]
    public async Task Editing_the_file_changes_the_mode_while_the_os_runs()
    {
        (SettingsFilesHarness harness, _, FakeChannel channel, HostClipboardBridge bridge, _) = await StartAsync();

        await harness.WriteAsRootAsync("/etc/kazoos/clipboard.config", "sharing=host-to-guest\n");

        for (int attempt = 0; attempt < 200 && bridge.Mode != ClipboardSharingMode.HostToGuest; attempt++)
        {
            await Task.Delay(10);
            await bridge.FlushAsync();
        }

        Assert.Equal(ClipboardSharingMode.HostToGuest, bridge.Mode);
        Assert.Equal((false, true), channel.Directions);
    }

    [Fact]
    public async Task Logging_out_makes_the_page_forget_the_text()
    {
        (SettingsFilesHarness harness, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();
        clipboard.SetText(session, "secret");
        await bridge.FlushAsync();

        await harness.Session.LogoutAsync();
        await bridge.FlushAsync();

        Assert.Null(clipboard.GetEntry(session));
        Assert.Equal(((string?)null, false), channel.Syncs[^1]);
    }

    [Fact]
    public async Task A_refused_host_write_leaves_the_os_clipboard_intact()
    {
        (_, SystemClipboard clipboard, FakeChannel channel, HostClipboardBridge bridge, SessionId session) = await StartAsync();
        channel.FailWrites = true;

        clipboard.SetText(session, "kept");
        await bridge.FlushAsync();
        clipboard.SetText(session, "next");
        await bridge.FlushAsync();

        Assert.Equal("next", clipboard.Get(session, SystemClipboard.TextMediaType));
        Assert.Equal(2, channel.Attempts);
    }

    [Fact]
    public async Task Nothing_crosses_before_a_session_exists()
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        using SystemClipboard clipboard = new(harness.EventBus);
        FakeChannel channel = new();
        await using HostClipboardBridge bridge = new(clipboard, harness.Session, harness.Settings, channel);
        await bridge.StartAsync();

        bridge.OnHostPaste("host text", []);
        bridge.OnNativeCopy("copied");

        Assert.Empty(channel.Syncs);
    }

    private static async Task<(SettingsFilesHarness Harness, SystemClipboard Clipboard, FakeChannel Channel, HostClipboardBridge Bridge, SessionId Session)> StartAsync(
        string? systemConfig = null)
    {
        SettingsFilesHarness harness = new();
        await harness.BootAsync();
        if (systemConfig is not null)
        {
            await harness.WriteAsRootAsync("/etc/kazoos/clipboard.config", systemConfig);
        }

        AuthenticatedPrincipal principal = await harness.LoginAsync(harness.Alice);
        SystemClipboard clipboard = new(harness.EventBus);
        FakeChannel channel = new();
        HostClipboardBridge bridge = new(clipboard, harness.Session, harness.Settings, channel);
        await bridge.StartAsync();
        return (harness, clipboard, channel, bridge, principal.SessionId);
    }

    private sealed class FakeChannel : IHostClipboardChannel
    {
        public IHostClipboardSink? Sink { get; private set; }

        public (bool ToHost, bool ToGuest) Directions { get; private set; }

        public List<(string? Text, bool WriteToHost)> Syncs { get; } = [];

        public bool FailWrites { get; set; }

        public int Attempts { get; private set; }

        public ValueTask AttachAsync(IHostClipboardSink sink, CancellationToken cancellationToken = default)
        {
            Sink = sink;
            return ValueTask.CompletedTask;
        }

        public ValueTask SetDirectionsAsync(bool guestToHost, bool hostToGuest, CancellationToken cancellationToken = default)
        {
            Directions = (guestToHost, hostToGuest);
            return ValueTask.CompletedTask;
        }

        public ValueTask SyncGuestTextAsync(string? text, bool writeToHost, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (FailWrites)
            {
                throw new InvalidOperationException("Document is not focused.");
            }

            Syncs.Add((text, writeToHost));
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
