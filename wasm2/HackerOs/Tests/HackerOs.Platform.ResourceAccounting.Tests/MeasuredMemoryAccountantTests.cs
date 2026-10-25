using HackerOs.App.Abstractions;
using HackerOs.Platform.ResourceAccounting.Memory;
using HackerOs.Simulation.Abstractions.Processes;

namespace HackerOs.Platform.ResourceAccounting.Tests;

/// <summary>The four layers of ADR 0068 and the documented constants behind them.</summary>
public sealed class MeasuredMemoryAccountantTests
{
    private const string Editor = "org.hackeros.text-editor";
    private const string Ls = "org.hackeros.cmd.ls";
    private const string Cat = "org.hackeros.cmd.cat";

    private static readonly IReadOnlyDictionary<string, AppImage> Images = new Dictionary<string, AppImage>(StringComparer.Ordinal)
    {
        [Editor] = new([new("HackerOs.Apps.TextEditor.dll", 40_000, false), new("HackerOs.AppSdk.dll", 400_000, true), new("MudBlazor.dll", 10_000_000, true)]),
        [Ls] = new([new("HackerOs.Commands.dll", 470_000, false), new("HackerOs.AppSdk.dll", 400_000, true)]),
        [Cat] = new([new("HackerOs.Commands.dll", 470_000, false), new("HackerOs.AppSdk.dll", 400_000, true)])
    };

    private readonly FakeHeapProbe _probe = new();
    private readonly ManualTimeProvider _time = new();
    private readonly AllocationTracker _allocations;
    private readonly KernelMemoryLedger _kernel = new();
    private readonly MeasuredMemoryAccountant _accountant;

    public MeasuredMemoryAccountantTests()
    {
        _allocations = new AllocationTracker(_probe);
        _allocations.OnContextSwitch(new ExecutionContextSwitch(null, null, false)); // first reading
        _accountant = new MeasuredMemoryAccountant(_allocations, _kernel, Images, _probe, _time);
    }

    [Fact]
    public void The_constants_are_the_documented_ones()
    {
        // doc/en/wasm/dev/system-metrics.md, "Mémoire mesurée": change both together.
        Assert.Equal(TimeSpan.FromSeconds(10), MemoryAccountingConstants.ResidentHalfLife);
        Assert.Equal(512 * 1024, MemoryAccountingConstants.WindowBytes);
    }

    [Fact]
    public void Image_layer_is_the_own_assembly_and_the_shared_ones_it_maps()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);

        Assert.True(_accountant.TryGetProcessMemory(editor, [editor], out ProcessMemoryUsage usage));

        Assert.Equal(40_000, usage.ImageBytes);
        Assert.Equal(10_440_000, usage.FileBytes);
    }

    [Fact]
    public void Kernel_layer_is_what_the_os_holds_plus_a_fixed_cost_per_window()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        using KernelMemoryCharge buffer = _kernel.Charge(editor.Pid, KernelMemoryKind.StreamBuffer, 4_096);
        using KernelMemoryCharge pipe = _kernel.Charge(ls.Pid, KernelMemoryKind.Pipe, 100);

        _accountant.TryGetProcessMemory(editor, [editor, ls], out ProcessMemoryUsage editorUsage);
        _accountant.TryGetProcessMemory(ls, [editor, ls], out ProcessMemoryUsage lsUsage);

        Assert.Equal(4_096 + MemoryAccountingConstants.WindowBytes, editorUsage.KernelBytes);
        Assert.Equal(100, lsUsage.KernelBytes);
    }

    [Fact]
    public void VmRSS_adds_the_anonymous_file_and_kernel_parts()
    {
        ProcessMemoryUsage usage = new(ImageBytes: 1, FileBytes: 10, AnonymousBytes: 100, KernelBytes: 1_000, AllocatedBytes: 5);

        Assert.Equal(1_110, usage.ResidentBytes);
        Assert.Equal(100, usage.PrivateBytes);
    }

    [Fact]
    public void The_heap_is_split_by_recent_allocations_and_the_os_keeps_its_share()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        _allocations.Run(_probe, 10, 3_000);
        _allocations.Run(_probe, 11, 1_000);
        _probe.Allocated += 4_000; // the OS (rendering) ...
        _allocations.OnContextSwitch(new ExecutionContextSwitch(null, ProcessId.FromInt64(11), true)); // ... charged to nobody's frame
        _probe.HeapSizeBytes = 80_000;
        _probe.CollectionCount = 1;

        _accountant.TryGetProcessMemory(editor, [editor, ls], out ProcessMemoryUsage editorUsage);
        _accountant.TryGetProcessMemory(ls, [editor, ls], out ProcessMemoryUsage lsUsage);
        _accountant.TryGetSystemMemory([editor, ls], out SystemMemoryUsage system);

        Assert.Equal(30_000, editorUsage.AnonymousBytes);
        Assert.Equal(10_000, lsUsage.AnonymousBytes);
        Assert.Equal(3_000, editorUsage.AllocatedBytes);
        // The shares add up to the heap: the OS's 40 000 bytes are in Slab with the window's cost.
        Assert.Equal(40_000, system.AnonymousBytes);
        Assert.Equal(40_000 + MemoryAccountingConstants.WindowBytes, system.KernelBytes);
    }

    [Fact]
    public void The_split_only_changes_after_a_garbage_collection()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        _allocations.Run(_probe, 10, 1_000);
        _probe.HeapSizeBytes = 10_000;
        _accountant.TryGetSystemMemory([editor, ls], out _);

        _allocations.Run(_probe, 11, 1_000_000);
        _accountant.TryGetProcessMemory(ls, [editor, ls], out ProcessMemoryUsage beforeCollection);
        _probe.CollectionCount++;
        _accountant.TryGetProcessMemory(ls, [editor, ls], out ProcessMemoryUsage afterCollection);

        Assert.Equal(0, beforeCollection.AnonymousBytes);
        Assert.True(afterCollection.AnonymousBytes > 9_000);
    }

    [Fact]
    public void Old_allocations_weigh_half_after_one_half_life()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        _allocations.Run(_probe, 10, 2_000);
        _accountant.TryGetSystemMemory([editor, ls], out _); // weights: editor 2000

        _time.Advance(MemoryAccountingConstants.ResidentHalfLife);
        _allocations.Run(_probe, 11, 1_000); // editor ages to 1000, ls is fresh at 1000
        _probe.HeapSizeBytes = 20_000;
        _probe.CollectionCount++;
        _accountant.TryGetProcessMemory(editor, [editor, ls], out ProcessMemoryUsage editorUsage);
        _accountant.TryGetProcessMemory(ls, [editor, ls], out ProcessMemoryUsage lsUsage);

        Assert.Equal(10_000, editorUsage.AnonymousBytes);
        Assert.Equal(10_000, lsUsage.AnonymousBytes);
    }

    [Fact]
    public void Kernel_bytes_already_named_are_taken_out_of_the_heap_before_the_split()
    {
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        using KernelMemoryCharge pipe = _kernel.Charge(ls.Pid, KernelMemoryKind.Pipe, 6_000);
        _allocations.Run(_probe, 11, 1_000);
        _probe.HeapSizeBytes = 10_000;

        _accountant.TryGetProcessMemory(ls, [ls], out ProcessMemoryUsage usage);

        Assert.Equal(4_000, usage.AnonymousBytes);
        Assert.Equal(6_000, usage.KernelBytes);
    }

    [Fact]
    public void Shared_assemblies_are_mapped_once_machine_wide()
    {
        ProcessRecord editor = MemoryRecords.Process(10, Editor, AppKind.Window);
        ProcessRecord ls = MemoryRecords.Process(11, Ls);
        ProcessRecord cat = MemoryRecords.Process(12, Cat);

        _accountant.TryGetSystemMemory([editor, ls, cat], out SystemMemoryUsage system);

        // TextEditor + AppSdk + MudBlazor + Commands (shared by ls and cat, like BusyBox).
        Assert.Equal(40_000 + 400_000 + 10_000_000 + 470_000, system.MappedBytes);
        Assert.Equal(3, system.MeasuredProcesses);
    }

    [Fact]
    public void A_process_with_no_image_that_never_ran_code_stays_simulated()
    {
        ProcessRecord game = MemoryRecords.Process(20, "org.game.mainframe-daemon");

        Assert.False(_accountant.TryGetProcessMemory(game, [game], out _));
    }

    [Fact]
    public void A_process_that_ran_code_is_measured_even_without_an_image()
    {
        ProcessRecord shell = MemoryRecords.Process(21, "org.hackeros.shell");
        _allocations.Run(_probe, 21, 500);

        Assert.True(_accountant.TryGetProcessMemory(shell, [shell], out ProcessMemoryUsage usage));
        Assert.Equal(0, usage.ImageBytes);
        Assert.Equal(500, usage.AllocatedBytes);
    }

    [Fact]
    public void Dead_processes_are_forgotten()
    {
        ProcessRecord shell = MemoryRecords.Process(21, "org.hackeros.shell");
        _allocations.Run(_probe, 21, 500);
        using KernelMemoryCharge pipe = _kernel.Charge(shell.Pid, KernelMemoryKind.Pipe, 100);

        _accountant.TryGetSystemMemory([], out SystemMemoryUsage system);

        Assert.False(_allocations.HasRun(shell.Pid));
        Assert.Equal(0, _kernel.GetBytes(shell.Pid));
        Assert.Equal(0, system.MeasuredProcesses);
        Assert.False(_accountant.TryGetProcessMemory(MemoryRecords.Process(21, "org.hackeros.shell", stopped: true), [], out _));
    }
}
