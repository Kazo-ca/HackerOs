using System.Reflection;
using System.Xml.Linq;
using HackerOs.App.Abstractions;

namespace HackerOs.Tools.ManifestGenerator;

/// <summary>
/// Runs the <c>[RegisterFileContextMenu]</c> method of an <c>[AppManifest]</c> class at build
/// (ADR 0151 §3, ADR 0159 §2) and copies the titles of its entries from the app's resources
/// (ADR 0159 §3), so the OS never runs app code to build a menu.
/// </summary>
/// <remarks>
/// The method and its context live in the App SDK (<c>HackerOs.AppSdk.Blazor</c>), which the tool
/// does not reference: they are found by full name and the context is created in the app's own load
/// context. The entries come back as <see cref="AppContextMenuEntryManifest"/>, a type of
/// <c>HackerOs.App.Abstractions</c> the tool shares with the app.
/// </remarks>
public static class ContextMenuRegistrationInspector
{
    /// <summary>Full name of the attribute marking the registration method.</summary>
    public const string AttributeFullName = "HackerOs.AppSdk.Blazor.Files.RegisterFileContextMenuAttribute";

    /// <summary>Full name of the context the method receives.</summary>
    public const string ContextFullName = "HackerOs.AppSdk.Blazor.Files.RegisterFileContextMenuContext";

    /// <summary>Base name of the neutral (English) resource file holding the titles, under <c>Resources/</c>.</summary>
    public const string NeutralResourceFile = "AppManifest.resx";

    /// <summary>
    /// Runs the registration method of <paramref name="type"/>, twice, and returns its entries
    /// without titles; <see langword="null"/> when the class has no such method.
    /// </summary>
    /// <param name="type">The <c>[AppManifest]</c> class.</param>
    /// <param name="subject">The class name for diagnostics.</param>
    /// <param name="diagnostics">Receives <c>HOSAPP0026</c> on a bad method, a throw or two different runs.</param>
    /// <returns>The entries in declaration order, or <see langword="null"/>.</returns>
    public static IReadOnlyList<AppContextMenuEntryManifest>? Run(Type type, string subject, List<GeneratorDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(type);
        MethodInfo[] methods = [.. type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.FullName == AttributeFullName))];
        if (methods.Length == 0)
        {
            return null;
        }

        if (methods.Length > 1)
        {
            diagnostics.Add(new(GeneratorCodes.InvalidContextMenu, subject, "Only one method may be marked [RegisterFileContextMenu]."));
            return null;
        }

        MethodInfo method = methods[0];
        ParameterInfo[] parameters = method.GetParameters();
        if (!method.IsStatic
            || method.ReturnType != typeof(void)
            || method.IsGenericMethodDefinition
            || parameters.Length != 1
            || parameters[0].ParameterType.FullName != ContextFullName)
        {
            diagnostics.Add(new(GeneratorCodes.InvalidContextMenu, subject,
                $"[RegisterFileContextMenu] method '{method.Name}' must be 'static void {method.Name}(RegisterFileContextMenuContext context)'."));
            return null;
        }

        if (!TryInvoke(method, parameters[0].ParameterType, subject, diagnostics, out IReadOnlyList<AppContextMenuEntryManifest>? first)
            || !TryInvoke(method, parameters[0].ParameterType, subject, diagnostics, out IReadOnlyList<AppContextMenuEntryManifest>? second))
        {
            return null;
        }

        if (!first!.SequenceEqual(second!, AppContextMenuEntryComparer.Instance))
        {
            diagnostics.Add(new(GeneratorCodes.InvalidContextMenu, subject,
                "Two runs of the [RegisterFileContextMenu] method registered different entries: it must not depend on the time, files or services."));
            return null;
        }

        return first;
    }

    /// <summary>
    /// Copies the title of each entry in each language (ADR 0159 §3): English from
    /// <c>Resources/AppManifest.resx</c>, other cultures from <c>Resources/AppManifest.&lt;culture&gt;.resx</c>.
    /// In a project declaring several apps, keys are prefixed with the app id, as for the other keys.
    /// </summary>
    /// <param name="entries">The entries of one app.</param>
    /// <param name="projectDirectory">The app project directory, or <see langword="null"/> (tests): the key then stands for the English title.</param>
    /// <param name="appId">The app.</param>
    /// <param name="multi">Whether the project declares several apps.</param>
    /// <param name="subject">The class name for diagnostics.</param>
    /// <param name="diagnostics">Receives <c>HOSAPP0026</c> for a key missing from the English resources, <c>HOSAPP0040</c> for an unreadable file.</param>
    /// <returns>The entries with their titles.</returns>
    public static IReadOnlyList<AppContextMenuEntryManifest> ResolveTitles(
        IReadOnlyList<AppContextMenuEntryManifest> entries,
        string? projectDirectory,
        string appId,
        bool multi,
        string subject,
        List<GeneratorDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (projectDirectory is null)
        {
            return [.. entries.Select(entry => entry with { Titles = [new(AppContextMenus.EnglishCulture, entry.Title)] })];
        }

        string resources = Path.Combine(projectDirectory, "Resources");
        IReadOnlyDictionary<string, string> english = Read(Path.Combine(resources, NeutralResourceFile), diagnostics);
        List<(string Culture, IReadOnlyDictionary<string, string> Values)> cultures = [.. ManifestLocalizationGenerator.FindSources(projectDirectory)
            .Select(source => (source.Culture, Read(source.Path, diagnostics)))];

        List<AppContextMenuEntryManifest> resolved = [];
        foreach (AppContextMenuEntryManifest entry in entries)
        {
            string key = multi ? $"{appId}.{entry.Title}" : entry.Title;
            if (!english.TryGetValue(key, out string? text))
            {
                diagnostics.Add(new(GeneratorCodes.InvalidContextMenu, subject,
                    $"Menu entry '{entry.Name}': title key '{key}' is missing from Resources/{NeutralResourceFile} (the English text, ADR 0159 §3)."));
                continue;
            }

            List<AppContextMenuTitleManifest> titles = [new(AppContextMenus.EnglishCulture, text)];
            foreach ((string culture, IReadOnlyDictionary<string, string> values) in cultures)
            {
                if (culture != AppContextMenus.EnglishCulture && values.TryGetValue(key, out string? translated))
                {
                    titles.Add(new(culture, translated));
                }
            }

            resolved.Add(entry with { Titles = titles });
        }

        return resolved;
    }

    /// <summary>Gets the resource keys an app's menu titles use, so the localization files may hold them.</summary>
    /// <param name="manifest">The app's manifest.</param>
    public static IEnumerable<string> TitleKeys(AppManifest manifest) =>
        (manifest.ContextMenus ?? []).Select(entry => entry.Title);

    private static bool TryInvoke(
        MethodInfo method,
        Type contextType,
        string subject,
        List<GeneratorDiagnostic> diagnostics,
        out IReadOnlyList<AppContextMenuEntryManifest>? entries)
    {
        entries = null;
        try
        {
            object context = Activator.CreateInstance(contextType)!;
            method.Invoke(null, [context]);
            entries = (IReadOnlyList<AppContextMenuEntryManifest>)contextType.GetProperty("Entries")!.GetValue(context)!;
            return true;
        }
        catch (Exception exception)
        {
            // ADR 0151 §2: a method that throws registers nothing at all, never part of its entries.
            Exception root = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
            diagnostics.Add(new(GeneratorCodes.InvalidContextMenu, subject,
                $"The [RegisterFileContextMenu] method threw {root.GetType().Name}: {root.Message}"));
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string> Read(string path, List<GeneratorDiagnostic> diagnostics)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return values;
        }

        try
        {
            foreach (XElement data in XDocument.Load(path).Root?.Elements("data") ?? [])
            {
                if (data.Attribute("name")?.Value is { Length: > 0 } name
                    && data.Element("value")?.Value is { } value
                    && !string.IsNullOrWhiteSpace(value)
                    && data.Attribute("type") is null)
                {
                    values[name] = value.Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            diagnostics.Add(new(GeneratorCodes.InvalidLocalization, path, $"The file cannot be read: {exception.Message}"));
        }

        return values;
    }

    /// <summary>Compares two registrations by their canonical JSON (records hold lists, so record equality is by reference).</summary>
    private sealed class AppContextMenuEntryComparer : IEqualityComparer<AppContextMenuEntryManifest>
    {
        public static readonly AppContextMenuEntryComparer Instance = new();

        public bool Equals(AppContextMenuEntryManifest? x, AppContextMenuEntryManifest? y) =>
            x is not null && y is not null && Key(x) == Key(y);

        public int GetHashCode(AppContextMenuEntryManifest obj) => Key(obj).GetHashCode(StringComparison.Ordinal);

        private static string Key(AppContextMenuEntryManifest entry) => string.Join('\u001f',
            entry.Name, entry.Title, entry.Target, entry.Intent, entry.Icon?.Kind, entry.Icon?.Source, entry.Icon?.Library,
            string.Join(',', entry.Scopes), entry.Types is null ? "\u0000" : string.Join(',', entry.Types), entry.Selection,
            entry.First, entry.After, entry.AfterGroup, entry.Parent, entry.Screens, entry.Screen);
    }
}
