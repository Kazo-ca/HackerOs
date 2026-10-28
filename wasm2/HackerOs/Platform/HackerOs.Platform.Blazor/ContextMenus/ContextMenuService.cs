using System.Runtime.CompilerServices;
using HackerOs.AppSdk;
using HackerOs.AppSdk.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;

namespace HackerOs.Platform.Blazor.ContextMenus;

/// <summary>How the user asked for a menu (ADR 0150 §5); decides where focus starts and how it is drawn.</summary>
public enum ContextMenuOrigin
{
    /// <summary>A right click.</summary>
    Pointer,

    /// <summary>A long press of a finger or a pen.</summary>
    Touch,

    /// <summary>The Menu key or Shift+F10 on the focused element: the first entry takes the highlight.</summary>
    Keyboard,

    /// <summary>An app opened it from code (<c>Menus.OpenAsync</c>).</summary>
    Code
}

/// <summary>The one menu open in the whole OS (ADR 0150 §6): what to draw, where, and for whom.</summary>
/// <param name="Scope">The window whose menu this is.</param>
/// <param name="Name">The menu's name.</param>
/// <param name="Item">The object attached to the element, handed to the entry's action.</param>
/// <param name="Label">The accessible name, or <see langword="null"/>.</param>
/// <param name="Root">The arranged menu.</param>
/// <param name="ClientX">Where it opens, in CSS pixels from the viewport's left.</param>
/// <param name="ClientY">Where it opens, in CSS pixels from the viewport's top.</param>
/// <param name="Origin">How it was asked for.</param>
public sealed record ContextMenuSession(
    ContextMenuScope Scope,
    string Name,
    object? Item,
    string? Label,
    ContextMenuLevel Root,
    double ClientX,
    double ClientY,
    ContextMenuOrigin Origin);

/// <summary>
/// The OS context menu service (contract <c>hackeros.context-menu</c>, ADR 0150): the menus each
/// window registered, the objects attached to its elements, and the single menu open at a time,
/// which <see cref="ContextMenuOverlay"/> draws above every window.
/// </summary>
/// <remarks>
/// Like a real window system's menu manager, it never runs app code to decide what is drawn except
/// the app's own menu builder and <c>Opening</c> handler, and an entry's action runs in the app's
/// context (as an <c>@onclick</c> handler of the component that declared it), never in the shell's.
/// </remarks>
public sealed class ContextMenuService
{
    private readonly Dictionary<string, ContextMenuScope> _scopes = new(StringComparer.Ordinal);
    private readonly ILogger<ContextMenuService>? _logger;
    private readonly IFileMenuEntrySource? _fileMenuEntries;
    private int _nextScope;

    /// <summary>Initializes the service.</summary>
    /// <param name="logger">Writes failing menus to the system log; optional.</param>
    /// <param name="fileMenuEntries">
    /// Adds the entries apps registered to the file menus (ADR 0151, ADR 0159 §5); <see langword="null"/>
    /// draws file menus with the provider's entries only.
    /// </param>
    public ContextMenuService(ILogger<ContextMenuService>? logger = null, IFileMenuEntrySource? fileMenuEntries = null)
    {
        _logger = logger;
        _fileMenuEntries = fileMenuEntries;
    }

    /// <summary>Raised when a menu opens or closes.</summary>
    public event Action? Changed;

    /// <summary>Gets the open menu, or <see langword="null"/>.</summary>
    public ContextMenuSession? Current { get; private set; }

    /// <summary>
    /// Gets the entry under the pointer or the keyboard focus in the open menu, or
    /// <see langword="null"/> (ADR 0179 §6: the screen identification veil follows it).
    /// </summary>
    internal HackMenuItemDefinition? Highlighted { get; private set; }

    /// <summary>Raised when <see cref="Highlighted"/> changed.</summary>
    internal event Action? HighlightChanged;

    /// <summary>Records the entry the overlay shows under the pointer or the keyboard focus.</summary>
    /// <param name="item">The entry, or <see langword="null"/> when the pointer or focus left it.</param>
    internal void SetHighlighted(HackMenuItemDefinition? item)
    {
        if (Current is null)
        {
            item = null;
        }

        if (!ReferenceEquals(item, Highlighted))
        {
            Highlighted = item;
            HighlightChanged?.Invoke();
        }
    }

    /// <summary>
    /// Creates the menus of one window (or of an OS area such as the desktop); disposing it removes
    /// its menus and attached objects, and closes its open menu.
    /// </summary>
    /// <param name="context">The app whose actions the menus run, or <see langword="null"/> for the shell.</param>
    public ContextMenuScope CreateScope(IAppExecutionContext? context)
    {
        string id = $"m{++_nextScope}";
        ContextMenuScope scope = new(this, id, context);
        _scopes.Add(id, scope);
        return scope;
    }

    /// <summary>Opens the menu attached under <paramref name="token"/> (the OS script's request).</summary>
    /// <param name="token">The token of the element's attributes.</param>
    /// <param name="clientX">Horizontal position.</param>
    /// <param name="clientY">Vertical position.</param>
    /// <param name="origin">How the user asked.</param>
    /// <returns>Whether a menu opened.</returns>
    public Task<bool> OpenFromTokenAsync(string token, double clientX, double clientY, ContextMenuOrigin origin)
    {
        if (TryResolve(token, out ContextMenuScope? scope, out string? name, out object? item))
        {
            return OpenAsync(scope, name, item, clientX, clientY, origin);
        }

        // An element removed since (its token released): nothing to open.
        return Task.FromResult(false);
    }

    /// <summary>
    /// Builds, adjusts and arranges a window's menu, then makes it the open one, closing any other
    /// (one menu at a time in the whole OS).
    /// </summary>
    /// <param name="scope">The window.</param>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The attached object.</param>
    /// <param name="clientX">Horizontal position.</param>
    /// <param name="clientY">Vertical position.</param>
    /// <param name="origin">How it was asked for.</param>
    /// <returns>Whether a menu opened: a known name, not cancelled, with a visible entry.</returns>
    public async Task<bool> OpenAsync(
        ContextMenuScope scope,
        string name,
        object? item,
        double clientX,
        double clientY,
        ContextMenuOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (scope.IsDisposed || !scope.TryGetRegistration(name, out HackMenuRegistration? registration))
        {
            _logger?.LogWarning("context-menu: no menu named '{Name}' is registered in its window.", name);
            return false;
        }

        HackMenuDefinition menu;
        try
        {
            // A fresh copy for each opening: Opening changes last for this opening only.
            menu = registration.Build().Clone();
            menu.Validate();
            if (registration.Opening.HasDelegate)
            {
                HackMenuOpeningEventArgs args = new(menu, item);
                using (scope.EnterAmbient())
                {
                    await registration.Opening.InvokeAsync(args);
                }

                if (args.Cancel)
                {
                    return false;
                }
            }

            // ADR 0159 §5: a file menu gets the entries apps registered, whichever app provides the
            // control, after the provider's and the host's own adjustments.
            if (menu.FileMenu is not null && _fileMenuEntries is not null)
            {
                await AddFileMenuEntriesAsync(menu, name);
            }

            menu.Validate();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A broken app menu must not take the shell down: the fault goes to the system log.
            _logger?.LogError(exception, "context-menu: the menu '{Name}' could not be built.", name);
            return false;
        }

        if (scope.IsDisposed || ContextMenuLayout.Arrange(menu.Groups) is not { } root)
        {
            return false;
        }

        SetHighlighted(null);
        Current = new ContextMenuSession(scope, name, item, menu.Label, root, clientX, clientY, origin);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// Adds the registered file menu entries; a failure is written to the system log and the menu
    /// opens with the provider's entries only, so one broken registration never hides a file menu.
    /// </summary>
    private async Task AddFileMenuEntriesAsync(HackMenuDefinition menu, string name)
    {
        try
        {
            await _fileMenuEntries!.ApplyAsync(menu);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogError(exception, "context-menu: the registered entries of the file menu '{Name}' could not be added.", name);
        }
    }

    /// <summary>Closes the open menu, if any.</summary>
    public void Close()
    {
        if (Current is null)
        {
            return;
        }

        Current = null;
        SetHighlighted(null);
        Changed?.Invoke();
    }

    /// <summary>
    /// Closes the menu and runs the chosen entry's action with the attached object, in the app's
    /// context. An action that throws is written to the system log.
    /// </summary>
    /// <param name="item">The chosen entry.</param>
    public async Task InvokeAsync(HackMenuItemDefinition item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (Current is not { } session || item.IsDisabled || item.Children.Count > 0)
        {
            return;
        }

        Close();
        if (!item.OnClick.HasDelegate)
        {
            return;
        }

        try
        {
            using (session.Scope.EnterAmbient())
            {
                await item.OnClick.InvokeAsync(session.Item);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.LogError(exception, "context-menu: the entry '{Id}' of the menu '{Name}' failed.", item.Id, session.Name);
        }
    }

    /// <summary>Forgets attached objects whose elements left the page (reported by the OS script).</summary>
    /// <param name="tokens">The released tokens.</param>
    public void Release(IEnumerable<string> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        foreach (string token in tokens)
        {
            if (ScopeOf(token) is { } scope)
            {
                scope.Release(token);
            }
        }
    }

    /// <summary>Finds the window, menu and object a token names.</summary>
    /// <param name="token">The token of an element's attributes.</param>
    /// <param name="scope">The window.</param>
    /// <param name="name">The menu's name.</param>
    /// <param name="item">The attached object.</param>
    /// <returns>Whether the token is still known.</returns>
    public bool TryResolve(
        string token,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ContextMenuScope? scope,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name,
        out object? item)
    {
        scope = ScopeOf(token);
        if (scope is not null && scope.TryResolve(token, out name, out item))
        {
            return true;
        }

        scope = null;
        name = null;
        item = null;
        return false;
    }

    /// <summary>Called by a disposed scope.</summary>
    internal void Remove(ContextMenuScope scope)
    {
        _scopes.Remove(scope.Id);
        if (Current?.Scope == scope)
        {
            Close();
        }
    }

    private ContextMenuScope? ScopeOf(string token)
    {
        int dash = token.IndexOf('-', StringComparison.Ordinal);
        return dash > 0 && _scopes.TryGetValue(token[..dash], out ContextMenuScope? scope) ? scope : null;
    }
}

/// <summary>
/// The context menus of one window (ADR 0150 §4), given in cascade to its components as
/// <see cref="IHackMenuHost"/>: its registered menus by name, and the objects attached to its
/// elements by token. The objects never leave C#; the page only sees the opaque token.
/// </summary>
public sealed class ContextMenuScope : IHackMenuHost, IDisposable
{
    private readonly ContextMenuService _service;
    private readonly IAppExecutionContext? _context;
    private readonly Dictionary<string, HackMenuRegistration> _menus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, object? Item)> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<AttachKey, string> _byItem = [];
    private int _nextToken;

    internal ContextMenuScope(ContextMenuService service, string id, IAppExecutionContext? context)
    {
        _service = service;
        Id = id;
        _context = context;
    }

    /// <summary>Gets the scope's id, the prefix of its tokens.</summary>
    public string Id { get; }

    /// <summary>Gets whether the window closed.</summary>
    public bool IsDisposed { get; private set; }

    /// <summary>Gets the number of attached objects held (for tests and diagnostics).</summary>
    public int AttachedCount => _byToken.Count;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, object> Attach(string name, object? item)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        AttachKey key = new(name, item);
        if (!_byItem.TryGetValue(key, out string? token))
        {
            token = $"{Id}-{++_nextToken}";
            _byItem.Add(key, token);
            _byToken.Add(token, (name, item));
        }

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [HackMenuAttributes.Menu] = name,
            [HackMenuAttributes.Token] = token,
        };
    }

    /// <inheritdoc />
    public IDisposable Register(HackMenuRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Name);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (!_menus.TryAdd(registration.Name, registration))
        {
            throw new InvalidOperationException(
                $"A context menu named '{registration.Name}' is already defined in this window.");
        }

        return new Registration(this, registration);
    }

    /// <inheritdoc />
    public Task<bool> OpenAsync(string name, object? item, double clientX, double clientY) =>
        _service.OpenAsync(this, name, item, clientX, clientY, ContextMenuOrigin.Code);

    /// <inheritdoc />
    public void Close()
    {
        if (_service.Current?.Scope == this)
        {
            _service.Close();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _menus.Clear();
        _byToken.Clear();
        _byItem.Clear();
        _service.Remove(this);
    }

    internal bool TryGetRegistration(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out HackMenuRegistration? registration) =>
        _menus.TryGetValue(name, out registration);

    internal bool TryResolve(string token, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? name, out object? item)
    {
        if (_byToken.TryGetValue(token, out (string Name, object? Item) attached))
        {
            (name, item) = attached;
            return true;
        }

        name = null;
        item = null;
        return false;
    }

    internal void Release(string token)
    {
        if (_byToken.Remove(token, out (string Name, object? Item) attached))
        {
            _byItem.Remove(new AttachKey(attached.Name, attached.Item));
        }
    }

    /// <summary>Runs the app's code (its builder, Opening handler, action) in the app's ambient scope.</summary>
    internal IDisposable? EnterAmbient() => _context is null ? null : HackExecutionContext.Enter(_context);

    private void Unregister(HackMenuRegistration registration)
    {
        if (_menus.TryGetValue(registration.Name, out HackMenuRegistration? current) && ReferenceEquals(current, registration))
        {
            _menus.Remove(registration.Name);
        }
    }

    /// <summary>
    /// The same object attached again gets the same token: by reference for an object, by value for
    /// a string or a value type (boxed anew at each render).
    /// </summary>
    private readonly record struct AttachKey(string Name, object? Item)
    {
        public bool Equals(AttachKey other) =>
            string.Equals(Name, other.Name, StringComparison.Ordinal)
            && (Item is string || Item?.GetType().IsValueType == true
                ? Equals(Item, other.Item)
                : ReferenceEquals(Item, other.Item));

        public override int GetHashCode() => HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(Name),
            Item is null ? 0
            : Item is string || Item.GetType().IsValueType ? Item.GetHashCode()
            : RuntimeHelpers.GetHashCode(Item));
    }

    private sealed class Registration(ContextMenuScope scope, HackMenuRegistration registration) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                scope.Unregister(registration);
            }
        }
    }
}
