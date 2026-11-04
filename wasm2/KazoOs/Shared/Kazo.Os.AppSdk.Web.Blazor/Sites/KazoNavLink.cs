using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;

namespace Kazo.Os.AppSdk.Web.Blazor.Sites;

/// <summary>
/// The <c>NavLink</c> of a Blazor site (ADR 0084, section 5): an <c>&lt;a&gt;</c> that carries
/// <see cref="ActiveClass"/> while the tab's address matches its <see cref="Href"/>. The click itself
/// is handled like any site link by <see cref="KazoWebSiteView"/>.
/// </summary>
public sealed class KazoNavLink : KazoComponentBase, IDisposable
{
    private KazoWebSite? _site;

    /// <summary>Gets or sets the link's target, absolute or relative to the site.</summary>
    [Parameter]
    public string? Href { get; set; }

    /// <summary>Gets or sets how the address must match: <see cref="NavLinkMatch.Prefix"/> (default) or <see cref="NavLinkMatch.All"/>.</summary>
    [Parameter]
    public NavLinkMatch Match { get; set; } = NavLinkMatch.Prefix;

    /// <summary>Gets or sets the class added while the link is active (<c>active</c> by default).</summary>
    [Parameter]
    public string ActiveClass { get; set; } = "active";

    /// <summary>Gets or sets the link's content.</summary>
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    /// <summary>Gets or sets the other attributes of the <c>&lt;a&gt;</c> (<c>class</c>, <c>title</c>…).</summary>
    [Parameter(CaptureUnmatchedValues = true)]
    public IReadOnlyDictionary<string, object>? AdditionalAttributes { get; set; }

    /// <summary>Gets the tab's site services, cascaded by <see cref="KazoWebSiteView"/>.</summary>
    [CascadingParameter]
    public KazoWebSite? Site { get; set; }

    /// <summary>Gets whether the tab's address matches <see cref="Href"/>.</summary>
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(_site, Site))
        {
            if (_site is not null)
            {
                _site.Navigation.LocationChanged -= OnLocationChanged;
            }

            _site = Site;
            if (_site is not null)
            {
                _site.Navigation.LocationChanged += OnLocationChanged;
            }
        }

        IsActive = ComputeIsActive();
    }

    /// <inheritdoc />
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "a");
        builder.AddMultipleAttributes(1, AdditionalAttributes);
        builder.AddAttribute(2, "href", Href);
        string? cssClass = AdditionalAttributes is not null && AdditionalAttributes.TryGetValue("class", out object? value) ? value?.ToString() : null;
        if (IsActive)
        {
            cssClass = string.IsNullOrEmpty(cssClass) ? ActiveClass : $"{cssClass} {ActiveClass}";
        }

        builder.AddAttribute(3, "class", cssClass);
        builder.AddContent(4, ChildContent);
        builder.CloseElement();
    }

    private bool ComputeIsActive()
    {
        if (_site is null || Href is null)
        {
            return false;
        }

        string current = _site.Navigation.Uri;
        string target = _site.Navigation.ToAbsoluteUri(Href).AbsoluteUri;
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Match == NavLinkMatch.All)
        {
            return false;
        }

        // Prefix: /notes matches /notes/5 but not /notesbook; the site root matches everything.
        string site = _site.Navigation.BaseUri;
        if (string.Equals(target, site, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = target.EndsWith('/') ? target : target + "/";
        return current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        bool active = ComputeIsActive();
        if (active != IsActive)
        {
            IsActive = active;
            _ = InvokeAsync(StateHasChanged);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_site is not null)
        {
            _site.Navigation.LocationChanged -= OnLocationChanged;
        }
    }
}
