using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net.Hosting;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.Network.Local;

/// <summary>
/// Source 4 of <c>kazodns</c> (ADR 0074, section 4; ADR 0080, section 4): the host names bound by the
/// hosted static sites. It reads the same <c>.site</c> files as <c>kazowebd</c>, as root, like a
/// resolver reading a zone file: <c>/etc/kazoweb/sites/*.site</c> and every user's
/// <c>~/.config/kazoweb/sites/*.site</c>.
/// </summary>
/// <remarks>
/// <para>
/// Only valid, enabled sites publish their names; <c>localhost</c>, wildcard hosts (<c>+</c>,
/// <c>*</c>) and IPv4 literals publish nothing. A name stays published while <c>kazowebd</c> is
/// stopped: the request then meets a closed port (connection refused), as on a machine whose
/// <c>/etc/hosts</c> still lists a site whose server is down.
/// </para>
/// <para>
/// The files are parsed again only when a directory listing changes (a file added, removed or
/// rewritten changes its revision), so a lookup costs a few directory reads. A system site wins over a
/// user site that binds the same name.
/// </para>
/// </remarks>
public sealed class HostedSiteNameSource : IHostedSiteNameSource
{
    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string _signature = "\0";
    private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes the source.</summary>
    /// <param name="fileSystem">The file system the site files are read from, as root.</param>
    /// <param name="clock">Simulated clock of the read operations.</param>
    public HostedSiteNameSource(IFileSystemService fileSystem, ISimulationClock clock)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <inheritdoc />
    public async ValueTask<string?> FindSiteAsync(string hostName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        IReadOnlyDictionary<string, string> names = await GetNamesAsync(cancellationToken).ConfigureAwait(false);
        return names.TryGetValue(hostName, out string? site) ? site : null;
    }

    /// <summary>Gets every published host name and the site that binds it.</summary>
    /// <param name="cancellationToken">Cancels the reads.</param>
    public async ValueTask<IReadOnlyDictionary<string, string>> GetNamesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IAppFileSystemGateway root = new AppFileSystemGateway(_fileSystem, _clock, KazoDnsResolver.RootOperation(), ["system"]);
            List<(string Directory, KazoWebSiteScope Scope, string? Owner, FileSystemDirectorySnapshot Listing)> directories = [];
            await AddListingAsync(root, KazoWebSiteFile.SystemSitesDirectory, KazoWebSiteScope.System, null, directories, cancellationToken).ConfigureAwait(false);
            FileSystemResult<FileSystemDirectorySnapshot> homes = await root
                .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse("/home")), cancellationToken)
                .ConfigureAwait(false);
            if (homes.Succeeded)
            {
                foreach (FileSystemDirectoryItem home in homes.Value!.Entries.Where(static item => item.Metadata.Kind == FileSystemEntryKind.Directory))
                {
                    string directory = KazoWebSiteFile.GetUserSitesDirectory($"/home/{home.Name.Value}");
                    await AddListingAsync(root, directory, KazoWebSiteScope.User, home.Name.Value, directories, cancellationToken).ConfigureAwait(false);
                }
            }

            string signature = string.Join('\n', directories.SelectMany(static listing =>
                listing.Listing.Entries.Select(item => $"{listing.Directory}/{item.Name.Value}@{item.Metadata.Revision}")));
            if (signature == _signature)
            {
                return _names;
            }

            Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach ((string directory, KazoWebSiteScope scope, string? owner, FileSystemDirectorySnapshot listing) in directories)
            {
                foreach (FileSystemDirectoryItem item in listing.Entries)
                {
                    if (item.Metadata.Kind != FileSystemEntryKind.File || !item.Name.Value.EndsWith(KazoWebSiteFile.Extension, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string path = $"{directory}/{item.Name.Value}";
                    SettingsFileReadResult read = await SettingsTextFile.ReadAsync(root, VirtualPath.Parse(path), cancellationToken).ConfigureAwait(false);
                    if (read.Content is null)
                    {
                        continue;
                    }

                    KazoWebSiteDeclaration declaration = KazoWebSiteFile.Parse(path, read.Content, scope, owner);
                    if (!declaration.IsServable)
                    {
                        continue;
                    }

                    foreach (string binding in declaration.Site!.Bindings)
                    {
                        if (KazoWebSiteFile.TryParseBinding(binding, out WebBinding parsed, out _)
                            && !parsed.IsWildcard
                            && !parsed.IsLoopbackHost
                            && !System.Net.IPAddress.TryParse(parsed.Host, out _))
                        {
                            names.TryAdd(parsed.Host, declaration.Name);
                        }
                    }
                }
            }

            _names = names;
            _signature = signature;
            return _names;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task AddListingAsync(
        IAppFileSystemGateway root,
        string directory,
        KazoWebSiteScope scope,
        string? owner,
        List<(string, KazoWebSiteScope, string?, FileSystemDirectorySnapshot)> listings,
        CancellationToken cancellationToken)
    {
        FileSystemResult<FileSystemDirectorySnapshot> listing = await root
            .EnumerateAsync(new FileSystemEnumerateRequest(VirtualPath.Parse(directory)), cancellationToken)
            .ConfigureAwait(false);
        if (listing.Succeeded)
        {
            listings.Add((directory, scope, owner, listing.Value!));
        }
    }
}
