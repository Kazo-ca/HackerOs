using HackerOs.Platform.Core.Appearance;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Shell;

namespace HackerOs.Platform.Core.Settings;

/// <summary>
/// The OS settings documents an app may open with <c>HackSettings.OpenSystemAsync</c> (ADR 0056,
/// section 8). Only layered <c>.config</c> documents with a declared schema are listed; whole-file
/// JSON documents (<c>file-associations.json</c>) and per-app state files are not.
/// </summary>
public static class OsSettingsDocumentRegistry
{
    private static readonly Dictionary<string, OsSettingsDocument> Documents = new(StringComparer.Ordinal)
    {
        [AppearanceSettingsDocuments.Document.DocumentId] = AppearanceSettingsDocuments.Document,
        [MobileSettingsDocuments.Document.DocumentId] = MobileSettingsDocuments.Document,
        [AccessibilitySettingsDocuments.Document.DocumentId] = AccessibilitySettingsDocuments.Document,
        [UiPlatformPreferenceSettingsDocuments.Document.DocumentId] = UiPlatformPreferenceSettingsDocuments.Document,
        [SecuritySettingsDocuments.Document.DocumentId] = SecuritySettingsDocuments.Document,
        [PolicySettingsDocuments.Document.DocumentId] = PolicySettingsDocuments.Document,
        [Clipboard.ClipboardSettingsDocuments.Document.DocumentId] = Clipboard.ClipboardSettingsDocuments.Document,
        [WindowKeyBindingsSettingsDocuments.Document.DocumentId] = WindowKeyBindingsSettingsDocuments.Document,
        // ADR 0126: Settings > Memory switches the OOM killer through HackSettings.OpenSystemAsync.
        [Processes.MemorySettingsDocuments.Document.DocumentId] = Processes.MemorySettingsDocuments.Document,
        // ADR 0179 §7: Settings > Screens chooses primary-taskbar and forgets remembered screen places.
        [ScreensSettingsDocuments.Document.DocumentId] = ScreensSettingsDocuments.Document,
        // ADR 0193 §2: Settings > Date and time chooses the user's time zone.
        [Globalization.TimeZoneSettingsDocuments.Document.DocumentId] = Globalization.TimeZoneSettingsDocuments.Document,
        // ADR 0205 §3: Settings > Appearance chooses the cursor theme.
        [CursorSettingsDocuments.Document.DocumentId] = CursorSettingsDocuments.Document,
        // ADR 0203 §3: Settings > Appearance chooses the icon pack.
        [IconPackSettingsDocuments.Document.DocumentId] = IconPackSettingsDocuments.Document
    };

    /// <summary>Gets every listed document id.</summary>
    public static IReadOnlyCollection<string> DocumentIds => Documents.Keys;

    /// <summary>Finds a document by id.</summary>
    /// <param name="documentId">File name without <c>.config</c>.</param>
    /// <param name="document">The document when listed.</param>
    public static bool TryGet(string documentId, out OsSettingsDocument document)
    {
        if (documentId is not null && Documents.TryGetValue(documentId, out OsSettingsDocument? found))
        {
            document = found;
            return true;
        }

        document = null!;
        return false;
    }
}
