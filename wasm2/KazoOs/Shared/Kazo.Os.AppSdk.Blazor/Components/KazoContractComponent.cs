using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.AppSdk.Blazor.Components;

/// <summary>
/// Base class of the SDK's typed host components (ADR 0148 §1): a component such as
/// <c>KazoFileView</c> that an app uses like any Blazor component, with ordinary parameters and
/// <see cref="EventCallback"/>s, while the visible implementation comes at run time from the
/// provider the OS registered for <see cref="Contract"/>. The SDK holds only the contract; the
/// provider (an OS app or the platform) holds the code, so a fix reaches every app without a rebuild.
/// </summary>
/// <typeparam name="TController">The contract's controller interface, handed back by the provider.</typeparam>
/// <remarks>
/// <para>
/// A derived host declares its parameters and copies them, by name, in
/// <see cref="AddProviderParameters"/>; the provider's component declares the same names as
/// <c>[Parameter]</c> properties (and ignores, through
/// <see cref="KazoEmbeddableComponent.UnmatchedParameters"/>, the ones it does not know yet).
/// It renders through <see cref="KazoEmbeddedComponent{TController}"/>, so resolution, lazy loading
/// and the unavailable state are those of ADR 0063.
/// </para>
/// <para>
/// The provider runs with the <b>host</b> app's rights (ADR 0148 §4): it is rendered inside the
/// host's window, receives the host's cascading <c>AppContext</c>, and the callbacks it invokes run
/// in this component's (the host's) ambient context.
/// </para>
/// </remarks>
public abstract class KazoContractComponent<TController> : KazoComponentBase
    where TController : class
{
    private IReadOnlyDictionary<string, object?>? _parameters;

    /// <summary>Gets the contract this host renders, e.g. <c>KazoComponentContracts.FileView</c>.</summary>
    protected abstract string Contract { get; }

    /// <summary>Gets the contract's options record passed to the provider, or <see langword="null"/>.</summary>
    protected virtual object? Options => null;

    /// <summary>
    /// Gets or sets the callback receiving the controller once the provider is ready (or
    /// <see langword="null"/> when it does not implement the contract).
    /// </summary>
    [Parameter]
    public EventCallback<TController?> ControllerChanged { get; set; }

    /// <summary>Gets or sets what to show, with the reason, when the OS has no provider for the contract.</summary>
    [Parameter]
    public RenderFragment<string>? Unavailable { get; set; }

    /// <summary>
    /// Gets what this host shows when no <see cref="Unavailable"/> is given; <see langword="null"/>
    /// keeps the SDK's default notice. A host that draws nothing in place (the context menu) returns
    /// an empty fragment.
    /// </summary>
    protected virtual RenderFragment<string>? DefaultUnavailable => null;

    /// <summary>Gets the provider's controller, once ready; valid while this component is rendered.</summary>
    public TController? Controller { get; private set; }

    /// <summary>
    /// Copies this host's parameters, by name, for the provider. Called each time the host's
    /// parameters are set; the provider is given a new set only when a value changed.
    /// </summary>
    /// <param name="parameters">The parameters to fill, keyed by the provider's <c>[Parameter]</c> names.</param>
    protected abstract void AddProviderParameters(IDictionary<string, object?> parameters);

    /// <summary>Called when the provider hands over its controller; the default does nothing.</summary>
    /// <param name="controller">The controller, or <see langword="null"/> when the provider does not implement the contract.</param>
    protected virtual Task OnControllerChangedAsync(TController? controller) => Task.CompletedTask;

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        Dictionary<string, object?> parameters = new(StringComparer.OrdinalIgnoreCase);
        AddProviderParameters(parameters);
        // Keep the previous instance when nothing changed: KazoEmbeddedComponent re-renders the
        // provider only for a new dictionary.
        if (_parameters is null || !SameValues(_parameters, parameters))
        {
            _parameters = parameters;
        }
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<KazoEmbeddedComponent<TController>>(0);
        builder.AddComponentParameter(1, nameof(KazoEmbeddedComponent<TController>.Contract), Contract);
        builder.AddComponentParameter(2, nameof(KazoEmbeddedComponent<TController>.Options), Options);
        builder.AddComponentParameter(3, nameof(KazoEmbeddedComponent<TController>.Parameters), _parameters);
        builder.AddComponentParameter(
            4,
            nameof(KazoEmbeddedComponent<TController>.ControllerChanged),
            EventCallback.Factory.Create<TController?>(this, HandleControllerChangedAsync));
        if ((Unavailable ?? DefaultUnavailable) is { } unavailable)
        {
            builder.AddComponentParameter(5, nameof(KazoEmbeddedComponent<TController>.Unavailable), unavailable);
        }

        builder.CloseComponent();
    }

    private async Task HandleControllerChangedAsync(TController? controller)
    {
        Controller = controller;
        await OnControllerChangedAsync(controller);
        await ControllerChanged.InvokeAsync(controller);
    }

    /// <summary>Shallow comparison: same names, and each value equal (<see cref="object.Equals(object?, object?)"/>).</summary>
    private static bool SameValues(IReadOnlyDictionary<string, object?> previous, Dictionary<string, object?> current)
    {
        if (previous.Count != current.Count)
        {
            return false;
        }

        foreach ((string name, object? value) in current)
        {
            if (!previous.TryGetValue(name, out object? old) || !Equals(old, value))
            {
                return false;
            }
        }

        return true;
    }
}
