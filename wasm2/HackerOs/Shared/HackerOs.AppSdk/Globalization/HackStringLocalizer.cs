using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Resources;
using Microsoft.Extensions.Localization;

namespace HackerOs.AppSdk.Globalization;

/// <summary>
/// An <see cref="IStringLocalizer"/> over the <c>.resx</c> resources of one type, bound to a
/// culture supplier instead of <see cref="CultureInfo.CurrentUICulture"/> (ADR 0104, section 3).
/// </summary>
/// <remarks>
/// <para>
/// Every app of a browser tab shares one .NET thread culture, while every Kazo OS process has its
/// own <c>LANG</c>. The localizer an app receives therefore asks its own process for the culture on
/// every lookup (<see cref="IAppExecutionContext.Culture"/>), so <c>LANG=en_US.UTF-8 ls</c> prints
/// English even inside a French session.
/// </para>
/// <para>
/// Resources follow the <c>Microsoft.Extensions.Localization</c> convention: the strings of type
/// <c>MyApp.Strings</c> in assembly <c>MyApp</c> live in <c>Resources/Strings.resx</c> (English,
/// neutral) and <c>Resources/Strings.fr.resx</c> (French satellite). A key missing from the French
/// file falls back to English, with one debug warning per key; a key missing everywhere returns the
/// key itself with <see cref="LocalizedString.ResourceNotFound"/> set.
/// </para>
/// </remarks>
public class HackStringLocalizer : IStringLocalizer
{
    private static readonly ConcurrentDictionary<string, byte> s_warnedMissing = new(StringComparer.Ordinal);

    private readonly ResourceManager _resources;
    private readonly Func<CultureInfo> _culture;
    private readonly string _resourceName;

    /// <summary>Creates a localizer over an existing resource manager.</summary>
    /// <param name="resources">The resource manager of one <c>.resx</c> family.</param>
    /// <param name="culture">Supplies the language of the texts at each lookup.</param>
    public HackStringLocalizer(ResourceManager resources, Func<CultureInfo> culture)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _culture = culture ?? throw new ArgumentNullException(nameof(culture));
        _resourceName = resources.BaseName;
    }

    /// <summary>Gets the culture texts are looked up in right now.</summary>
    public CultureInfo Culture => _culture();

    /// <inheritdoc />
    public LocalizedString this[string name]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            string? value = Lookup(name, Culture);
            return new LocalizedString(name, value ?? name, resourceNotFound: value is null, searchedLocation: _resourceName);
        }
    }

    /// <inheritdoc />
    public LocalizedString this[string name, params object[] arguments]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(name);
            CultureInfo culture = Culture;
            string? format = Lookup(name, culture);
            // Arguments are formatted with the same culture as the text, so "{0:N1}" reads 1,5 in French.
            string value = string.Format(culture, format ?? name, arguments);
            return new LocalizedString(name, value, resourceNotFound: format is null, searchedLocation: _resourceName);
        }
    }

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures)
    {
        CultureInfo culture = Culture;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (CultureInfo current = culture; ; current = current.Parent)
        {
            ResourceSet? set = _resources.GetResourceSet(current, createIfNotExists: true, tryParents: false);
            if (set is not null)
            {
                foreach (DictionaryEntry entry in set)
                {
                    if (entry.Key is string key && entry.Value is string && seen.Add(key))
                    {
                        string? value = Lookup(key, culture);
                        yield return new LocalizedString(key, value ?? key, value is null, _resourceName);
                    }
                }
            }

            if (!includeParentCultures || current.Equals(CultureInfo.InvariantCulture))
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// Looks <paramref name="name"/> up in <paramref name="culture"/> then its parents (the neutral
    /// English resources last), noting once per key when a non-English culture lacks it.
    /// </summary>
    private string? Lookup(string name, CultureInfo culture)
    {
        string? value = _resources.GetString(name, culture);
        if (value is not null
            && !culture.TwoLetterISOLanguageName.Equals(HackLocale.DefaultLanguage.TwoLetterISOLanguageName, StringComparison.Ordinal)
            && !culture.Equals(CultureInfo.InvariantCulture)
            && _resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false)?.GetString(name) is null
            && s_warnedMissing.TryAdd($"{_resourceName}|{culture.Name}|{name}", 0))
        {
            Debug.WriteLine($"HackStringLocalizer: '{name}' has no {culture.Name} text in {_resourceName}; English used.");
        }

        return value;
    }
}

/// <summary>The <see cref="HackStringLocalizer"/> of the resources named after <typeparamref name="TResource"/>.</summary>
/// <typeparam name="TResource">The type whose <c>Resources/&lt;Type&gt;.resx</c> holds the texts.</typeparam>
public sealed class HackStringLocalizer<TResource> : HackStringLocalizer, IStringLocalizer<TResource>
{
    /// <summary>Creates a localizer of <typeparamref name="TResource"/>'s resources.</summary>
    /// <param name="culture">Supplies the language of the texts at each lookup.</param>
    public HackStringLocalizer(Func<CultureInfo> culture)
        : base(HackLocalization.GetResourceManager(typeof(TResource)), culture)
    {
    }
}

/// <summary>
/// Entry points of Kazo OS localization for apps (ADR 0104): a localizer bound to the app's own
/// process, and the resource naming convention.
/// </summary>
public static class HackLocalization
{
    /// <summary>The folder of a project that holds its <c>.resx</c> files.</summary>
    public const string ResourcesFolder = "Resources";

    private static readonly ConcurrentDictionary<Type, ResourceManager> s_managers = new();

    /// <summary>
    /// Returns a localizer of <typeparamref name="TResource"/>'s texts in the language of
    /// <paramref name="context"/>'s process, read again at every lookup.
    /// </summary>
    /// <typeparam name="TResource">The type naming the <c>.resx</c> family (<c>Resources/&lt;Type&gt;.resx</c>).</typeparam>
    /// <param name="context">The app instance; <see langword="null"/> (a component rendered outside any app, a test) gives English.</param>
    public static IStringLocalizer<TResource> GetLocalizer<TResource>(this IAppExecutionContext? context) =>
        context is null
            ? new HackStringLocalizer<TResource>(static () => HackLocale.DefaultLanguage)
            : new HackStringLocalizer<TResource>(() => context.Culture);

    /// <summary>
    /// Returns a localizer of <typeparamref name="TResource"/>'s texts in the language of the
    /// process running the current code (<see cref="HackEnvironment.Culture"/>), or English outside
    /// any app.
    /// </summary>
    /// <typeparam name="TResource">The type naming the <c>.resx</c> family.</typeparam>
    public static IStringLocalizer<TResource> GetLocalizer<TResource>() =>
        new HackStringLocalizer<TResource>(static () => HackExecutionContext.CurrentOrNull?.Culture ?? HackLocale.DefaultLanguage);

    /// <summary>
    /// Returns the shared resource manager of <paramref name="resourceType"/>, named by the
    /// <c>Microsoft.Extensions.Localization</c> convention:
    /// <c>&lt;assembly&gt;.Resources.&lt;type name relative to the assembly name&gt;</c>.
    /// </summary>
    /// <param name="resourceType">The type naming the resources.</param>
    public static ResourceManager GetResourceManager(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        return s_managers.GetOrAdd(resourceType, static type =>
        {
            Assembly assembly = type.Assembly;
            return new ResourceManager(GetResourceBaseName(type), assembly);
        });
    }

    /// <summary>
    /// Computes the manifest resource base name of <paramref name="resourceType"/>'s <c>.resx</c>:
    /// <c>MyApp.Strings</c> in assembly <c>MyApp</c> gives <c>MyApp.Resources.Strings</c>, matching
    /// what MSBuild names <c>Resources/Strings.resx</c> when the root namespace is the assembly name.
    /// </summary>
    /// <remarks>
    /// MSBuild names the resource after the project's <c>RootNamespace</c>, which may differ from the
    /// assembly name (<c>HackerOs.Ecosystem.Host</c> keeps the root namespace <c>HackerOs.Ecosystem</c>).
    /// The neutral resources live in the main assembly, so the namespace prefix whose
    /// <c>&lt;prefix&gt;.Resources.&lt;rest&gt;.resources</c> exists there wins; without one, the
    /// assembly name is assumed.
    /// </remarks>
    /// <param name="resourceType">The type naming the resources.</param>
    public static string GetResourceBaseName(Type resourceType)
    {
        ArgumentNullException.ThrowIfNull(resourceType);
        string root = resourceType.Assembly.GetName().Name ?? string.Empty;
        string fullName = resourceType.FullName ?? resourceType.Name;
        if (!fullName.StartsWith(root + ".", StringComparison.Ordinal))
        {
            HashSet<string> embedded = new(resourceType.Assembly.GetManifestResourceNames(), StringComparer.Ordinal);
            for (int dot = fullName.LastIndexOf('.'); dot > 0; dot = fullName.LastIndexOf('.', dot - 1))
            {
                string candidate = $"{fullName[..dot]}.{ResourcesFolder}.{fullName[(dot + 1)..]}";
                if (embedded.Contains(candidate + ".resources"))
                {
                    return candidate;
                }
            }
        }

        string relative = root.Length > 0 && fullName.StartsWith(root + ".", StringComparison.Ordinal)
            ? fullName[(root.Length + 1)..]
            : fullName;
        return root.Length == 0 ? $"{ResourcesFolder}.{relative}" : $"{root}.{ResourcesFolder}.{relative}";
    }
}

/// <summary>
/// An <see cref="IStringLocalizer{T}"/> that always answers in English, for components rendered
/// outside any Kazo OS session (component tests, previews):
/// <c>services.AddSingleton(typeof(IStringLocalizer&lt;&gt;), typeof(EnglishStringLocalizer&lt;&gt;))</c>.
/// </summary>
/// <typeparam name="TResource">The type whose <c>Resources/&lt;Type&gt;.resx</c> holds the texts.</typeparam>
public sealed class EnglishStringLocalizer<TResource> : HackStringLocalizer, IStringLocalizer<TResource>
{
    /// <summary>Creates the localizer.</summary>
    public EnglishStringLocalizer()
        : base(HackLocalization.GetResourceManager(typeof(TResource)), static () => HackLocale.DefaultLanguage)
    {
    }
}
