using System.Globalization;

namespace Kazo.Os.Simulation.Abstractions.Settings;

/// <summary>Identifies which layer supplied the effective value of a setting (ADR 0044 section 3).</summary>
public enum SettingLayer
{
    /// <summary>The default declared by the app or OS schema.</summary>
    Default = 0,

    /// <summary>The system layer, <c>/etc/&lt;appId&gt;/&lt;document&gt;.config</c>.</summary>
    System = 1,

    /// <summary>The user layer, <c>~/.config/&lt;appId&gt;/&lt;document&gt;.config</c>.</summary>
    User = 2
}

/// <summary>
/// One problem found while reading a settings file. Readers never reject a whole file: the
/// offending line is ignored and the warning is written to the app's log (ADR 0044 section 5).
/// </summary>
/// <param name="Path">Absolute virtual path of the file that holds the problem.</param>
/// <param name="LineNumber">One-based line number of the problem.</param>
/// <param name="QualifiedKey">Qualified key concerned, or an empty string for a structural problem.</param>
/// <param name="Message">Human-readable description, without the path/line prefix.</param>
public sealed record LayeredConfigWarning(string Path, int LineNumber, string QualifiedKey, string Message)
{
    /// <summary>
    /// Formats the warning as one log line, e.g.
    /// <c>settings: /etc/x/settings.config:12: invalid value for 'tab-size' (expected int64), using default</c>.
    /// </summary>
    public string ToLogLine() => $"settings: {Path}:{LineNumber}: {Message}";
}

/// <summary>
/// The merged view of one settings document after layering defaults, <c>/etc</c> and <c>~/.config</c>.
/// </summary>
/// <param name="Values">Qualified key to effective serialized value, for every declared field.</param>
/// <param name="Sources">Qualified key to the layer that supplied <paramref name="Values"/>.</param>
/// <param name="LockedKeys">Qualified keys the system layer locks with its <c>[locked]</c> section.</param>
/// <param name="Warnings">Every problem found in either file, in file then line order.</param>
public sealed record LayeredConfigSnapshot(
    IReadOnlyDictionary<string, string> Values,
    IReadOnlyDictionary<string, SettingLayer> Sources,
    IReadOnlySet<string> LockedKeys,
    IReadOnlyList<LayeredConfigWarning> Warnings)
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the non-public keys whose value the reader may not see (ADR 0100 section 1): the private
    /// file that may hold it exists but denies the read, like <c>/etc/shadow</c> for a user. Their
    /// entry in <see cref="Values"/> is the declared default, never the hidden value; the layer in
    /// <see cref="Sources"/> is the one whose private file hides it.
    /// </summary>
    public IReadOnlySet<string> MaskedKeys { get; init; } = NoKeys;
}

/// <summary>
/// The four files of one settings document (ADR 0100 section 1): the public <c>&lt;doc&gt;.config</c>
/// of each layer and its private companion <c>&lt;doc&gt;.private.config</c> (mode <c>0600</c>), which
/// holds the keys whose sensitivity is not <see cref="SettingSensitivity.Public"/>.
/// </summary>
/// <param name="SystemContent">System public file text, or <see langword="null"/> when absent.</param>
/// <param name="UserContent">User public file text, or <see langword="null"/> when absent.</param>
/// <param name="SystemPath">System public file path, used in warnings.</param>
/// <param name="UserPath">User public file path, used in warnings (empty for a system-only document).</param>
public sealed record LayeredConfigFiles(string? SystemContent, string? UserContent, string SystemPath, string UserPath)
{
    /// <summary>Gets the system private file text, or <see langword="null"/> when absent or unreadable.</summary>
    public string? SystemPrivateContent { get; init; }

    /// <summary>Gets the user private file text, or <see langword="null"/> when absent or unreadable.</summary>
    public string? UserPrivateContent { get; init; }

    /// <summary>Gets whether the system private file exists but the reader may not read it.</summary>
    public bool SystemPrivateDenied { get; init; }

    /// <summary>Gets whether the user private file exists but the reader may not read it.</summary>
    public bool UserPrivateDenied { get; init; }

    /// <summary>Gets the system private file path.</summary>
    public string SystemPrivatePath => SettingsPrivateFiles.GetPrivatePath(SystemPath);

    /// <summary>Gets the user private file path (empty for a system-only document).</summary>
    public string UserPrivatePath => SettingsPrivateFiles.GetPrivatePath(UserPath);
}

/// <summary>
/// Naming and placement rules of the private settings files of ADR 0100 section 1, the
/// <c>/etc/passwd</c> / <c>/etc/shadow</c> split applied to every settings document.
/// </summary>
public static class SettingsPrivateFiles
{
    /// <summary>Suffix of a public settings file.</summary>
    public const string PublicSuffix = ".config";

    /// <summary>Suffix of the private companion file.</summary>
    public const string PrivateSuffix = ".private.config";

    /// <summary>Mode of a private settings file: <c>0600</c>, owner only.</summary>
    public const ushort PrivateFileMode = 0b110_000_000;

    /// <summary>
    /// Returns the private companion of a public settings file:
    /// <c>/etc/x/settings.config</c> gives <c>/etc/x/settings.private.config</c>. An empty path stays empty.
    /// </summary>
    /// <param name="publicPath">Public file path ending in <c>.config</c>, or an empty string.</param>
    public static string GetPrivatePath(string publicPath)
    {
        ArgumentNullException.ThrowIfNull(publicPath);
        if (publicPath.Length == 0)
        {
            return string.Empty;
        }

        return publicPath.EndsWith(PublicSuffix, StringComparison.Ordinal)
            ? string.Concat(publicPath.AsSpan(0, publicPath.Length - PublicSuffix.Length), PrivateSuffix)
            : publicPath + PrivateSuffix;
    }

    /// <summary>Gets whether a file name or path is a private settings file.</summary>
    /// <param name="path">File name or absolute path.</param>
    public static bool IsPrivateFile(string path) =>
        path is not null && path.EndsWith(PrivateSuffix, StringComparison.Ordinal);

    /// <summary>Gets whether a field is stored in the private file rather than the public one.</summary>
    /// <param name="field">Declared field.</param>
    public static bool IsPrivate(SettingFieldDeclaration field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Sensitivity != SettingSensitivity.Public;
    }

    /// <summary>Gets whether any field of <paramref name="schema"/> is stored in the private files.</summary>
    /// <param name="schema">Declared schema.</param>
    public static bool HasPrivateFields(SettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return schema.Fields.Values.Any(IsPrivate);
    }

    /// <summary>
    /// Applies edits to one layer's two files and moves misplaced keys to the file they belong in
    /// (ADR 0100 section 1: a key found in the wrong file is ignored on read and moved on the next
    /// write). A set goes to the file of the field's sensitivity, a removal clears both files.
    /// </summary>
    /// <param name="schema">Declared schema.</param>
    /// <param name="publicContent">Current public file text, or <see langword="null"/>.</param>
    /// <param name="privateContent">Current private file text, or <see langword="null"/>.</param>
    /// <param name="edits">Fields and their valid serialized values; <see langword="null"/> removes the key.</param>
    /// <returns>The new texts; a text is <see langword="null"/> when that file needs no write.</returns>
    public static (string? Public, string? Private) Apply(
        SettingsSchema schema,
        string? publicContent,
        string? privateContent,
        IReadOnlyList<(SettingFieldDeclaration Field, string? Value)> edits)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(edits);
        string? nextPublic = publicContent;
        string? nextPrivate = privateContent;

        // Move misplaced lines first, so an edit of the same key below wins over the moved value.
        foreach ((SettingFieldDeclaration field, string value) in FindMisplaced(schema, publicContent, wantPrivate: false))
        {
            nextPublic = ConfigDocumentEditor.RemoveValue(nextPublic, field.Group, field.Key);
            if (!HasKey(privateContent, field))
            {
                nextPrivate = ConfigDocumentEditor.SetValue(nextPrivate, field.Group, field.Key, value);
            }
        }

        foreach ((SettingFieldDeclaration field, string value) in FindMisplaced(schema, privateContent, wantPrivate: true))
        {
            nextPrivate = ConfigDocumentEditor.RemoveValue(nextPrivate, field.Group, field.Key);
            if (!HasKey(publicContent, field))
            {
                nextPublic = ConfigDocumentEditor.SetValue(nextPublic, field.Group, field.Key, value);
            }
        }

        foreach ((SettingFieldDeclaration field, string? value) in edits)
        {
            if (value is null)
            {
                nextPublic = nextPublic is null ? null : ConfigDocumentEditor.RemoveValue(nextPublic, field.Group, field.Key);
                nextPrivate = nextPrivate is null ? null : ConfigDocumentEditor.RemoveValue(nextPrivate, field.Group, field.Key);
            }
            else if (IsPrivate(field))
            {
                nextPrivate = ConfigDocumentEditor.SetValue(nextPrivate, field.Group, field.Key, value);
            }
            else
            {
                nextPublic = ConfigDocumentEditor.SetValue(nextPublic, field.Group, field.Key, value);
            }
        }

        return (
            string.Equals(nextPublic, publicContent, StringComparison.Ordinal) ? null : nextPublic ?? string.Empty,
            string.Equals(nextPrivate, privateContent, StringComparison.Ordinal) ? null : nextPrivate ?? string.Empty);
    }

    private static IEnumerable<(SettingFieldDeclaration Field, string Value)> FindMisplaced(
        SettingsSchema schema, string? content, bool wantPrivate)
    {
        if (content is null)
        {
            yield break;
        }

        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            if (line.Kind == ConfigLineKind.KeyValue
                && !line.InMalformedSection
                && line.Group != LayeredConfigResolver.LockedSectionName
                && schema.Fields.TryGetValue(line.QualifiedKey!, out SettingFieldDeclaration? field)
                && IsPrivate(field) == !wantPrivate
                && SettingValueCodec.TryNormalize(field, line.Value!, out string normalized))
            {
                yield return (field, normalized);
            }
        }
    }

    private static bool HasKey(string? content, SettingFieldDeclaration field) =>
        content is not null
        && ConfigDocumentLines.Parse(content).Any(line =>
            line.Kind == ConfigLineKind.KeyValue && line.QualifiedKey == field.QualifiedKey);
}

/// <summary>
/// Pure merge engine shared by the SDK (<c>HackSettings</c>) and the platform
/// (<c>IOsSettingsService</c>), implementing ADR 0044 sections 3 to 6.
/// </summary>
/// <remarks>
/// <para>Effective value of a key = the first found in this order:</para>
/// <list type="number">
/// <item>a key locked by the system layer: the system value, or the default when the system layer
/// has no valid value for it; the user layer is ignored for that key;</item>
/// <item>the user layer;</item>
/// <item>the system layer;</item>
/// <item>the declared default.</item>
/// </list>
/// <para>
/// Malformed lines, undeclared keys and ill-typed values are ignored line by line, each producing
/// one <see cref="LayeredConfigWarning"/>; a duplicate key is not an error, the last one wins.
/// </para>
/// </remarks>
public static class LayeredConfigResolver
{
    /// <summary>Name of the reserved system-layer section that lists locked keys.</summary>
    public const string LockedSectionName = "locked";

    /// <summary>Reserved root key that records the schema version; never a data field.</summary>
    public const string SchemaVersionKey = "schemaVersion";

    /// <summary>Merges the two public layers of one document over its schema defaults (no private files).</summary>
    /// <param name="schema">Declared fields, types and defaults.</param>
    /// <param name="systemContent">System file text, or <see langword="null"/> when the file is absent.</param>
    /// <param name="userContent">User file text, or <see langword="null"/> when the file is absent.</param>
    /// <param name="systemPath">System file path, used in warnings.</param>
    /// <param name="userPath">User file path, used in warnings.</param>
    /// <returns>The effective values, their sources, the locked keys and every warning.</returns>
    public static LayeredConfigSnapshot Resolve(
        SettingsSchema schema,
        string? systemContent,
        string? userContent,
        string systemPath,
        string userPath) =>
        Resolve(schema, new LayeredConfigFiles(systemContent, userContent, systemPath, userPath));

    /// <summary>
    /// Merges the four files of one document over its schema defaults (ADR 0044 sections 3 to 6,
    /// ADR 0100 section 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A public key is read from the public files only, a non-public key from the private files
    /// only; a key found in the wrong file is ignored with a warning (the next write moves it). A
    /// <see cref="SettingSensitivity.Restricted"/> key is read from the system private file only.
    /// Locks come from the system public file only.
    /// </para>
    /// <para>
    /// A non-public key whose private file denies the read is reported in
    /// <see cref="LayeredConfigSnapshot.MaskedKeys"/> instead of failing: the reader cannot know
    /// the value, as a user cannot read <c>/etc/shadow</c>.
    /// </para>
    /// </remarks>
    /// <param name="schema">Declared fields, types and defaults.</param>
    /// <param name="files">The texts and paths of the four files.</param>
    /// <returns>The effective values, their sources, the locked and masked keys, and every warning.</returns>
    public static LayeredConfigSnapshot Resolve(SettingsSchema schema, LayeredConfigFiles files)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(files.SystemPath);
        ArgumentNullException.ThrowIfNull(files.UserPath);

        List<LayeredConfigWarning> warnings = [];
        HashSet<string> locked = new(StringComparer.Ordinal);
        List<PendingInvalid> invalid = [];
        Dictionary<string, (string Value, int Line)> systemValues = ReadLayer(
            schema, files.SystemContent, files.SystemPath, LayerFile.SystemPublic, files.SystemPrivatePath, locked, warnings, invalid);
        Dictionary<string, (string Value, int Line)> userValues = ReadLayer(
            schema, files.UserContent, files.UserPath, LayerFile.UserPublic, files.UserPrivatePath, locked, warnings, invalid);
        Dictionary<string, (string Value, int Line)> systemPrivate = ReadLayer(
            schema, files.SystemPrivateContent, files.SystemPrivatePath, LayerFile.SystemPrivate, files.SystemPath, locked, warnings, invalid);
        Dictionary<string, (string Value, int Line)> userPrivate = ReadLayer(
            schema, files.UserPrivateContent, files.UserPrivatePath, LayerFile.UserPrivate, files.UserPath, locked, warnings, invalid);

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        Dictionary<string, SettingLayer> sources = new(StringComparer.Ordinal);
        HashSet<string> masked = new(StringComparer.Ordinal);
        foreach (SettingFieldDeclaration field in schema.Fields.Values)
        {
            string key = field.QualifiedKey;
            if (!SettingsPrivateFiles.IsPrivate(field))
            {
                if (!locked.Contains(key) && userValues.TryGetValue(key, out (string Value, int Line) user))
                {
                    Use(key, user.Value, SettingLayer.User);
                }
                else if (systemValues.TryGetValue(key, out (string Value, int Line) system))
                {
                    Use(key, system.Value, SettingLayer.System);
                }
                else
                {
                    Use(key, field.DefaultValue, SettingLayer.Default);
                }

                continue;
            }

            bool userLayerAllowed = !locked.Contains(key) && field.Sensitivity != SettingSensitivity.Restricted;
            if (userLayerAllowed && userPrivate.TryGetValue(key, out (string Value, int Line) userSecret))
            {
                Use(key, userSecret.Value, SettingLayer.User);
            }
            else if (userLayerAllowed && files.UserPrivateDenied)
            {
                Mask(field, SettingLayer.User);
            }
            else if (systemPrivate.TryGetValue(key, out (string Value, int Line) systemSecret))
            {
                Use(key, systemSecret.Value, SettingLayer.System);
            }
            else if (files.SystemPrivateDenied)
            {
                Mask(field, SettingLayer.System);
            }
            else
            {
                Use(key, field.DefaultValue, SettingLayer.Default);
            }
        }

        // Invalid-value warnings name the value actually used instead, which is only known now.
        foreach (PendingInvalid pending in invalid)
        {
            string fallback = sources[pending.Field.QualifiedKey] switch
            {
                SettingLayer.System => "using system value",
                SettingLayer.User => "using user value",
                _ => "using default"
            };
            warnings.Add(new LayeredConfigWarning(
                pending.Path,
                pending.Line,
                pending.Field.QualifiedKey,
                $"invalid value for '{pending.Field.QualifiedKey}' (expected {SettingValueCodec.DescribeExpected(pending.Field)}), {fallback}"));
        }

        warnings.Sort(static (left, right) =>
        {
            int byPath = string.CompareOrdinal(left.Path, right.Path);
            return byPath != 0 ? byPath : left.LineNumber.CompareTo(right.LineNumber);
        });

        return new LayeredConfigSnapshot(values, sources, locked, warnings) { MaskedKeys = masked };

        void Use(string key, string value, SettingLayer layer)
        {
            values[key] = value;
            sources[key] = layer;
        }

        void Mask(SettingFieldDeclaration field, SettingLayer layer)
        {
            // The default, never a guess: the hidden value stays in the file the reader cannot open.
            Use(field.QualifiedKey, field.DefaultValue, layer);
            masked.Add(field.QualifiedKey);
        }
    }

    private static Dictionary<string, (string Value, int Line)> ReadLayer(
        SettingsSchema schema,
        string? content,
        string path,
        LayerFile file,
        string companionPath,
        HashSet<string> locked,
        List<LayeredConfigWarning> warnings,
        List<PendingInvalid> invalid)
    {
        Dictionary<string, (string Value, int Line)> values = new(StringComparer.Ordinal);
        bool isLockSource = file == LayerFile.SystemPublic;
        bool isPrivate = file is LayerFile.SystemPrivate or LayerFile.UserPrivate;
        if (content is null)
        {
            return values;
        }

        bool userLockedSectionReported = false;
        foreach (ConfigLine line in ConfigDocumentLines.Parse(content))
        {
            switch (line.Kind)
            {
                case ConfigLineKind.Malformed:
                    warnings.Add(new LayeredConfigWarning(path, line.LineNumber, string.Empty, "malformed line ignored"));
                    continue;
                case ConfigLineKind.Section when line.Group == LockedSectionName && !isLockSource && !userLockedSectionReported:
                    userLockedSectionReported = true;
                    warnings.Add(new LayeredConfigWarning(
                        path,
                        line.LineNumber,
                        string.Empty,
                        isPrivate
                            ? "[locked] is only honored in the public system file, section ignored"
                            : "[locked] is only honored in the system layer, section ignored"));
                    continue;
                case ConfigLineKind.KeyValue:
                    break;
                default:
                    continue;
            }

            if (line.InMalformedSection)
            {
                // Already reported once through the malformed header itself.
                continue;
            }

            if (line.Group == LockedSectionName)
            {
                if (isLockSource)
                {
                    ReadLockLine(schema, line, path, locked, warnings);
                }

                continue;
            }

            string qualifiedKey = line.QualifiedKey!;
            if (line.Group is null && line.Key == SchemaVersionKey)
            {
                continue;
            }

            if (!schema.Fields.TryGetValue(qualifiedKey, out SettingFieldDeclaration? field))
            {
                warnings.Add(new LayeredConfigWarning(
                    path, line.LineNumber, qualifiedKey, $"unknown key '{qualifiedKey}' ignored"));
                continue;
            }

            // ADR 0100 section 1: each key lives in the file of its sensitivity; a misplaced line is
            // ignored (never read from the wrong file) and moved by the next write.
            if (SettingsPrivateFiles.IsPrivate(field) != isPrivate)
            {
                warnings.Add(new LayeredConfigWarning(
                    path,
                    line.LineNumber,
                    qualifiedKey,
                    isPrivate
                        ? $"public key '{qualifiedKey}' belongs in {companionPath}, ignored"
                        : $"sensitive key '{qualifiedKey}' belongs in {companionPath}, ignored"));
                continue;
            }

            if (file == LayerFile.UserPrivate && field.Sensitivity == SettingSensitivity.Restricted)
            {
                warnings.Add(new LayeredConfigWarning(
                    path, line.LineNumber, qualifiedKey, $"restricted key '{qualifiedKey}' is only honored in the system private file, ignored"));
                continue;
            }

            if (!SettingValueCodec.TryNormalize(field, line.Value!, out string normalized))
            {
                // A later valid duplicate may still win; an invalid line never erases a valid one.
                invalid.Add(new PendingInvalid(field, path, line.LineNumber));
                continue;
            }

            values[qualifiedKey] = (normalized, line.LineNumber);
        }

        return values;
    }

    private static void ReadLockLine(
        SettingsSchema schema,
        ConfigLine line,
        string path,
        HashSet<string> locked,
        List<LayeredConfigWarning> warnings)
    {
        // Inside [locked] the "key" is itself a qualified key such as "Editor.tab-size"; the parser
        // splits on '=' only, so dots in the key survive intact.
        string target = line.Key!;
        if (!schema.Fields.ContainsKey(target))
        {
            warnings.Add(new LayeredConfigWarning(
                path, line.LineNumber, target, $"cannot lock unknown key '{target}', ignored"));
            return;
        }

        switch (line.Value!.Trim())
        {
            case "true":
                locked.Add(target);
                break;
            case "false":
                locked.Remove(target);
                break;
            default:
                warnings.Add(new LayeredConfigWarning(
                    path, line.LineNumber, target, $"invalid lock value for '{target}' (expected boolean), ignored"));
                break;
        }
    }

    private sealed record PendingInvalid(SettingFieldDeclaration Field, string Path, int Line);

    /// <summary>Which of the four files of a document is being read.</summary>
    private enum LayerFile
    {
        SystemPublic,
        UserPublic,
        SystemPrivate,
        UserPrivate
    }
}

/// <summary>
/// Validates and normalizes serialized setting values against their declared type.
/// </summary>
public static class SettingValueCodec
{
    /// <summary>Checks a serialized value and returns its canonical form.</summary>
    /// <param name="field">Declared field.</param>
    /// <param name="value">Serialized value as read from a file.</param>
    /// <param name="normalized">Canonical serialized value (e.g. an integer without leading <c>+</c>).</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> is valid for <paramref name="field"/>.</returns>
    public static bool TryNormalize(SettingFieldDeclaration field, string value, out string normalized)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        normalized = value;
        switch (field.ValueType)
        {
            case SettingValueType.Integer:
                if (!long.TryParse(value.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
                {
                    return false;
                }

                normalized = number.ToString(CultureInfo.InvariantCulture);
                return true;
            case SettingValueType.Boolean:
                normalized = value.Trim();
                return normalized is "true" or "false";
            case SettingValueType.Enum:
                normalized = value.Trim();
                return field.AllowedValues is not null && field.AllowedValues.Contains(normalized, StringComparer.Ordinal);
            case SettingValueType.String:
            default:
                return true;
        }
    }

    /// <summary>Describes the expected type for warnings, e.g. <c>int64</c> or <c>one of: dark, light</c>.</summary>
    /// <param name="field">Declared field.</param>
    public static string DescribeExpected(SettingFieldDeclaration field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.ValueType switch
        {
            SettingValueType.Integer => "int64",
            SettingValueType.Boolean => "boolean",
            SettingValueType.Enum => $"one of: {string.Join(", ", field.AllowedValues ?? [])}",
            _ => "string"
        };
    }
}
