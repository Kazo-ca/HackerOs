using HackerOs.Simulation.Abstractions.FileSystem;
using Microsoft.JSInterop;

namespace HackerOs.Infrastructure.Browser.Interop;

/// <summary>
/// Browser half of the network drives (ADR 0181): imports <c>hostFolder.js</c> of this library,
/// which opens <c>showDirectoryPicker</c>, keeps the chosen folders in the IndexedDB database
/// <c>hackeros-host-folders</c>, and reads and writes their files.
/// </summary>
/// <remarks>
/// The module answers errors as <c>hostfolder:&lt;kind&gt;:&lt;message&gt;</c>; they become
/// <see cref="HostFolderException"/>. A missing user gesture becomes an
/// <see cref="InvalidOperationException"/> and a browser without the dialog a
/// <see cref="NotSupportedException"/>, as <see cref="IHostFolderAccess.PickAsync"/> documents.
/// </remarks>
public sealed class BrowserHostFolderAccess : IHostFolderAccess, IAsyncDisposable
{
    internal const string ModulePath = "./_content/HackerOs.Infrastructure.Browser/hostFolder.js";

    private readonly IJSRuntime _runtime;
    private readonly SemaphoreSlim _moduleGate = new(1, 1);
    private IJSObjectReference? _module;

    /// <summary>Creates the access over the page's JavaScript runtime.</summary>
    public BrowserHostFolderAccess(IJSRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
    }

    /// <inheritdoc />
    public async ValueTask<bool> IsSupportedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
            return await module.InvokeAsync<bool>("isSupported", cancellationToken).ConfigureAwait(false);
        }
        catch (JSException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async ValueTask<HostFolderPick?> PickAsync(CancellationToken cancellationToken = default)
    {
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        PickedFolder? picked;
        try
        {
            picked = await module.InvokeAsync<PickedFolder?>("pick", cancellationToken).ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            HostFolderException translated = Translate(exception);
            if (translated.Message.Contains("notsupported", StringComparison.Ordinal))
            {
                throw new NotSupportedException("hostfs: not supported by this browser", exception);
            }

            if (translated.Message.Contains("user activation required", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("hostfs: user activation required", exception);
            }

            throw translated;
        }

        return picked is null ? null : new HostFolderPick(picked.Key!, new BrowserHostFolder(module, picked.Id, picked.Name));
    }

    /// <inheritdoc />
    public async ValueTask<IHostFolder?> LoadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        PickedFolder? loaded = await Call(() => module.InvokeAsync<PickedFolder?>("load", cancellationToken, key)).ConfigureAwait(false);
        return loaded is null ? null : new BrowserHostFolder(module, loaded.Id, loaded.Name);
    }

    /// <inheritdoc />
    public async ValueTask ForgetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        IJSObjectReference module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await Call(() => module.InvokeVoidAsync("forget", cancellationToken, key)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync().ConfigureAwait(false);
            }
            catch (JSDisconnectedException)
            {
                // The page is gone: nothing to release.
            }
        }

        _moduleGate.Dispose();
    }

    private async ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        if (_module is not null)
        {
            return _module;
        }

        await _moduleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _module ??= await _runtime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        }
        finally
        {
            _moduleGate.Release();
        }
    }

    internal static async ValueTask Call(Func<ValueTask> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw Translate(exception);
        }
    }

    internal static async ValueTask<T> Call<T>(Func<ValueTask<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (JSException exception)
        {
            throw Translate(exception);
        }
    }

    /// <summary>Reads the kind out of <c>hostfolder:&lt;kind&gt;:&lt;message&gt;</c>.</summary>
    internal static HostFolderException Translate(JSException exception)
    {
        string message = exception.Message;
        int start = message.IndexOf("hostfolder:", StringComparison.Ordinal);
        if (start >= 0)
        {
            string rest = message[(start + "hostfolder:".Length)..];
            int separator = rest.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && Enum.TryParse(rest[..separator], out HostFolderErrorKind kind))
            {
                string detail = rest[(separator + 1)..];
                int newline = detail.IndexOf('\n', StringComparison.Ordinal);
                return new HostFolderException(kind, newline >= 0 ? detail[..newline] : detail, exception);
            }
        }

        return new HostFolderException(HostFolderErrorKind.Failure, message, exception);
    }

    private sealed record PickedFolder(string? Key, int Id, string Name);
}

/// <summary>One open host folder: a number of <c>hostFolder.js</c> standing for its handle.</summary>
internal sealed class BrowserHostFolder(IJSObjectReference module, int id, string name) : IHostFolder, IObservableHostFolder
{
    /// <inheritdoc />
    public async ValueTask<IAsyncDisposable?> ObserveAsync(IReadOnlyList<string> path, Action changed, CancellationToken cancellationToken = default)
    {
        DotNetObjectReference<ObserverCallback> callback = DotNetObjectReference.Create(new ObserverCallback(changed));
        int observerId;
        try
        {
            observerId = await BrowserHostFolderAccess.Call(() => module.InvokeAsync<int>("observe", cancellationToken, id, path, callback)).ConfigureAwait(false);
        }
        catch
        {
            callback.Dispose();
            throw;
        }

        if (observerId == 0)
        {
            callback.Dispose();
            return null;
        }

        return new Observation(module, observerId, callback);
    }

    public string Name { get; } = name;

    public ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default) =>
        PermissionAsync("queryPermission", cancellationToken);

    public ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        PermissionAsync("requestPermission", cancellationToken);

    public async ValueTask<HostFolderEntry> StatAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        ToEntry(await BrowserHostFolderAccess.Call(() => module.InvokeAsync<WireEntry>("stat", cancellationToken, id, path)).ConfigureAwait(false));

    public async ValueTask<IReadOnlyList<HostFolderEntry>> ListAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default)
    {
        WireEntry[] entries = await BrowserHostFolderAccess.Call(() => module.InvokeAsync<WireEntry[]>("list", cancellationToken, id, path)).ConfigureAwait(false);
        return [.. entries.Select(ToEntry)];
    }

    public ValueTask<byte[]> ReadAsync(IReadOnlyList<string> path, CancellationToken cancellationToken = default) =>
        BrowserHostFolderAccess.Call(() => module.InvokeAsync<byte[]>("read", cancellationToken, id, path));

    public ValueTask CreateAsync(IReadOnlyList<string> path, HostFolderEntryKind kind, CancellationToken cancellationToken = default) =>
        BrowserHostFolderAccess.Call(() => module.InvokeVoidAsync(
            "create", cancellationToken, id, path, kind == HostFolderEntryKind.Directory ? "directory" : "file"));

    public ValueTask WriteAsync(IReadOnlyList<string> path, byte[] content, CancellationToken cancellationToken = default) =>
        BrowserHostFolderAccess.Call(() => module.InvokeVoidAsync("write", cancellationToken, id, path, content));

    public ValueTask DeleteAsync(IReadOnlyList<string> path, bool recursive, CancellationToken cancellationToken = default) =>
        BrowserHostFolderAccess.Call(() => module.InvokeVoidAsync("remove", cancellationToken, id, path, recursive));

    public ValueTask<bool> TryMoveAsync(IReadOnlyList<string> source, IReadOnlyList<string> destination, CancellationToken cancellationToken = default) =>
        BrowserHostFolderAccess.Call(() => module.InvokeAsync<bool>("move", cancellationToken, id, source, destination));

    public async ValueTask DisposeAsync()
    {
        try
        {
            await module.InvokeVoidAsync("release", id).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JSException or JSDisconnectedException or ObjectDisposedException)
        {
            // The page or the module is gone: the handle went with it.
        }
    }

    private async ValueTask<HostFolderPermission> PermissionAsync(string function, CancellationToken cancellationToken)
    {
        string state = await BrowserHostFolderAccess.Call(() => module.InvokeAsync<string>(function, cancellationToken, id)).ConfigureAwait(false);
        return state switch
        {
            "granted" => HostFolderPermission.Granted,
            "denied" => HostFolderPermission.Denied,
            _ => HostFolderPermission.Prompt
        };
    }

    private static HostFolderEntry ToEntry(WireEntry entry) => new(
        entry.Name,
        entry.Kind == "directory" ? HostFolderEntryKind.Directory : HostFolderEntryKind.File,
        entry.Size,
        entry.LastModified is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)milliseconds) : null);

    private sealed record WireEntry(string Name, string Kind, long Size, double? LastModified);

    /// <summary>Receives the browser observer's calls.</summary>
    internal sealed class ObserverCallback(Action changed)
    {
        [JSInvokable]
        public void OnChanged() => changed();
    }

    /// <summary>One folder observed by the browser; disposing it stops the observer.</summary>
    private sealed class Observation(IJSObjectReference module, int observerId, DotNetObjectReference<ObserverCallback> callback) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await module.InvokeVoidAsync("unobserve", observerId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is JSException or JSDisconnectedException or ObjectDisposedException)
            {
                // The page or the module is gone: the observer went with it.
            }
            finally
            {
                callback.Dispose();
            }
        }
    }
}
