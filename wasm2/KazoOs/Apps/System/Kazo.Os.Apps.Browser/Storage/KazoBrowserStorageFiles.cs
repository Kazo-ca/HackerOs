using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.Browser.Storage;

/// <summary>The browser's storage files in the OS file system, through the SDK (<see cref="KazoFile"/>).</summary>
public sealed class KazoBrowserStorageFiles : IBrowserStorageFiles
{
    /// <inheritdoc />
    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await KazoFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KazoFileNotFoundException or KazoDirectoryNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (KazoPath.GetDirectoryName(path) is { } directory)
        {
            // Creates the missing parents too, like Directory.CreateDirectory.
            await KazoDirectory.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        await KazoFile.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
    }
}
