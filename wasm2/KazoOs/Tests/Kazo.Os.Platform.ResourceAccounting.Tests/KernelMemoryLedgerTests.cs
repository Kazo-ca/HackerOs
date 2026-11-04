using Kazo.Os.Platform.ResourceAccounting.Memory;
using Kazo.Os.Simulation.Abstractions.Processes;

namespace Kazo.Os.Platform.ResourceAccounting.Tests;

/// <summary>The OS-resources layer of ADR 0068.</summary>
public sealed class KernelMemoryLedgerTests
{
    [Fact]
    public void Charges_add_up_per_process_and_kind_and_release_on_disposal()
    {
        KernelMemoryLedger ledger = new();
        ProcessId pid = ProcessId.FromInt64(4);

        KernelMemoryCharge clipboard = ledger.Charge(pid, KernelMemoryKind.Clipboard, 100);
        KernelMemoryCharge socket = ledger.Charge(pid, KernelMemoryKind.Socket, 1_000);
        clipboard.Resize(300);

        Assert.True(ledger.IsTracking);
        Assert.Equal(1_300, ledger.GetBytes(pid));
        Assert.Equal(300, ledger.GetBytes(pid, KernelMemoryKind.Clipboard));

        socket.Dispose();
        clipboard.Dispose();
        Assert.Equal(0, ledger.GetBytes(pid));
    }

    [Fact]
    public void A_charge_outliving_its_forgotten_process_never_touches_a_new_one()
    {
        KernelMemoryLedger ledger = new();
        ProcessId pid = ProcessId.FromInt64(4);
        KernelMemoryCharge late = ledger.Charge(pid, KernelMemoryKind.Clipboard, 100);

        ledger.Retain(new HashSet<long>());
        late.Dispose();

        Assert.Equal(0, ledger.GetBytes(pid));
    }
}
