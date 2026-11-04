# HackerOs.AppSdk.Icons

Offline icon contracts, searchable embedded catalogs, and the native Blazor `HackerIcon`
renderer used by KazoOS apps and reusable shell components. The package embeds its Bootstrap,
Font Awesome Free, Lucide, and Simple Icons metadata; consumers do not need a CDN, web font, or
server connection.

Register one catalog in the consuming host and render icons by stable library/name pair or by an
already resolved descriptor:

```razor
@using HackerOs.AppSdk.Icons

<HackerIcon Library="IconLibrary.Lucide" Name="settings" Size="20" Title="Settings" />
```

```csharp
IIconCatalog catalog = new IconCatalog();
IconDescriptor settings = catalog.Search("settings", IconLibrary.Lucide, maxResults: 1).Single();
```

`HackerOs.Taskbar.Blazor` consumes this package transitively. Applications may reference it
directly when they use `HackerIcon`, `IIconCatalog`, or the catalog contracts themselves.
