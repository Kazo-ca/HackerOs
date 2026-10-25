# App Tile Color Customization

This document provides an overview of the app tile color customization feature in HackerSimulator.

## Overview

The app tile color customization feature allows users to:
- Customize the colors of app tiles in the Start Menu
- Have theme support for app tile colors with the option to use theme-defined colors only
- Automatically assign random colors to newly pinned apps

## Implementation Details

### Theme Integration

- The Theme interface has been extended to include:
  - `appTileForegroundColors`: An array of foreground colors for app tiles
  - `disableCustomAppTileColors`: An option to disable custom colors and use theme colors only

- App tile colors are now defined using CSS variables that reference theme values
- Both background and foreground colors can be customized for better contrast

### Color Assignment Format

App tile color assignments are stored in user settings with the format:
```
appId;colorClass,appId;colorClass,appId;colorClass
```

Example:
```
terminal;A,browser;B,calculator;G
```

This allows for efficient storage and retrieval of color assignments.

### Random Color Assignment

When a new app is pinned to the Start Menu, it is automatically assigned a random color class (A-I) if no color preference exists. This color assignment is then saved to user settings for consistency across sessions.

## User Interface

Users can customize app tile colors through the Settings app:
1. Navigate to the "Start Menu" section in Settings
2. Toggle "Use theme colors only" to enable/disable custom colors
3. For each pinned app, select a color from the available color options

## Developer Information

To work with app tile colors programmatically:

### Get Color Assignment for an App
```typescript
const userSettings = os.getUserSettings();
const colorClass = await userSettings.getAppTileColorAssignment('appId');
```

### Set Color Assignment for an App
```typescript
const userSettings = os.getUserSettings();
await userSettings.setAppTileColorAssignment('appId', 'A'); // A-I are valid color classes
```

### Check if Custom Colors are Disabled
```typescript
const themeManager = os.themeSystem.getThemeManager();
const currentTheme = themeManager.getCurrentTheme();
const disableCustomColors = currentTheme.disableCustomAppTileColors || false;
```
