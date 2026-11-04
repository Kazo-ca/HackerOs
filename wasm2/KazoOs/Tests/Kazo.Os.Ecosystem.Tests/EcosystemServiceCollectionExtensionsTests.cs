using Microsoft.Extensions.Configuration;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Ecosystem;
using Kazo.Os.Infrastructure.Browser.FileSystem;
using Kazo.Os.Infrastructure.Browser.Policy;
using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Intents;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Diagnostics;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Notifications;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;

namespace Kazo.Os.Ecosystem.Tests;

public sealed class EcosystemServiceCollectionExtensionsTests
{
    [Fact]
    public async Task AddHackerOsEcosystem_resolves_complete_synchronous_graph()
    {
        ServiceCollection services = new();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton<IJSRuntime, NonInvokedJsRuntime>();
        services.AddHackerOsEcosystem();
        // The settings services take an ILogger<T> (ADR 0044 warnings). Strip the WASM-only
        // diagnostic ILoggerProvider (the former server-rendered host did the same, ADR 0027):
        // its singleton-over-scoped edge is accepted in WASM but rejected by scope validation.
        services.RemoveAll<Microsoft.Extensions.Logging.ILoggerProvider>();

        await using ServiceProvider rootProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // Most of this graph is Scoped, not Singleton: every service that construct-injects
        // IJSRuntime (directly or transitively) must be, matching Blazor Server's per-circuit
        // IJSRuntime lifetime — a Singleton first resolved outside a live circuit would
        // permanently capture an unattached IJSRuntime and break JS interop for every later
        // circuit. Resolve from a scope, exactly as a real circuit/WASM app would, rather than
        // the root provider.
        await using AsyncServiceScope scope = rootProvider.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        Assert.IsType<IndexedDbFileSystemProvider>(provider.GetRequiredService<IFileSystemProvider>());
        Assert.Same(
            provider.GetRequiredService<IndexedDbCapabilityGrantRepository>(),
            provider.GetRequiredService<IPersistentCapabilityGrantRepository>());
        Assert.IsType<OsSettingsService>(provider.GetRequiredService<IOsSettingsService>());
        Assert.NotNull(provider.GetRequiredService<SettingsStoreToFilesMigration>());
        Assert.NotNull(provider.GetRequiredService<Kazo.Os.Platform.Blazor.Windows.WindowGeometryPersistence>());
        Assert.NotNull(provider.GetRequiredService<IFileSystemService>());
        Assert.NotNull(provider.GetRequiredService<ISessionService>());
        Assert.NotNull(provider.GetRequiredService<IProcessManager>());
        Assert.NotNull(provider.GetRequiredService<IResourceSimulator>());
        Assert.NotNull(provider.GetRequiredService<AppCatalog>());
        Assert.NotNull(provider.GetRequiredService<AppLifecycleOrchestrator>());
        Assert.NotNull(provider.GetRequiredService<AppIntentDispatcher>());
        Assert.NotNull(provider.GetRequiredService<WindowRuntime>());
        Assert.NotNull(provider.GetRequiredService<INotificationQueue>());
        Assert.NotNull(provider.GetRequiredService<ISimulationClock>());
        Assert.NotNull(provider.GetRequiredService<IDiagnosticSink>());
        Assert.NotNull(provider.GetRequiredService<IAuditLog>());
        Assert.NotNull(provider.GetRequiredService<IEventBus>());
        Assert.NotNull(provider.GetRequiredService<ITopicMessageBus>());

        // ADR 0150 §1: the platform itself provides the OS context menu, without loading an app.
        Assert.NotNull(provider.GetRequiredService<Kazo.Os.Platform.Blazor.ContextMenus.ContextMenuService>());
        Kazo.Os.AppSdk.Blazor.Components.KazoComponentResolution menu = await provider
            .GetRequiredService<Kazo.Os.AppSdk.Blazor.Components.IHackComponentHost>()
            .ResolveAsync(Kazo.Os.AppSdk.Components.KazoComponentContracts.ContextMenu);
        Assert.Equal(typeof(Kazo.Os.Platform.Blazor.ContextMenus.PlatformContextMenu), menu.ComponentType);
        Assert.Equal(Kazo.Os.AppSdk.Blazor.Components.KazoComponentResolution.PlatformProviderId, menu.ProviderAppId);

        SessionId sessionId = SessionId.FromGuid(Guid.NewGuid());
        IFileDialogService dialogs = provider.GetRequiredService<FileDialogServiceFactory>().Create(sessionId);
        Assert.Equal(sessionId, Assert.IsType<Kazo.Os.Platform.Blazor.Dialogs.FileDialogCoordinator>(dialogs).SessionId);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("nonsense", true)]
    public void Resource_limits_are_on_unless_the_host_turns_them_off(string? value, bool enabled)
    {
        Dictionary<string, string?> values = value is null ? [] : new() { ["HackerOs:ResourceLimits:Enabled"] = value };
        Microsoft.Extensions.Configuration.IConfiguration configuration =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(values).Build();

        Assert.Equal(enabled, ResourceLimitsServiceCollectionExtensions.ReadSettings(configuration).Enabled);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddHackerOsEcosystem_composes_the_resource_limits_of_ADR_0125(bool enabled)
    {
        ServiceCollection services = new();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton<IJSRuntime, NonInvokedJsRuntime>();
        services.AddHackerOsResourceLimits(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["HackerOs:ResourceLimits:Enabled"] = enabled.ToString() }).Build());
        services.AddHackerOsEcosystem();
        services.RemoveAll<Microsoft.Extensions.Logging.ILoggerProvider>();
        await using ServiceProvider rootProvider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        await using AsyncServiceScope scope = rootProvider.CreateAsyncScope();
        IServiceProvider provider = scope.ServiceProvider;

        IProcessResourceLimitTable limits = provider.GetRequiredService<IProcessResourceLimitTable>();
        Kazo.Os.Platform.Core.Processes.InMemoryProcessManager manager =
            Assert.IsType<Kazo.Os.Platform.Core.Processes.InMemoryProcessManager>(provider.GetRequiredService<IProcessManager>());

        Assert.Equal(enabled, limits.IsEnabled);
        Assert.Same(limits, manager.ResourceLimits);
        Assert.Same(provider.GetRequiredService<IProcessFileDescriptorTable>(), manager.FileDescriptors);
        Assert.NotNull(provider.GetRequiredService<AppLifecycleOrchestrator>().LoginResourceLimits);
        // No measurement is composed here (ADR 0066 off): the CPU and memory limits are inactive.
        Assert.False(provider.GetRequiredService<Kazo.Os.Platform.Core.Processes.ResourceLimitEnforcer>().IsEffective);
    }

    private sealed class NonInvokedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("DI graph validation must not invoke browser storage.");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            throw new InvalidOperationException("DI graph validation must not invoke browser storage.");
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager()
        {
            Initialize("http://localhost/", "http://localhost/");
        }
    }
}