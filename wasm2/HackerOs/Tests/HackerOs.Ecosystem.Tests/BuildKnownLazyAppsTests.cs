using HackerOs.App.Abstractions;
using HackerOs.Platform.Core;

namespace HackerOs.Ecosystem.Tests;

/// <summary>Verifies the runnable host exposes every first-party system application.</summary>
public sealed class BuildKnownLazyAppsTests
{
    [Fact]
    public void Catalog_contains_all_first_party_system_apps()
    {
        AppCatalog catalog = BuildKnownLazyApps.Catalog;

        // 14 window apps (Theme Editor with ADR 0103), the hackwebd service (ADR 0077), the sample Blazor site (ADR 0084), the Intranet
        // sample (web/11) and 78 commands (stress joined with ADR 0070, nslookup with ADR 0074,
        // ausearch with ADR 0100, xdg-open with ADR 0096, hackbackup with ADR 0099, localectl with ADR 0104,
        // xdg-mime with ADR 0121, passwd with ADR 0120, prlimit with ADR 0125, nice and renice with ADR 0127, dmesg with ADR 0126, mount and umount with ADR 0181, hackpkg with ADR 0182, date with ADR 0193), plus sample-cmd, the third-party command the worker
        // pool loads on demand (ADR 0124 §4).
        Assert.Equal(97, catalog.Manifests.Count);
        // ADR 0064: the Linux commands added with the POSIX shell.
        foreach (string command in new[] { "locate", "zip", "unzip", "cut", "tee", "whoami", "df", "du", "top", "htop", "systemctl", "watch", "jobs", "wget", "ip", "netstat", "ln", "test", "read", "stress", "nslookup", "ausearch", "xdg-open", "localectl", "xdg-mime", "prlimit", "dmesg", "mount", "umount" })
        {
            Assert.Contains($"org.hackeros.commands.{command}", catalog.Manifests.Keys);
        }

        Assert.Contains("org.hackeros.browser", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.diagnostic", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.hack-paint", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.terminal", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.hackwebd", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.web-server-manager", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.samples.intranet", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.curl", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.ping", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.nmap", catalog.Manifests.Keys);
        // ADR 0051: cd/pwd/clear/help ship with the other commands in HackerOs.Commands.
        Assert.Contains("org.hackeros.cmd.cd", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.cmd.pwd", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.clear", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.help", catalog.Manifests.Keys);
        // ADR 0051 section 4: sudo and the environment commands are catalog commands too.
        Assert.Contains("org.hackeros.commands.sudo", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.export", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.unset", catalog.Manifests.Keys);
        Assert.Contains("org.hackeros.commands.env", catalog.Manifests.Keys);
        // ADR 0058 section 4: exit ends the shell that ran it, so the console can close the tab.
        Assert.Contains("org.hackeros.commands.exit", catalog.Manifests.Keys);

        // Window apps (the launcher-visible desktop suite) stay Visible; terminal commands and
        // background daemons (hackwebd, ADR 0077) and web sites (started on demand by a request,
        // ADR 0073; the sample Blazor site, ADR 0084) are deliberately Hidden — they're invoked by
        // name or started at boot, not double-clicked from a launcher, and shouldn't clutter it. The
        // desktop (ADR 0160 §1) is a window app the shell draws under the windows: hidden too.
        Assert.All(catalog.Manifests.Values, manifest => Assert.Equal(
            manifest.Kind is AppKind.Terminal or AppKind.Service or AppKind.Web || manifest.Id == "org.hackeros.desktop"
                ? AppLaunchVisibility.Hidden
                : AppLaunchVisibility.Visible,
            manifest.Presentation.LaunchVisibility));
        // One assembly per window app (the desktop included, ADR 0160), the two sample sites, the sample command (ADR 0124), plus the
        // single HackerOs.Commands assembly (ADR 0051).
        Assert.Equal(19, BuildKnownLazyAssemblies.Names.Count);
        Assert.Contains("HackerOs.Samples.TerminalApp.dll", BuildKnownLazyAssemblies.Names);
        Assert.Contains("HackerOs.Samples.BlazorSite.dll", BuildKnownLazyAssemblies.Names);
        Assert.Contains("HackerOs.Samples.Intranet.dll", BuildKnownLazyAssemblies.Names);
        Assert.Contains("HackerOs.Commands.dll", BuildKnownLazyAssemblies.Names);
    }

    [Fact]
    public void Every_app_comes_from_the_aggregated_BuiltInApplications_resource()
    {
        using Stream? resource = typeof(BuildKnownLazyApps).Assembly
            .GetManifestResourceStream(BuiltInApplicationsJsonSerializer.ResourceName);
        Assert.NotNull(resource);
        using StreamReader reader = new(resource);
        IReadOnlyList<BuiltInApplication> builtIn = BuiltInApplicationsJsonSerializer.DeserializeStrict(reader.ReadToEnd());

        // Every app is migrated to [AppManifest] (ADR 0047 §6): it reaches the host through its
        // ProjectReference only. No hand-written manifest resource and no manual lazy-load entry remain.
        Assert.DoesNotContain(
            typeof(BuildKnownLazyApps).Assembly.GetManifestResourceNames(),
            name => name.StartsWith("HackerOs.Ecosystem.Manifests.", StringComparison.Ordinal));
        Assert.Equal(
            builtIn.Select(item => item.Manifest.Id).Order(StringComparer.Ordinal),
            BuildKnownLazyApps.Catalog.Manifests.Keys.Order(StringComparer.Ordinal));
        // Desktop/mobile apps use platform.entryPoints instead of the legacy entryPoint.
        // The host's default lazy list must still include every desktop assembly.
        Assert.Equal(
            builtIn.Select(item => Assert.IsType<AppManifestPlatformResolution>(
                AppManifestPlatformSupport.Resolve(item.Manifest))
                .EntryPointsByPlatform[WellKnownAppPlatforms.Desktop].Assembly)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal),
            BuildKnownLazyAssemblies.Names.Order(StringComparer.Ordinal));

        BuiltInApplication calculator = Assert.Single(builtIn, item => item.Manifest.Id == "org.hackeros.calculator");
        AppManifestPlatformResolution calculatorPlatforms = Assert.IsType<AppManifestPlatformResolution>(
            AppManifestPlatformSupport.Resolve(calculator.Manifest));
        Assert.Equal("HackerOs.Apps.Calculator.dll",
            calculatorPlatforms.EntryPointsByPlatform[WellKnownAppPlatforms.Desktop].Assembly);
        Assert.Equal("HackerOs.Apps.Calculator.dll",
            calculatorPlatforms.EntryPointsByPlatform[WellKnownAppPlatforms.Mobile].Assembly);

        // Calculator's ProjectReference has no IsSystemApp metadata (ADR 0048 §1).
        Assert.False(calculator.IsSystemApp);
        Assert.False(BuildKnownLazyApps.Catalog.IsSystemApp("org.hackeros.calculator"));
    }

    [Fact]
    public void System_apps_follow_the_host_ProjectReference_metadata()
    {
        AppCatalog catalog = BuildKnownLazyApps.Catalog;
        string[] systemWindows =
        [
            // ADR 0210 §1: reaches real sites through the relay (network.real.access) without an approval prompt.
            "org.hackeros.browser",
            // ADR 0056 §7: its terminal panel hosts a shell (shell.host) without an approval prompt.
            "org.hackeros.code-editor",
            // ADR 0160 §1: the desktop, started by the shell under the windows.
            "org.hackeros.desktop",
            "org.hackeros.error-log-viewer",
            "org.hackeros.file-explorer",
            // ADR 0077: the static web server and its manager (companion service of the same assembly).
            "org.hackeros.hackwebd",
            "org.hackeros.settings",
            "org.hackeros.system-monitor",
            "org.hackeros.terminal",
            "org.hackeros.text-editor",
            // ADR 0103 §2: reads /etc/hackeros/themes (filesystem.system.read).
            "org.hackeros.theme-editor",
            "org.hackeros.web-server-manager",
        ];
        string[] uninstallable =
        [
            "org.hackeros.calculator",
            "org.hackeros.diagnostic",
            "org.hackeros.hack-paint",
            "org.hackeros.icon-viewer",
            // ADR 0084: the sample Blazor site, installed so the browser has a site to draw.
            "org.hackeros.samples.blazor-site",
            // web/11: the Intranet sample, the site of the ASP.Hack developer guide.
            "org.hackeros.samples.intranet",
            // ADR 0124 §4: a sample command, installable and removable like any third-party app.
            "org.hackeros.samples.terminal-app",
        ];

        // ADR 0048 §1 table: every command is a system app, like coreutils.
        string[] commands = [.. catalog.Manifests.Values
            .Where(manifest => manifest.Kind == AppKind.Terminal && manifest.Id != "org.hackeros.samples.terminal-app")
            .Select(manifest => manifest.Id)];
        Assert.Equal(78, commands.Length);

        Assert.Equal(
            systemWindows.Concat(commands).Order(StringComparer.Ordinal),
            catalog.SystemAppIds.Order(StringComparer.Ordinal));
        Assert.All(uninstallable, appId => Assert.False(catalog.IsSystemApp(appId)));
        Assert.Equal(catalog.Manifests.Count, catalog.SystemAppIds.Count + uninstallable.Length);
    }

    [Fact]
    public void No_non_system_app_of_the_host_declares_a_dangerous_capability()
    {
        // ADR 0049 "Conséquences": the base experience shows no approval window. Single, deliberate
        // exception (ADR 0056 section 11): Diagnostic stays uninstallable and its first launch asks an
        // administrator for system.storage.inspect, because the raw snapshot bypasses file rights.
        // ADR 0146: its isolation badge reads HackCrossOriginIsolation, which needs no capability,
        // so system.storage.inspect stays its only dangerous one.
        AppCatalog catalog = BuildKnownLazyApps.Catalog;
        const string diagnostic = "org.hackeros.diagnostic";

        Assert.All(
            catalog.Manifests.Values.Where(manifest => !catalog.IsSystemApp(manifest.Id) && manifest.Id != diagnostic),
            manifest => Assert.DoesNotContain(manifest.Capabilities, AppCapabilities.IsDangerous));
        Assert.False(catalog.IsSystemApp(diagnostic));
        Assert.Equal(
            [AppCapabilities.SystemStorageInspect],
            catalog.Manifests[diagnostic].Capabilities.Where(AppCapabilities.IsDangerous).Order(StringComparer.Ordinal));
    }
}
