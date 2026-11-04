using Kazo.Os.AppSdk.IO;

namespace Kazo.Os.Apps.Browser.Storage;

/// <summary>The browser's storage files in the OS file system, through the SDK (<see cref="HackFile"/>).</summary>
public sealed class HackBrowserStorageFiles : IBrowserStorageFiles
{
    /// <inheritdoc />
    public async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            return await HackFile.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HackFileNotFoundException or HackDirectoryNotFoundException or FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
    {
        if (HackPath.GetDirectoryName(path) is { } directory)
        {
            // Creates the missing parents too, like Directory.CreateDirectory.
            await HackDirectory.CreateDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        await HackFile.WriteAllTextAsync(path, text, cancellationToken).ConfigureAwait(false);
    }
}
