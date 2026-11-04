using System.Text;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

public sealed class HackFileTests
{
    [Fact]
    public async Task WriteAllText_creates_a_missing_file_with_the_umask_and_reads_it_back()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);

        await HackFile.WriteAllTextAsync("~/notes.txt", "héllo");

        Assert.Equal("héllo", fs.ContentOf("/home/alice/notes.txt"));
        Assert.Equal("héllo", await HackFile.ReadAllTextAsync("notes.txt"));
        Assert.Equal((UnixFileMode)0b110_100_100, await HackFile.GetUnixFileModeAsync("notes.txt"));
    }

    [Fact]
    public async Task Default_text_encoding_is_utf8_without_bom_and_an_explicit_encoding_writes_its_preamble()
    {
        using IDisposable scope = HackTestHost.Enter(out _);

        await HackFile.WriteAllTextAsync("plain.txt", "a");
        await HackFile.WriteAllTextAsync("bom.txt", "a", Encoding.UTF8);

        Assert.Equal(new byte[] { (byte)'a' }, await HackFile.ReadAllBytesAsync("plain.txt"));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'a' }, await HackFile.ReadAllBytesAsync("bom.txt"));
        Assert.Equal("a", await HackFile.ReadAllTextAsync("bom.txt"));
    }

    [Fact]
    public async Task CreateMode_overrides_the_umask_only_for_a_new_file()
    {
        using IDisposable scope = HackTestHost.Enter(out _);

        await HackFile.WriteAllTextAsync("secret", "x", new HackWriteOptions { CreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        await HackFile.WriteAllTextAsync("secret", "y", new HackWriteOptions { CreateMode = (UnixFileMode)0b111_111_111 });

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, await HackFile.GetUnixFileModeAsync("secret"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            HackFile.WriteAllTextAsync("bad", "x", new HackWriteOptions { CreateMode = UnixFileMode.SetUser }));
    }

    [Fact]
    public async Task WriteAllText_replaces_existing_content_and_returns_the_committed_revision()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "old");

        HackFileInfo committed = await HackFile.WriteAllTextAsync("a.txt", "new", new HackWriteOptions());

        Assert.Equal("new", fs.ContentOf("/home/alice/a.txt"));
        Assert.Equal(fs.RevisionOf("/home/alice/a.txt"), committed.Revision);
        Assert.Equal(3, committed.Length);
    }

    [Fact]
    public async Task Lines_and_bytes_round_trip()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);

        await HackFile.WriteAllLinesAsync("lines.txt", ["one", "two"]);
        await HackFile.AppendAllLinesAsync("lines.txt", ["three"]);
        await HackFile.WriteAllBytesAsync("blob.bin", [1, 2, 3]);

        Assert.Equal("one\ntwo\nthree\n", fs.ContentOf("/home/alice/lines.txt"));
        Assert.Equal(["one", "two", "three"], await HackFile.ReadAllLinesAsync("lines.txt"));
        Assert.Equal(new byte[] { 1, 2, 3 }, await HackFile.ReadAllBytesAsync("blob.bin"));
    }

    [Fact]
    public async Task AppendAllText_creates_then_appends()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);

        await HackFile.AppendAllTextAsync("log.txt", "a");
        await HackFile.AppendAllTextAsync("log.txt", "b");

        Assert.Equal("ab", fs.ContentOf("/home/alice/log.txt"));
    }

    [Fact]
    public async Task A_single_revision_conflict_is_retried_once_and_succeeds()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/alice/a.txt", "old");
        ScriptedFileSystemGateway gateway = new(fs);
        int conflicts = 0;
        gateway.OnWrite = request => conflicts++ == 0
            ? ScriptedFileSystemGateway.Error(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path.Value)
            : null;
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        await HackFile.WriteAllTextAsync("a.txt", "new");

        Assert.Equal(2, gateway.WriteCalls);
        Assert.Equal("new", fs.ContentOf("/home/alice/a.txt"));
    }

    [Fact]
    public async Task A_second_revision_conflict_throws_a_conflict_with_the_current_revision()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/alice/a.txt", "old");
        ScriptedFileSystemGateway gateway = new(fs)
        {
            OnWrite = request => ScriptedFileSystemGateway.Error(FileSystemOperation.Write, FileSystemErrorCode.RevisionConflict, request.Path.Value)
        };
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        HackFileConflictException conflict = await Assert.ThrowsAsync<HackFileConflictException>(() => HackFile.WriteAllTextAsync("a.txt", "new"));

        Assert.Equal(2, gateway.WriteCalls);
        Assert.Equal(1, conflict.CurrentRevision);
        Assert.Equal("/home/alice/a.txt", conflict.Path);
        Assert.IsAssignableFrom<IOException>(conflict);
    }

    [Fact]
    public async Task Append_retry_rereads_so_a_concurrent_change_is_kept()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/alice/log.txt", "a");
        RaceOnNextMutationFileSystemGateway racing = new(fs, "/home/alice/log.txt");
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(racing));

        await HackFile.AppendAllTextAsync("log.txt", "b");

        Assert.Equal("ab", fs.ContentOf("/home/alice/log.txt"));
    }

    [Fact]
    public async Task ExpectedRevision_is_strict_and_never_retried()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/alice/a.txt", "old");
        ScriptedFileSystemGateway gateway = new(fs);
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));
        long loaded = (await HackFileInfo.GetAsync("a.txt")).Revision!.Value;

        HackFileInfo saved = await HackFile.WriteAllTextAsync("a.txt", "mine", new HackWriteOptions { ExpectedRevision = loaded });
        fs.SimulateConcurrentChange("/home/alice/a.txt");

        await Assert.ThrowsAsync<HackFileConflictException>(() =>
            HackFile.WriteAllTextAsync("a.txt", "stale", new HackWriteOptions { ExpectedRevision = saved.Revision }));
        Assert.Equal(2, gateway.WriteCalls);
        Assert.Equal("mine", fs.ContentOf("/home/alice/a.txt"));
        await Assert.ThrowsAsync<HackFileNotFoundException>(() =>
            HackFile.WriteAllTextAsync("missing.txt", "x", new HackWriteOptions { ExpectedRevision = 1 }));
    }

    [Fact]
    public async Task Reading_a_missing_file_or_a_directory_throws_like_System_IO()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithDirectory("/home/alice/dir");

        await Assert.ThrowsAsync<HackFileNotFoundException>(() => HackFile.ReadAllTextAsync("nope.txt"));
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(() => HackFile.ReadAllTextAsync("dir"));
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(() => HackFile.WriteAllTextAsync("dir", "x"));
        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackFile.WriteAllTextAsync("no/such/dir.txt", "x"));
    }

    [Fact]
    public async Task Exists_is_true_only_for_files_and_never_throws_for_bad_paths()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt").WithDirectory("/home/alice/dir");

        Assert.True(await HackFile.ExistsAsync("a.txt"));
        Assert.False(await HackFile.ExistsAsync("dir"));
        Assert.False(await HackFile.ExistsAsync("missing"));
        Assert.False(await HackFile.ExistsAsync(""));
        Assert.False(await HackFile.ExistsAsync("/../.."));
    }

    [Fact]
    public async Task Copy_creates_refuses_to_clobber_and_overwrites_on_request()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "A").WithFile("/home/alice/b.txt", "B");

        await HackFile.CopyAsync("a.txt", "c.txt");
        await Assert.ThrowsAsync<HackIOException>(() => HackFile.CopyAsync("a.txt", "b.txt"));
        await HackFile.CopyAsync("a.txt", "b.txt", overwrite: true);

        Assert.Equal("A", fs.ContentOf("/home/alice/c.txt"));
        Assert.Equal("A", fs.ContentOf("/home/alice/b.txt"));
        await Assert.ThrowsAsync<HackFileNotFoundException>(() => HackFile.CopyAsync("missing", "x"));
    }

    [Fact]
    public async Task Copy_and_move_across_mounts_stream_the_bytes()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithFile("/home/alice/a.txt", "A").WithDirectory("/mnt/usb");
        ScriptedFileSystemGateway gateway = new(fs)
        {
            OnCopy = r => ScriptedFileSystemGateway.Error(FileSystemOperation.Copy, FileSystemErrorCode.CrossMountOperation, r.SourcePath.Value),
            OnMove = r => ScriptedFileSystemGateway.Error(FileSystemOperation.Move, FileSystemErrorCode.CrossMountOperation, r.SourcePath.Value)
        };
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        await HackFile.CopyAsync("a.txt", "/mnt/usb/copy.txt");
        await HackFile.MoveAsync("a.txt", "/mnt/usb/moved.txt");

        Assert.Equal("A", fs.ContentOf("/mnt/usb/copy.txt"));
        Assert.Equal("A", fs.ContentOf("/mnt/usb/moved.txt"));
        Assert.False(fs.Exists("/home/alice/a.txt"));
    }

    [Fact]
    public async Task Move_renames_and_honors_overwrite()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt", "A").WithFile("/home/alice/b.txt", "B");

        await Assert.ThrowsAsync<HackIOException>(() => HackFile.MoveAsync("a.txt", "b.txt"));
        await HackFile.MoveAsync("a.txt", "b.txt", overwrite: true);
        await HackFile.MoveAsync("b.txt", "b.txt");

        Assert.False(fs.Exists("/home/alice/a.txt"));
        Assert.Equal("A", fs.ContentOf("/home/alice/b.txt"));
    }

    [Fact]
    public async Task Delete_ignores_a_missing_file_but_not_a_missing_directory()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/a.txt").WithDirectory("/home/alice/dir");

        await HackFile.DeleteAsync("a.txt");
        await HackFile.DeleteAsync("a.txt");

        Assert.False(fs.Exists("/home/alice/a.txt"));
        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackFile.DeleteAsync("nope/a.txt"));
        await Assert.ThrowsAsync<HackUnauthorizedAccessException>(() => HackFile.DeleteAsync("dir"));
    }

    [Fact]
    public async Task SetUnixFileMode_changes_the_bits()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/run.sh");

        await HackFile.SetUnixFileModeAsync("run.sh", (UnixFileMode)0b111_101_101);

        Assert.Equal((UnixFileMode)0b111_101_101, await HackFile.GetUnixFileModeAsync("run.sh"));
        await Assert.ThrowsAsync<ArgumentException>(() => HackFile.SetUnixFileModeAsync("run.sh", UnixFileMode.StickyBit));
    }

    [Fact]
    public async Task Write_stream_publishes_once_on_dispose()
    {
        FakeAppFileSystemGateway fs = new FakeAppFileSystemGateway().WithDirectory(HackTestHost.Home);
        ScriptedFileSystemGateway gateway = new(fs);
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        await using (HackFileStream stream = await HackFile.OpenWriteAsync("out.txt"))
        await using (StreamWriter writer = new(stream))
        {
            await writer.WriteAsync("line 1\n");
            await writer.WriteAsync("line 2\n");
            Assert.Equal(0, gateway.WriteCalls);
        }

        Assert.Equal(1, gateway.WriteCalls);
        Assert.Equal("line 1\nline 2\n", fs.ContentOf("/home/alice/out.txt"));
    }

    [Fact]
    public async Task Write_stream_charges_its_buffer_to_the_process_until_it_is_released()
    {
        // ADR 0068: the in-memory buffer of a write stream is OS-held memory of the process (VmData).
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        RecordingKernelMemoryLedger ledger = new();
        ProcessId pid = ProcessId.FromInt64(7);
        fs.StreamBufferCharges = () => ledger.Charge(pid, KernelMemoryKind.StreamBuffer);

        await using (HackFileStream read = await HackFile.OpenWriteAsync("seed.txt"))
        {
        }

        Assert.Equal(1, ledger.ChargesOpened);
        await using (HackFileStream stream = await HackFile.OpenWriteAsync("big.bin"))
        {
            await stream.WriteAsync(new byte[10_000]);
            Assert.True(ledger.BytesOf(7, KernelMemoryKind.StreamBuffer) >= 10_000);
        }

        Assert.Equal(0, ledger.BytesOf(7, KernelMemoryKind.StreamBuffer));

        await using (HackFileStream reader = await HackFile.OpenReadAsync("big.bin"))
        {
            Assert.Equal(2, ledger.ChargesOpened); // a read stream holds no OS buffer
        }
    }

    [Fact]
    public async Task Write_stream_replaces_append_stream_appends_and_read_stream_reads()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/f.txt", "old content");

        await using (HackFileStream stream = await HackFile.OpenWriteAsync("f.txt"))
        {
            await stream.WriteAsync("new"u8.ToArray());
        }

        await using (HackFileStream stream = await HackFile.OpenAppendAsync("f.txt"))
        {
            await stream.WriteAsync("+1"u8.ToArray());
            await stream.FlushAsync();
            await stream.WriteAsync("+2"u8.ToArray());
            Assert.False(stream.CanSeek);
        }

        await using HackFileStream read = await HackFile.OpenReadAsync("f.txt");
        using StreamReader reader = new(read);
        Assert.Equal("new+1+2", await reader.ReadToEndAsync());
        Assert.False(read.CanWrite);
    }

    [Fact]
    public async Task Opening_an_empty_write_stream_still_truncates_and_sync_dispose_of_unpublished_bytes_throws()
    {
        using IDisposable scope = HackTestHost.Enter(out FakeAppFileSystemGateway fs);
        fs.WithFile("/home/alice/f.txt", "old");

        await using (await HackFile.OpenWriteAsync("f.txt"))
        {
        }

        Assert.Equal(string.Empty, fs.ContentOf("/home/alice/f.txt"));

        HackFileStream dirty = await HackFile.OpenWriteAsync("g.txt");
        dirty.WriteByte(1);
        Assert.Throws<InvalidOperationException>(dirty.Dispose);
        await dirty.DisposeAsync();
        Assert.True(fs.Exists("/home/alice/g.txt"));
    }

    [Fact]
    public async Task A_stream_publishes_through_the_opening_app_even_when_disposed_elsewhere()
    {
        HackFileStream stream;
        FakeAppFileSystemGateway fs;
        using (HackTestHost.Enter(out fs))
        {
            stream = await HackFile.OpenWriteAsync("late.txt");
            stream.WriteByte((byte)'x');
        }

        await stream.DisposeAsync();

        Assert.Equal("x", fs.ContentOf("/home/alice/late.txt"));
    }
}
