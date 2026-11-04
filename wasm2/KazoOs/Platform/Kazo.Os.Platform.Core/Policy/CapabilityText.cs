using System.Globalization;
using Kazo.Os.App.Abstractions;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Core.Policy;

/// <summary>
/// Writes the user-facing title and risk sentence of a catalog capability in one language
/// (ADR 0104), as polkit translates an action's description for the agent that shows it.
/// </summary>
/// <remarks>
/// <see cref="AppCapabilities.Describe"/> stays the catalog of identifiers and risk classes; its
/// texts are the fallback for a capability these resources do not know yet. The texts live in the
/// core's <c>Strings</c> resources under <c>Capability.&lt;id&gt;.Title</c> and
/// <c>Capability.&lt;id&gt;.Risk</c>.
/// </remarks>
public static class CapabilityText
{
    /// <summary>Describes one catalog capability in <paramref name="language"/>.</summary>
    /// <param name="capability">Exact catalog capability identifier.</param>
    /// <param name="language">Language of the reader; English when <see langword="null"/>.</param>
    /// <returns>The description with its title and risk sentence translated.</returns>
    /// <exception cref="ArgumentException">The identifier is not in the catalog.</exception>
    public static CapabilityDescription Describe(string capability, CultureInfo? language) =>
        Localize(AppCapabilities.Describe(capability), language);

    /// <summary>Translates the texts of a description built by <see cref="AppCapabilities.Describe"/>.</summary>
    /// <param name="description">The catalog description.</param>
    /// <param name="language">Language of the reader; English when <see langword="null"/>.</param>
    /// <returns>The same capability and risk class, with translated texts when they exist.</returns>
    public static CapabilityDescription Localize(CapabilityDescription description, CultureInfo? language)
    {
        ArgumentNullException.ThrowIfNull(description);
        IStringLocalizer text = Strings.For(language ?? Kazo.Os.AppSdk.Globalization.KazoLocale.DefaultLanguage);
        LocalizedString title = text[$"Capability.{description.Capability}.Title"];
        LocalizedString risk = text[$"Capability.{description.Capability}.Risk"];
        return description with
        {
            Title = title.ResourceNotFound ? description.Title : title.Value,
            RiskDescription = description.RiskDescription is null || risk.ResourceNotFound
                ? description.RiskDescription
                : risk.Value
        };
    }
}
