using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Platform.Core.Tests.Clipboard;

/// <summary>Formats, sequence number and origin of the OS clipboard (ADR 0065).</summary>
public sealed class SystemClipboardTests
{
    private static readonly SessionId Session = SessionId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SessionId OtherSession = SessionId.FromGuid(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void The_sequence_number_counts_every_change_of_one_session()
    {
        using SystemClipboard clipboard = new();
        Assert.Equal(0, clipboard.GetSequenceNumber(Session));

        clipboard.SetText(Session, "a");
        clipboard.SetFileDropList(Session, new HackClipboardFileList(HackClipboardFileOperation.Copy, ["/etc/hostname"]));
        clipboard.Clear(Session);
        clipboard.Clear(Session);   // already empty: no change

        Assert.Equal(3, clipboard.GetSequenceNumber(Session));
        Assert.Equal(0, clipboard.GetSequenceNumber(OtherSession));
    }

    [Fact]
    public void An_entry_carries_any_set_of_formats_its_sequence_number_and_origin()
    {
        using SystemClipboard clipboard = new();
        clipboard.SetTargets(
            Session,
            new Dictionary<string, string> { ["text/plain"] = "hello", ["text/html"] = "<b>hello</b>" },
            SystemClipboardOrigin.Host);

        SystemClipboardEntry? entry = clipboard.GetEntry(Session);
        Assert.NotNull(entry);
        Assert.Equal("hello", entry.Text);
        Assert.Equal("<b>hello</b>", clipboard.Get(Session, "text/html"));
        Assert.Equal(1, entry.SequenceNumber);
        Assert.Equal(SystemClipboardOrigin.Host, entry.Origin);
    }

    [Fact]
    public void A_file_list_is_offered_as_gnome_copied_files_and_as_text_by_an_app()
    {
        using SystemClipboard clipboard = new();
        clipboard.SetFileDropList(Session, new HackClipboardFileList(HackClipboardFileOperation.Cut, ["/home/alice/a.txt"]));

        SystemClipboardEntry entry = clipboard.GetEntry(Session)!;
        Assert.Equal(SystemClipboardOrigin.Guest, entry.Origin);
        Assert.Equal("/home/alice/a.txt", entry.Text);
        Assert.Equal("cut\nfile:///home/alice/a.txt", entry.Targets[HackClipboardFileList.GnomeCopiedFilesMediaType]);
    }

    [Fact]
    public void An_empty_set_of_formats_clears_the_clipboard()
    {
        using SystemClipboard clipboard = new();
        clipboard.SetText(Session, "a");
        int changes = 0;
        clipboard.Changed += (_, _) => changes++;

        clipboard.SetTargets(Session, new Dictionary<string, string>(), SystemClipboardOrigin.Guest);

        Assert.Null(clipboard.GetEntry(Session));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Entries_are_copied_so_the_caller_cannot_change_them_afterwards()
    {
        using SystemClipboard clipboard = new();
        Dictionary<string, string> targets = new() { ["text/plain"] = "before" };
        clipboard.SetTargets(Session, targets, SystemClipboardOrigin.Guest);

        targets["text/plain"] = "after";

        Assert.Equal("before", clipboard.GetEntry(Session)!.Text);
    }

    [Fact]
    public void A_format_without_media_type_is_refused()
    {
        using SystemClipboard clipboard = new();

        Assert.Throws<ArgumentException>(() => clipboard.SetTargets(
            Session, new Dictionary<string, string> { [" "] = "x" }, SystemClipboardOrigin.Guest));
    }
}
