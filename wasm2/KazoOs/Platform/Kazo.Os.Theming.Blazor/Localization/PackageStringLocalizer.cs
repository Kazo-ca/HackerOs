using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Theming.Blazor.Localization;

/// <summary>
/// Gives the reusable shell packages (theming, windowing, taskbar, mobile shell) their texts
/// (ADR 0104) without making localization a hard requirement of their hosts.
/// </summary>
/// <remarks>
/// <para>
/// Inside Kazo OS the host registers an <see cref="IStringLocalizer{T}"/> for every resource type
/// (the session-language <c>SessionStringLocalizer&lt;T&gt;</c>), so a package's components speak the
/// language of the signed-in session like the rest of the platform.
/// </para>
/// <para>
/// These packages are also meant to be adopted alone (the windowing sample host, the browser test
/// harness), by hosts that register no localization at all. There, a component falls back to the
/// package's neutral English <c>.resx</c> texts instead of failing to render on a missing service.
/// </para>
/// </remarks>
public static class PackageStringLocalizer
{
    private static readonly ConcurrentDictionary<Type, ResourceManager> s_managers = new();

    /// <summary>
    /// Returns the host's <see cref="IStringLocalizer{T}"/> of <typeparamref name="TResource"/> when one is
    /// registered, otherwise a localizer reading the neutral (English) texts of the resource family.
    /// </summary>
    /// <typeparam name="TResource">
    /// The marker type naming the resources: <c>MyPackage.Strings</c> reads <c>Resources/Strings.resx</c>.
    /// </typeparam>
    /// <param name="services">
    /// The component's service provider; <see langword="null"/> for a component instantiated
    /// without injection (a test attaching it to a renderer by hand), which gets the English texts.
    /// </param>
    public static IStringLocalizer Resolve<TResource>(IServiceProvider? services)
    {
        if (services?.GetService(typeof(IStringLocalizer<TResource>)) is IStringLocalizer registered)
        {
            return registered;
        }

        ResourceManager resources = s_managers.GetOrAdd(
            typeof(TResource),
            static type => new ResourceManager(GetResourceBaseName(type), type.Assembly));
        return new NeutralLocalizer(resources);
    }

    /// <summary>
    /// The manifest resource name of <paramref name="type"/>'s texts, by the
    /// <c>Microsoft.Extensions.Localization</c> convention used across Kazo OS:
    /// <c>&lt;assembly&gt;.Resources.&lt;type name relative to the assembly&gt;</c>.
    /// </summary>
    private static string GetResourceBaseName(Type type)
    {
        string root = type.Assembly.GetName().Name ?? string.Empty;
        string fullName = type.FullName ?? type.Name;
        string relative = root.Length > 0 && fullName.StartsWith(root + ".", StringComparison.Ordinal)
            ? fullName[(root.Length + 1)..]
            : fullName;
        return root.Length == 0 ? $"Resources.{relative}" : $"{root}.Resources.{relative}";
    }

    /// <summary>Reads the neutral texts only; arguments are formatted with the invariant culture.</summary>
    private sealed class NeutralLocalizer(ResourceManager resources) : IStringLocalizer
    {
        public LocalizedString this[string name]
        {
            get
            {
                string? value = resources.GetString(name, CultureInfo.InvariantCulture);
                return new LocalizedString(name, value ?? name, resourceNotFound: value is null, searchedLocation: resources.BaseName);
            }
        }

        public LocalizedString this[string name, params object[] arguments]
        {
            get
            {
                string? format = resources.GetString(name, CultureInfo.InvariantCulture);
                string value = string.Format(CultureInfo.InvariantCulture, format ?? name, arguments);
                return new LocalizedString(name, value, resourceNotFound: format is null, searchedLocation: resources.BaseName);
            }
        }

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
        {
            ResourceSet? set = resources.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
            if (set is null)
            {
                yield break;
            }

            foreach (DictionaryEntry entry in set)
            {
                if (entry.Key is string key && entry.Value is string value)
                {
                    yield return new LocalizedString(key, value, resourceNotFound: false, searchedLocation: resources.BaseName);
                }
            }
        }
    }
}
