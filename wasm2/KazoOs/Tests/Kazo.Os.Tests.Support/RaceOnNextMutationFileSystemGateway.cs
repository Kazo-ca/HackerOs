using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Tests.Support;

/// <summary>
/// Decorates a <see cref="FakeAppFileSystemGateway"/> so the first mutating call bumps
/// <paramref name="racePath"/>'s revision immediately beforehand, simulating another actor
/// committing a concurrent change between a command's <c>StatAsync</c> and its subsequent
/// mutation call (a TOCTOU race), so revision-conflict handling can be tested without timing
/// tricks.
/// </summary>
public sealed class RaceOnNextMutationFileSystemGateway(
    FakeAppFileSystemGateway inner, string racePath) : IAppFileSystemGateway
{
    private bool _raced;

    public ValueTask<FileSystemResult<FileSystemEntrySnapshot>> StatAsync(
        FileSystemStatRequest request, CancellationToken cancellationToken = default) =>
        inner.StatAsync(request, cancellationToken);

    public ValueTask<FileSystemResult<FileSystemContentReadHandle>> ReadAsync(
        FileSystemReadRequest request, CancellationToken cancellationToken = default) =>
        inner.ReadAsync(request, cancellationToken);

    public ValueTask<FileSystemResult<FileSystemDirectorySnapshot>> EnumerateAsync(
        FileSystemEnumerateRequest request, CancellationToken cancellationToken = default) =>
        inner.EnumerateAsync(request, cancellationToken);

    public ValueTask<FileSystemMutationResult> CreateAsync(
        FileSystemCreateRequest request, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.CreateAsync(request, cancellationToken);
    }

    public ValueTask<FileSystemMutationResult> WriteAsync(
        FileSystemWriteRequest request, IFileSystemContentSource content, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.WriteAsync(request, content, cancellationToken);
    }

    public ValueTask<FileSystemMutationResult> MoveAsync(
        FileSystemMoveRequest request, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.MoveAsync(request, cancellationToken);
    }

    public ValueTask<FileSystemMutationResult> CopyAsync(
        FileSystemCopyRequest request, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.CopyAsync(request, cancellationToken);
    }

    public ValueTask<FileSystemMutationResult> DeleteAsync(
        FileSystemDeleteRequest request, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.DeleteAsync(request, cancellationToken);
    }

    public ValueTask<FileSystemMutationResult> SetPermissionsAsync(
        FileSystemSetPermissionsRequest request, CancellationToken cancellationToken = default)
    {
        Race();
        return inner.SetPermissionsAsync(request, cancellationToken);
    }

    public IAppFileSystemGateway WithSelectedHandle(FileSystemSelectedResourceHandle handle) => this;

    private void Race()
    {
        if (_raced)
        {
            return;
        }
        _raced = true;
        inner.SimulateConcurrentChange(racePath);
    }
}
