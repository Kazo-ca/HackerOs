using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.AppSdk.Tests.IO;

/// <summary>Covers every row of the ADR 0043 section 5 translation table through the public API.</summary>
public sealed class KazoErrorTranslationTests
{
    public static TheoryData<FileSystemErrorCode, Type> ReadErrors => new()
    {
        { FileSystemErrorCode.NotFound, typeof(KazoFileNotFoundException) },
        { FileSystemErrorCode.AlreadyExists, typeof(KazoIOException) },
        { FileSystemErrorCode.DirectoryNotEmpty, typeof(KazoIOException) },
        { FileSystemErrorCode.NotDirectory, typeof(KazoIOException) },
        { FileSystemErrorCode.SymbolicLinkLoop, typeof(KazoIOException) },
        { FileSystemErrorCode.SymbolicLinkLimitExceeded, typeof(KazoIOException) },
        { FileSystemErrorCode.InvalidContent, typeof(KazoIOException) },
        { FileSystemErrorCode.ProviderUnavailable, typeof(KazoIOException) },
        { FileSystemErrorCode.ProviderFailure, typeof(KazoIOException) },
        { FileSystemErrorCode.CrossMountOperation, typeof(KazoIOException) },
        { FileSystemErrorCode.NoSpace, typeof(KazoIOException) },
        { FileSystemErrorCode.FileTooLarge, typeof(KazoIOException) },
        { FileSystemErrorCode.NotFile, typeof(KazoUnauthorizedAccessException) },
        { FileSystemErrorCode.PermissionDenied, typeof(KazoUnauthorizedAccessException) },
        { FileSystemErrorCode.CapabilityDenied, typeof(KazoUnauthorizedAccessException) },
        { FileSystemErrorCode.AuthorityDenied, typeof(KazoUnauthorizedAccessException) },
        { FileSystemErrorCode.ProtectedEntry, typeof(KazoUnauthorizedAccessException) },
        { FileSystemErrorCode.RevisionConflict, typeof(KazoFileConflictException) },
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
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(gateway));

        Exception exception = await Record.ExceptionAsync(() => KazoFile.ReadAllBytesAsync("/data/file.bin"));

        Assert.IsType(expected, exception);
        if (exception is KazoIOException io)
        {
            Assert.Equal(code, io.ErrorCode);
            Assert.Equal("/data/file.bin", io.Path);
        }

        if (exception is KazoUnauthorizedAccessException denied)
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
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(gateway));

        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoDirectory.GetFilesAsync("/data"));
        await Assert.ThrowsAsync<KazoDirectoryNotFoundException>(() => KazoFile.WriteAllTextAsync("/data/new.txt", "x"));
    }

    [Fact]
    public async Task Kazo_exceptions_are_caught_by_ordinary_System_IO_handlers()
    {
        using IDisposable scope = KazoTestHost.Enter(out _);

        await Assert.ThrowsAnyAsync<FileNotFoundException>(() => Task.FromException(new FileNotFoundException()));
        Exception missing = await Record.ExceptionAsync(() => KazoFile.ReadAllTextAsync("missing"));

        Assert.IsAssignableFrom<IOException>(missing);
        Assert.IsAssignableFrom<UnauthorizedAccessException>(new KazoUnauthorizedAccessException("x"));
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
        using IDisposable scope = KazoExecutionContext.Enter(KazoTestHost.CreateContext(gateway));

        Exception exception = await Record.ExceptionAsync(() => KazoFile.ReadAllBytesAsync("/data/file.bin"));

        Assert.Equal(message, ShellErrorText.Describe(exception));
    }
}
