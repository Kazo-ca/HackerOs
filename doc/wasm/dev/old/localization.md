> **Archive — ne décrit pas HackerOS.** Cette conception (cinq cultures, `/etc/hackeros/locale.json`) n'a jamais été réalisée. La localisation réelle est décrite par l'[ADR 0104](../adr/0104-localization.md), l'[ADR 0111](../adr/0111-localization-implementation-adjustments.md) et [`localization.md`](../../framework/localization.md).

# HackerOS Globalization & Localization Architecture

## Overview

HackerOS supports globalization and multi-language localization across the Desktop Shell, platform dialogs, and applications using standard .NET localization paradigms (`IStringLocalizer<T>`) combined with manifest localization.

---

## 1. Core Principles

- **Default Culture:** `en-US` (English United States) serves as the primary fallback culture.
- **Supported Cultures:** `en-US`, `fr-FR` (French), `de-DE` (German), `es-ES` (Spanish), `ja-JP` (Japanese).
- **Runtime Language Switching:** The effective culture is stored in `/etc/hackeros/locale.json` and updated via `AppSettingsGateway`.

---

## 2. Component Localization Convention

Applications and platform components define `.resx` resource files in `Resources/`:

```razor
@inject IStringLocalizer<FileExplorerWindow> Localizer

<span class="toolbar-title">@Localizer["Title_FileExplorer"]</span>
<button class="btn">@Localizer["Action_NewFolder"]</button>
```

---

## 3. App Manifest Localization

Manifests support localized display names and descriptions through localized keys in `presentation`:

```json
{
  "id": "org.hackeros.file-explorer",
  "name": "File Explorer",
  "presentation": {
    "nameKey": "App_FileExplorer_Name",
    "descriptionKey": "App_FileExplorer_Description"
  }
}
```

---

## 4. Text Containment & LTR/RTL Support

- **Text Overflow:** All labels, buttons, and table cells use `text-overflow: ellipsis; overflow: hidden; white-space: nowrap;` or flexible wrapping to handle longer translated strings without layout truncation or broken boundaries.
- **Directionality:** Layouts use CSS Logical Properties (`margin-inline-start`, `padding-inline-end`) to automatically support LTR and RTL document directions.
