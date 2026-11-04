using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor.ContextMenus;
using Microsoft.AspNetCore.Components;

namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Base class for a child component of a Kazo OS window app. It receives the window's
/// <see cref="IAppExecutionContext"/> through a cascading value (set by the window host) and runs its
/// lifecycle methods, event handlers and after-render work inside that app's
/// <see cref="HackExecutionContext"/> scope, so it can call the <c>Hack*</c> file API directly
/// (ADR 0043, section 2).
/// </summary>
/// <remarks>
/// A plain <see cref="ComponentBase"/> child still works for rendering, but its <c>@onclick</c>
/// handlers run outside any ambient scope; derive from this class whenever a child component touches
/// files. Outside a window (in a test, for instance) with no cascading context, the component simply
/// keeps whatever scope is already ambient.
/// </remarks>
public abstract class HackComponentBase : ComponentBase, IHandleEvent, IHandleAfterRender
{
    private bool _hasCalledOnAfterRender;
    private HackMenus? _menus;

    /// <summary>Gets the execution context of the window app this component belongs to.</summary>
    [CascadingParameter]
    protected IAppExecutionContext? AppContext { get; set; }

    /// <summary>Gets or sets the window's context menus, cascaded by the OS (ADR 0150 §4).</summary>
    [CascadingParameter]
    private IHackMenuHost? MenuHost { get; set; }

    /// <summary>
    /// Gets the window's context menus (ADR 0150 §4): <c>@attributes="Menus.Attach(name, item)"</c>
    /// on any element, <c>Menus.Define(name, builder =&gt; …)</c> in C#. Callbacks defined here run
    /// in this component's app context and re-render it.
    /// </summary>
    protected HackMenus Menus => _menus is { } menus && menus.Host == MenuHost
        ? menus
        : _menus = new HackMenus(MenuHost, this);

    /// <summary>Assigns parameters and runs the synchronous lifecycle inside the app's ambient scope.</summary>
    public override Task SetParametersAsync(ParameterView parameters)
    {
        IAppExecutionContext? context = parameters.TryGetValue(nameof(AppContext), out IAppExecutionContext? cascaded)
            ? cascaded
            : AppContext;
        using IDisposable? scope = AmbientComponentDispatch.Enter(context);
        return base.SetParametersAsync(parameters);
    }

    /// <inheritdoc />
    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) =>
        AmbientComponentDispatch.HandleEventAsync(AppContext, callback, arg, StateHasChanged);

    /// <inheritdoc />
    Task IHandleAfterRender.OnAfterRenderAsync()
    {
        bool firstRender = !_hasCalledOnAfterRender;
        _hasCalledOnAfterRender = true;

        using IDisposable? scope = AmbientComponentDispatch.Enter(AppContext);
        OnAfterRender(firstRender);
        return OnAfterRenderAsync(firstRender);
    }
}
