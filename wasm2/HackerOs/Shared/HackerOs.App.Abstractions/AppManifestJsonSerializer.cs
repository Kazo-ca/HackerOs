using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HackerOs.App.Abstractions;

/// <summary>
/// Provides strict, source-generated JSON serialization for canonical manifest fixtures and runtime manifests.
/// </summary>
public static class AppManifestJsonSerializer
{
    /// <summary>
    /// Serializes a manifest in canonical form: camelCase, compact, null members omitted, enums as
    /// strings, and unordered collections (icon paths, settings fields, ...) sorted ordinally, so the
    /// same manifest always produces byte-identical JSON (one trailing newline included).
    /// </summary>
    /// <param name="manifest">The manifest to serialize.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string SerializeCanonical(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        AppManifest canonical = CreateCanonicalManifest(manifest);
        return JsonSerializer.Serialize(canonical, AppManifestJsonSerializerContext.Default.AppManifest) + "\n";
    }

    /// <summary>
    /// Deserializes a manifest, rejecting any duplicate or unknown property at every level before
    /// binding, so a typo in a manifest fails loudly instead of being silently ignored.
    /// </summary>
    /// <param name="json">The manifest JSON.</param>
    /// <returns>The deserialized manifest.</returns>
    /// <exception cref="JsonException">The JSON is malformed, has a duplicate or unknown property, or deserializes to null.</exception>
    public static AppManifest DeserializeStrict(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using JsonDocument document = JsonDocument.Parse(json);
        ValidateObject(document.RootElement, "manifest", new[]
        {
            "schemaVersion",
            "id",
            "name",
            "version",
            "publisherId",
            "description",
            "kind",
            "entryPoint",
            "platform",
            "sdkCompatibility",
            "osCompatibility",
            "presentation",
            "localizations",
            "capabilities",
            "declaredTopicPermissions",
            "resources",
            "settings",
            "intents",
            "dependencies",
            "assets",
            "update",
            "fileHandlers",
            "terminal",
            "components",
            "contextMenus",
            "singleInstancePerUser",
            "autoStart",
            "serviceStartup",
            "supportsBack",
            "window",
            "execution",
            "web",
            "oomScoreAdj"
        });

        return JsonSerializer.Deserialize(document.RootElement.GetRawText(), AppManifestJsonSerializerContext.Default.AppManifest)
            ?? throw new JsonException("The provided manifest payload deserialized to null.");
    }

    /// <summary>
    /// Replaces the optional collections that source-generated deserialization leaves
    /// <see langword="null"/> when a property is omitted with empty lists, as the runtime model expects.
    /// </summary>
    /// <param name="manifest">A freshly deserialized manifest.</param>
    /// <returns>The same manifest with every optional collection non-null.</returns>
    public static AppManifest NormalizeOptionalCollections(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest with
        {
            Presentation = manifest.Presentation with { IconAssetPaths = manifest.Presentation.IconAssetPaths ?? [] },
            Localizations = manifest.Localizations ?? [],
            Capabilities = manifest.Capabilities ?? [],
            DeclaredTopicPermissions = manifest.DeclaredTopicPermissions ?? [],
            Intents = manifest.Intents ?? [],
            Dependencies = manifest.Dependencies ?? [],
            Assets = manifest.Assets ?? [],
            FileHandlers = manifest.FileHandlers ?? [],
            Terminal = manifest.Terminal is null ? null : manifest.Terminal with { Aliases = manifest.Terminal.Aliases ?? [] },
            Web = manifest.Web is null ? null : manifest.Web with { Urls = manifest.Web.Urls ?? [] },
            ContextMenus = manifest.ContextMenus is null
                ? null
                : [.. manifest.ContextMenus.Select(entry => entry with
                {
                    Titles = entry.Titles ?? [],
                    Scopes = entry.Scopes ?? [AppContextMenus.FileScope],
                    Target = entry.Target ?? AppContextMenus.SelfTarget,
                    Selection = entry.Selection ?? AppContextMenus.SelectionAll
                })]
        };
    }

    private static AppManifest CreateCanonicalManifest(AppManifest manifest)
    {
        PresentationManifest presentation = manifest.Presentation with
        {
            IconAssetPaths = [.. manifest.Presentation.IconAssetPaths.OrderBy(path => path, StringComparer.Ordinal)]
        };

        AppSettingsSchemaManifest? settings = manifest.Settings is null
            ? null
            : new AppSettingsSchemaManifest(
                manifest.Settings.SchemaVersion,
                [.. manifest.Settings.Fields
                    .OrderBy(field => field.Group ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(field => field.Key, StringComparer.Ordinal)
                    .Select(field => field with
                    {
                        AllowedValues = field.AllowedValues is null
                            ? null
                            : [.. field.AllowedValues.OrderBy(value => value, StringComparer.Ordinal)]
                    })],
                manifest.Settings.MigrationIds is null
                    ? null
                    : [.. manifest.Settings.MigrationIds.OrderBy(value => value, StringComparer.Ordinal)]);

        AppManifestPlatform? platform = manifest.Platform is null
            ? null
            : new AppManifestPlatform(
                [.. manifest.Platform.Supported.OrderBy(value => value, StringComparer.Ordinal)],
                [.. manifest.Platform.EntryPoints
                    .Select(entryPoint => entryPoint with
                    {
                        Platforms = [.. entryPoint.Platforms.OrderBy(value => value, StringComparer.Ordinal)]
                    })
                    .OrderBy(entryPoint => string.Join("|", entryPoint.Platforms), StringComparer.Ordinal)]);

        return manifest with
        {
            Presentation = presentation,
            Platform = platform,
            Localizations = [.. manifest.Localizations.OrderBy(item => item.Culture, StringComparer.Ordinal)],
            Capabilities = [.. manifest.Capabilities.OrderBy(value => value, StringComparer.Ordinal)],
            Dependencies = [.. manifest.Dependencies.OrderBy(item => item.AppId, StringComparer.Ordinal)
                .ThenBy(item => item.MinimumVersion, StringComparer.Ordinal)
                .ThenBy(item => item.MaximumVersion ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.Optional)],
            Assets = [.. manifest.Assets.OrderBy(item => item.Path, StringComparer.Ordinal)
                .ThenBy(item => item.Kind)
                .ThenBy(item => item.Role ?? string.Empty, StringComparer.Ordinal)],
            Intents = [.. manifest.Intents.OrderBy(item => item.IntentId, StringComparer.Ordinal)],
            Settings = settings,
            FileHandlers = [.. manifest.FileHandlers.OrderBy(item => item.MediaType ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(item => item.Extensions.Count)
                .ThenBy(item => string.Join("|", item.Extensions), StringComparer.Ordinal)],
            Components = manifest.Components is null
                ? null
                : [.. manifest.Components.OrderBy(item => item.Contract, StringComparer.Ordinal)
                    .ThenBy(item => item.Type, StringComparer.Ordinal)],
            // ADR 0159: the declared order of the entries is their order at a shared place, so it is
            // kept; an empty list is written as no property, like a manifest without entries.
            ContextMenus = manifest.ContextMenus is not { Count: > 0 }
                ? null
                : [.. manifest.ContextMenus.Select(entry => entry with
                {
                    Titles = [.. entry.Titles.OrderBy(title => title.Culture, StringComparer.Ordinal)],
                    Scopes = [.. entry.Scopes.OrderBy(scope => AppContextMenusOrder(scope))],
                    Types = entry.Types is null ? null : [.. entry.Types.OrderBy(type => type, StringComparer.Ordinal)]
                })],
            // The first URL is the site's primary address: the declared order is kept.
            Web = manifest.Web
        };
    }

    /// <summary>Sorts scopes in menu order (file, directory, background, desktop), an unknown one last.</summary>
    private static int AppContextMenusOrder(string scope)
    {
        for (int index = 0; index < AppContextMenus.Scopes.Count; index++)
        {
            if (string.Equals(AppContextMenus.Scopes[index], scope, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return int.MaxValue;
    }

    private static void ValidateObject(JsonElement element, string path, IReadOnlyCollection<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new JsonException($"Duplicate property '{property.Name}' at '{path}'.");
            }

            if (!allowedProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new JsonException($"Unknown property '{property.Name}' at '{path}'.");
            }

            switch (property.Name)
            {
                case "entryPoint":
                    ValidateObject(property.Value, $"{path}.entryPoint", ["assembly", "type"]);
                    break;
                case "platform":
                    ValidateObject(property.Value, $"{path}.platform", ["supported", "entryPoints"]);
                    break;
                case "entryPoints":
                    ValidateArray(property.Value, $"{path}.entryPoints", ["platforms", "assembly", "type"]);
                    break;
                case "sdkCompatibility":
                case "osCompatibility":
                    ValidateObject(property.Value, $"{path}.{property.Name}", ["minimumVersion", "maximumVersion"]);
                    break;
                case "presentation":
                    ValidateObject(property.Value, $"{path}.presentation", ["category", "launchVisibility", "iconAssetPaths"]);
                    break;
                case "localizations":
                    ValidateArray(property.Value, $"{path}.localizations", ["culture", "resourcePath"]);
                    break;
                case "declaredTopicPermissions":
                    ValidateArray(property.Value, $"{path}.declaredTopicPermissions", ["id", "description"]);
                    break;
                case "resources":
                    ValidateObject(property.Value, $"{path}.resources", ["baselineCpuWeight", "burstCpuWeight", "baselineMemoryWeight", "burstMemoryWeight", "baselineStorageIoWeight", "burstStorageIoWeight", "baselineNetworkIoWeight", "burstNetworkIoWeight"]);
                    break;
                case "settings":
                    ValidateObject(property.Value, $"{path}.settings", ["schemaVersion", "migrationIds", "fields"]);
                    break;
                case "intents":
                    ValidateArray(property.Value, $"{path}.intents", ["intentId", "payloadSchemaAssetPath"]);
                    break;
                case "dependencies":
                    ValidateArray(property.Value, $"{path}.dependencies", ["appId", "minimumVersion", "maximumVersion", "optional"]);
                    break;
                case "assets":
                    ValidateArray(property.Value, $"{path}.assets", ["path", "kind", "sha256", "role"]);
                    break;
                case "update":
                    ValidateObject(property.Value, $"{path}.update", ["migrationIds", "minimumUpgradableVersion"]);
                    break;
                case "fileHandlers":
                    ValidateArray(property.Value, $"{path}.fileHandlers", ["mediaType", "extensions", "actions", "priority"]);
                    break;
                case "terminal":
                    ValidateObject(property.Value, $"{path}.terminal", ["name", "aliases", "usage"]);
                    break;
                case "components":
                    ValidateArray(property.Value, $"{path}.components", ["contract", "assembly", "type"]);
                    break;
                case "contextMenus":
                    ValidateArray(property.Value, $"{path}.contextMenus", ["name", "title", "titles", "target", "intent", "icon", "scopes", "types", "selection", "first", "after", "afterGroup", "parent", "screens", "screen"]);
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        break;
                    }

                    foreach (JsonElement entry in property.Value.EnumerateArray().Where(static entry => entry.ValueKind == JsonValueKind.Object))
                    {
                        if (entry.TryGetProperty("titles", out JsonElement titles))
                        {
                            ValidateArray(titles, $"{path}.contextMenus.titles", ["culture", "text"]);
                        }

                        if (entry.TryGetProperty("icon", out JsonElement icon))
                        {
                            ValidateObject(icon, $"{path}.contextMenus.icon", ["kind", "source", "library"]);
                        }
                    }

                    break;
                case "window":
                    ValidateObject(property.Value, $"{path}.window", ["defaultWidth", "defaultHeight", "minWidth", "minHeight", "maxWidth", "maxHeight", "resizable", "rememberGeometry"]);
                    break;
                case "web":
                    ValidateObject(property.Value, $"{path}.web", ["urls", "blazor"]);
                    if (property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.TryGetProperty("blazor", out JsonElement blazor))
                    {
                        ValidateObject(blazor, $"{path}.web.blazor", ["assembly", "type"]);
                    }

                    break;
                default:
                    break;
            }
        }
    }

    private static void ValidateArray(JsonElement element, string path, IReadOnlyCollection<string> allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        int index = 0;
        foreach (JsonElement item in element.EnumerateArray())
        {
            ValidateObject(item, $"{path}[{index}]", allowedProperties);
            index++;
        }
    }
}

/// <summary>
/// Source-generated (trim- and AOT-safe) JSON metadata for <see cref="AppManifest"/> and every type it
/// contains: camelCase names, null members omitted, enums as strings.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false,
    UseStringEnumConverter = true,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(AppManifest))]
[JsonSerializable(typeof(AppEntryPointManifest))]
[JsonSerializable(typeof(AppManifestPlatform))]
[JsonSerializable(typeof(AppPlatformEntryPointManifest))]
[JsonSerializable(typeof(AppSdkCompatibilityManifest))]
[JsonSerializable(typeof(PresentationManifest))]
[JsonSerializable(typeof(LocalizationManifest))]
[JsonSerializable(typeof(AppResourceProfileManifest))]
[JsonSerializable(typeof(AppSettingsSchemaManifest))]
[JsonSerializable(typeof(AppSettingFieldManifest))]
[JsonSerializable(typeof(AppIntentDeclarationManifest))]
[JsonSerializable(typeof(AppDependencyManifest))]
[JsonSerializable(typeof(AssetManifest))]
[JsonSerializable(typeof(UpdateManifest))]
[JsonSerializable(typeof(FileHandlerManifest))]
[JsonSerializable(typeof(TerminalCommandManifest))]
[JsonSerializable(typeof(AppComponentManifest))]
[JsonSerializable(typeof(AppContextMenuEntryManifest))]
[JsonSerializable(typeof(AppContextMenuTitleManifest))]
[JsonSerializable(typeof(AppContextMenuIconManifest))]
[JsonSerializable(typeof(WindowOptions))]
[JsonSerializable(typeof(WebAppManifest))]
[JsonSerializable(typeof(WebBlazorManifest))]
[JsonSerializable(typeof(AppKind))]
[JsonSerializable(typeof(AppExecutionHost))]
[JsonSerializable(typeof(AppLaunchVisibility))]
[JsonSerializable(typeof(AppSettingValueType))]
[JsonSerializable(typeof(AppSettingScope))]
[JsonSerializable(typeof(AppSettingSensitivity))]
public sealed partial class AppManifestJsonSerializerContext : JsonSerializerContext
{
}
