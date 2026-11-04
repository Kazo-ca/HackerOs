using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>Covers every row of the ADR 0043 section 5 translation table through the public API.</summary>
public sealed class HackErrorTranslationTests
{
    public static TheoryData<FileSystemErrorCode, Type> ReadErrors => new()
    {
        { FileSystemErrorCode.NotFound, typeof(HackFileNotFoundException) },
        { FileSystemErrorCode.AlreadyExists, typeof(HackIOException) },
        { FileSystemErrorCode.DirectoryNotEmpty, typeof(HackIOException) },
        { FileSystemErrorCode.NotDirectory, typeof(HackIOException) },
        { FileSystemErrorCode.SymbolicLinkLoop, typeof(HackIOException) },
        { FileSystemErrorCode.SymbolicLinkLimitExceeded, typeof(HackIOException) },
        { FileSystemErrorCode.InvalidContent, typeof(HackIOException) },
        { FileSystemErrorCode.ProviderUnavailable, typeof(HackIOException) },
        { FileSystemErrorCode.ProviderFailure, typeof(HackIOException) },
        { FileSystemErrorCode.CrossMountOperation, typeof(HackIOException) },
        { FileSystemErrorCode.NoSpace, typeof(HackIOException) },
        { FileSystemErrorCode.FileTooLarge, typeof(HackIOException) },
        { FileSystemErrorCode.NotFile, typeof(HackUnauthorizedAccessException) },
        { FileSystemErrorCode.PermissionDenied, typeof(HackUnauthorizedAccessException) },
        { FileSystemErrorCode.CapabilityDenied, typeof(HackUnauthorizedAccessException) },
        { FileSystemErrorCode.AuthorityDenied, typeof(HackUnauthorizedAccessException) },
        { FileSystemErrorCode.ProtectedEntry, typeof(HackUnauthorizedAccessException) },
        { FileSystemErrorCode.RevisionConflict, typeof(HackFileConflictException) },
        { FileSystemErrorCode.InvalidPath, typeof(ArgumentException) },
        { FileSystemErrorCode.InvalidName, typeof(ArgumentException) },
        { FileSystemErrorCode.RootContainmentViolation, typeof(ArgumentException) },
        { FileSystemErrorCode.UnsupportedOperation, typeof(NotSupportedException) },
        { FileSystemErrorCode.Cancelled, typeof(OperationCanceledException) },
    };

    [Theory]
    [MemberData(nameof(ReadErrors))]
    public async Task Each_engine_code_maps_to_its_exception(FileSystemErrorCode code, Type expected)
    {
        ScriptedFileSystemGateway gateway = new(new FakeAppFileSystemGateway())
        {
            OnRead = request => new FileSystemError(FileSystemOperation.Read, code, request.Path, requiredCapability: "fs.read")
        };
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        Exception exception = await Record.ExceptionAsync(() => HackFile.ReadAllBytesAsync("/data/file.bin"));

        Assert.IsType(expected, exception);
        if (exception is HackIOException io)
        {
            Assert.Equal(code, io.ErrorCode);
            Assert.Equal("/data/file.bin", io.Path);
        }

        if (exception is HackUnauthorizedAccessException denied)
        {
            Assert.Equal("fs.read", denied.RequiredCapability);
            Assert.Equal("/data/file.bin", denied.Path);
        }
    }

    [Fact]
    public async Task NotFound_on_a_directory_call_or_a_create_is_a_missing_directory()
    {
        ScriptedFileSystemGateway gateway = new(new FakeAppFileSystemGateway().WithDirectory("/data"))
        {
            OnEnumerate = r => new FileSystemError(FileSystemOperation.Enumerate, FileSystemErrorCode.NotFound, r.Path),
            OnCreate = r => new FileSystemError(FileSystemOperation.Create, FileSystemErrorCode.NotFound, r.Path)
        };
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackDirectory.GetFilesAsync("/data"));
        await Assert.ThrowsAsync<HackDirectoryNotFoundException>(() => HackFile.WriteAllTextAsync("/data/new.txt", "x"));
    }

    [Fact]
    public async Task Hack_exceptions_are_caught_by_ordinary_System_IO_handlers()
    {
        using IDisposable scope = HackTestHost.Enter(out _);

        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => Task.FromException(new FileNotFoundException()));
        Exception missing = await Record.ExceptionAsync(() => HackFile.ReadAllTextAsync("missing"));

        Assert.IsAssignableFrom<IOException>(missing);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(new HackUnauthorizedAccessException("x"));
    }

    [Theory]
    [InlineData(FileSystemErrorCode.NoSpace, "No space left on device")]
    [InlineData(FileSystemErrorCode.FileTooLarge, "File too large")]
    public async Task Storage_limits_reach_the_shell_as_their_linux_message(FileSystemErrorCode code, string message)
    {
        ScriptedFileSystemGateway gateway = new(new FakeAppFileSystemGateway())
        {
            OnRead = request => new FileSystemError(FileSystemOperation.Read, code, request.Path)
        };
        using IDisposable scope = HackExecutionContext.Enter(HackTestHost.CreateContext(gateway));

        Exception exception = await Record.ExceptionAsync(() => HackFile.ReadAllBytesAsync("/data/file.bin"));

        Assert.Equal(message, ShellErrorText.Describe(exception));
    }
}
