using Kazo.Os.AppSdk.Clipboard;
using Kazo.Os.Platform.Core.Clipboard;
using Kazo.Os.Platform.Core.Shell;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>The OS-resources layer of ADR 0068: charges, the clipboard and shell pipes.</summary>
public sealed class KernelMemoryChargeTests
{
    private static readonly SessionId Session = SessionId.FromGuid(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    [Fact]
    public void A_charge_reports_each_resize_and_releases_everything_once()
    {
        List<long> deltas = [];
        KernelMemoryCharge charge = new(deltas.Add, 100);

        charge.Resize(250);
        charge.Resize(250);
        charge.Resize(-5);
        charge.Resize(40);
        charge.Dispose();
        charge.Dispose();
        charge.Resize(1000);

        Assert.Equal([100, 150, -250, 40, -40], deltas);
        Assert.Equal(0, charge.Bytes);
    }

    [Fact]
    public void The_none_charge_records_nothing()
    {
        KernelMemoryCharge.None.Resize(1000);
        KernelMemoryCharge.None.Dispose();

        Assert.Equal(0, KernelMemoryCharge.None.Bytes);
    }

    [Fact]
    public void Clipboard_content_is_charged_to_the_copying_process_until_replaced_or_cleared()
    {
        RecordingKernelMemoryLedger ledger = new();
        using SystemClipboard clipboard = new(ledger: ledger);
        ProcessId editor = ProcessId.FromInt64(10);
        ProcessId explorer = ProcessId.FromInt64(11);

        clipboard.SetText(Session, "hello", owner: editor);
        // UTF-16: two bytes per character of the media type and of the content.
        Assert.Equal((SystemClipboard.TextMediaType.Length + 5) * 2, ledger.BytesOf(10, KernelMemoryKind.Clipboard));

        clipboard.SetFileDropList(Session, new HackClipboardFileList(HackClipboardFileOperation.Copy, ["/etc/hostname"]), explorer);
        Assert.Equal(0, ledger.BytesOf(10, KernelMemoryKind.Clipboard));
        Assert.True(ledger.BytesOf(11, KernelMemoryKind.Clipboard) > 0);

        clipboard.Clear(Session);
        Assert.Equal(0, ledger.BytesOf(11, KernelMemoryKind.Clipboard));
    }

    [Fact]
    public void Clipboard_content_from_the_os_or_the_host_is_charged_to_nobody()
    {
        RecordingKernelMemoryLedger ledger = new();
        using SystemClipboard clipboard = new(ledger: ledger);

        clipboard.SetText(Session, "from the host", SystemClipboardOrigin.Host);

        Assert.Equal(0, ledger.ChargesOpened);
    }

    [Fact]
    public async Task A_pipe_charges_its_buffered_bytes_and_releases_them_once_drained_and_closed()
    {
        RecordingKernelMemoryLedger ledger = new();
        ShellPipe pipe = new(charge: ledger.Charge(ProcessId.FromInt64(3), KernelMemoryKind.Pipe));

        await pipe.Writer.WriteAsync("0123456789");
        Assert.Equal(20, ledger.BytesOf(3, KernelMemoryKind.Pipe));

        char[] buffer = new char[4];
        await pipe.Reader.ReadAsync(buffer, 0, 4);
        Assert.Equal(12, ledger.BytesOf(3, KernelMemoryKind.Pipe));

        pipe.Writer.Dispose();
        Assert.Equal(12, ledger.BytesOf(3, KernelMemoryKind.Pipe)); // still buffered
        Assert.Equal("456789", await pipe.Reader.ReadToEndAsync());
        Assert.Equal(0, ledger.BytesOf(3, KernelMemoryKind.Pipe));
    }

    [Fact]
    public async Task Closing_the_read_end_releases_the_pipe_buffer()
    {
        RecordingKernelMemoryLedger ledger = new();
        ShellPipe pipe = new(charge: ledger.Charge(ProcessId.FromInt64(3), KernelMemoryKind.Pipe));
        await pipe.Writer.WriteAsync("abc");

        pipe.Reader.Dispose();

        Assert.Equal(0, ledger.BytesOf(3, KernelMemoryKind.Pipe));
    }
}
