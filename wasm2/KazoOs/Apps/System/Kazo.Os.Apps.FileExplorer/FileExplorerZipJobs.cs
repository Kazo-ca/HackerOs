using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Threading;
using Kazo.Os.Apps.FileExplorer;

// ADR 0128 section 2: the archive tasks' inputs and results cross as JSON, through this context.
[assembly: KazoWireContext(typeof(FileExplorerJobsJsonContext))]

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>One selected item to put in an archive: its path, its name inside the archive, and whether it is a folder.</summary>
internal sealed record ZipSource(string Path, string EntryName, bool IsDirectory);

/// <summary>What the compress task archives, and where it writes the archive.</summary>
internal sealed record ZipCompressJobInput(List<ZipSource> Sources, string Destination);

/// <summary>Which archive the extract task opens, and the new folder it fills.</summary>
internal sealed record ZipExtractJobInput(string ZipPath, string DestinationDirectory);

/// <summary>Why an archive task failed: a text key of the explorer and its argument, localized by the window.</summary>
internal sealed record ZipFailure(string Key, string? Argument = null);

/// <summary>The serializer context of the explorer's background tasks (ADR 0128, ADR 0124 section 2).</summary>
[JsonSerializable(typeof(ZipCompressJobInput))]
[JsonSerializable(typeof(ZipExtractJobInput))]
[JsonSerializable(typeof(ZipFailure))]
internal sealed partial class FileExplorerJobsJsonContext : JsonSerializerContext;

/// <summary>
/// The explorer's archive tasks (ADR 0128): static methods the OS may run in a Web Worker. Each one
/// reads and writes through its own process's gateways: the explorer's user and capabilities.
/// </summary>
internal static class FileExplorerZipJobs
{
    /// <summary>Compresses the selection into a new archive.</summary>
    public static Task<ZipFailure?> Compress(ZipCompressJobInput input, KazoBackgroundContext context) =>
        FileExplorerZipService.CompressCoreAsync(
            KazoExecutionContext.Current.FileSystem, input.Sources, VirtualPath.Parse(input.Destination), context.CancellationToken);

    /// <summary>Extracts an archive into a new folder.</summary>
    public static Task<ZipFailure?> Extract(ZipExtractJobInput input, KazoBackgroundContext context) =>
        FileExplorerZipService.ExtractCoreAsync(
            KazoExecutionContext.Current.FileSystem, VirtualPath.Parse(input.ZipPath), VirtualPath.Parse(input.DestinationDirectory), context.CancellationToken);
}
