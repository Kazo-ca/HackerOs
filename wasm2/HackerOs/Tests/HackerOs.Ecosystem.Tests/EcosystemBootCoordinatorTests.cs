using HackerOs.App.Abstractions;
using HackerOs.App.Abstractions.Policy;
using HackerOs.Infrastructure.Browser.FileSystem;
using HackerOs.Platform.Core;
using HackerOs.Platform.Core.Apps;
using HackerOs.Platform.Core.Events;
using HackerOs.Platform.Core.FileSystem;
using HackerOs.Platform.Core.Lifecycle;
using HackerOs.Platform.Core.Policy;
using HackerOs.Platform.Core.Settings;
using HackerOs.Platform.Core.Time;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Gateways;
using HackerOs.Simulation.Abstractions.Settings;
using HackerOs.Simulation.Abstractions.Sessions;
using Microsoft.JSInterop;

namespace HackerOs.Ecosystem.Tests;

public sealed class EcosystemBootCoordinatorTests
{
    private readonly IndexedDbFileSystemBootstrapper _fsBootstrapper;
    private readonly FileSystemService _fileSystem;
    private readonly SettingsStoreToFilesMigration _settings;
    private readonly FakeCapabilityGrantRepository _grants = new();
    private readonly FakeAppCatalogRepository _catalog = new();
    private readonly AppCatalog _selectedCatalog = CreateEmptyCatalog();
    private readonly ManualSimulationClock _clock = new(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1));
    private readonly AppEnablementToFilesMigration _enablement;
    private readonly FakeLocalGroupRepository _groups = new();
    private readonly FakeLocalUserRepository _users = new();

    public EcosystemBootCoordinatorTests()
    {
        IJSRuntime jsRuntime = new TestJsRuntime();
        _fsBootstrapper = new IndexedDbFileSystemBootstrapper(jsRuntime);
        _fileSystem = CreateFileSystem();
        _settings = new SettingsStoreToFilesMigration(
            _fileSystem,
            new ManualSimulationClock(DateTimeOffset.UnixEpoch, TimeSpan.FromSeconds(1)),
            new EmptyLegacySettingsStore());
        _enablement = new AppEnablementToFilesMigration(_fileSystem, _clock, _selectedCatalog);
    }

    [Fact]
    public void Constructor_validates_arguments()
    {
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            null!, _settings, _grants, _catalog, _selectedCatalog, _enablement, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, null!, _grants, _catalog, _selectedCatalog, _enablement, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, null!, _catalog, _selectedCatalog, _enablement, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, _grants, null!, _selectedCatalog, _enablement, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, _grants, _catalog, null!, _enablement, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, _grants, _catalog, _selectedCatalog, null!, _groups, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, _grants, _catalog, _selectedCatalog, _enablement, null!, _users));
        Assert.Throws<ArgumentNullException>(() => new EcosystemBootCoordinator(
            _fsBootstrapper, _settings, _grants, _catalog, _selectedCatalog, _enablement, _groups, null!));
    }

    [Fact]
    public async Task BootAsync_returns_boot_result_on_success()
    {
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper,
            _settings,
            _grants,
            _catalog,
            _selectedCatalog,
            _enablement,
            _groups,
            _users);

        _grants.PolicyRevision = 1L;
        EcosystemBootResult result = await bootCoordinator.BootAsync();

        Assert.NotNull(result);
        Assert.Equal(1L, result.PolicyRevision);
        Assert.Equal(0, result.CatalogEntryCount);
        Assert.Empty(result.Users);
    }

    [Fact]
    public async Task BootAsync_installs_the_default_settings_files_in_etc()
    {
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper, _settings, _grants, _catalog, _selectedCatalog, _enablement, _groups, _users);

        await bootCoordinator.BootAsync();

        foreach (string path in new[]
                 {
                     "/etc/hackeros/appearance.config",
                     "/etc/hackeros/ui-platform-preference.config",
                     "/etc/hackeros/policy.config",
                     "/etc/hackeros/file-associations.json"
                 })
        {
            FileSystemResult<FileSystemEntrySnapshot> stat = await _fileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(path)),
                new FileSystemAuthorizationContext(
                    new AppOperationContext
                    {
                        AppId = "org.hackeros.shell",
                        UserId = "system",
                        UserAuthority = AppAuthority.System,
                        GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                        IsSystemOperation = true
                    },
                    groupIds: ["system"],
                    DateTimeOffset.UnixEpoch));
            Assert.True(stat.Succeeded, path);
        }
    }

    [Fact]
    public async Task BootAsync_reconciles_the_host_selected_catalog()
    {
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper,
            _settings,
            _grants,
            _catalog,
            BuildKnownLazyApps.Catalog,
            new AppEnablementToFilesMigration(_fileSystem, _clock, BuildKnownLazyApps.Catalog),
            _groups,
            _users);

        await bootCoordinator.BootAsync();

        Assert.Equal(96, _catalog.SelectedManifests.Count);
        Assert.Contains(_catalog.SelectedManifests, manifest => manifest.Id == "org.hackeros.hack-paint");
    }

    [Fact]
    public async Task BootAsync_moves_persisted_disabled_entries_into_the_machine_apps_config()
    {
        _catalog.ReconcileResult =
        [
            new PersistedAppCatalogEntry(BuildKnownLazyApps.Catalog.Manifests["org.hackeros.hack-paint"], IsEnabled: false),
            new PersistedAppCatalogEntry(BuildKnownLazyApps.Catalog.Manifests["org.hackeros.terminal"], IsEnabled: false)
        ];
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper,
            _settings,
            _grants,
            _catalog,
            BuildKnownLazyApps.Catalog,
            new AppEnablementToFilesMigration(_fileSystem, _clock, BuildKnownLazyApps.Catalog),
            _groups,
            _users);

        await bootCoordinator.BootAsync();

        // ADR 0048 section 6: the machine-wide ADR 0032 disable becomes a machine uninstall; a
        // system app (the terminal) is never migrated.
        string content = await ReadSystemFileAsync("/etc/hackeros/apps.config");
        Assert.Contains("org.hackeros.hack-paint=true", content);
        Assert.DoesNotContain("org.hackeros.terminal", content);
    }

    [Fact]
    public async Task BootAsync_throws_when_policy_revision_is_invalid()
    {
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper,
            _settings,
            _grants,
            _catalog,
            _selectedCatalog,
            _enablement,
            _groups,
            _users);

        _grants.PolicyRevision = -1L;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bootCoordinator.BootAsync().AsTask());
    }

    [Fact]
    public async Task BootAsync_throws_on_cancellation()
    {
        EcosystemBootCoordinator bootCoordinator = new(
            _fsBootstrapper,
            _settings,
            _grants,
            _catalog,
            _selectedCatalog,
            _enablement,
            _groups,
            _users);

        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => bootCoordinator.BootAsync(cts.Token).AsTask());
    }

    private async Task<string> ReadSystemFileAsync(string path)
    {
        IAppFileSystemGateway gateway = new HackerOs.Platform.Core.Execution.AppFileSystemGateway(
            _fileSystem,
            _clock,
            new AppOperationContext
            {
                AppId = "org.hackeros.shell",
                UserId = "system",
                UserAuthority = AppAuthority.System,
                GrantedCapabilities = new HashSet<string>(AppCapabilities.All, StringComparer.Ordinal),
                IsSystemOperation = true
            },
            ["system"]);
        SettingsFileReadResult read = await SettingsTextFile.ReadAsync(gateway, VirtualPath.Parse(path));
        Assert.True(read.Exists, path);
        return read.Content!;
    }

    private sealed class TestJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args)
        {
            if (typeof(TValue) == typeof(IJSObjectReference))
            {
                return new ValueTask<TValue>((TValue)(object)new TestJsObjectReference());
            }

            return new ValueTask<TValue>(default(TValue)!);
        }
    }

    private static FileSystemService CreateFileSystem()
    {
        int entryId = 1;
        int transactionId = 100;
        InMemoryFileSystemRepository repository = new(
            () => FileSystemEntryId.FromGuid(new Guid(entryId++, 0, 0, new byte[8])),
            () => new Guid(transactionId++, 0, 0, new byte[8]),
            TimeProvider.System);
        FileSystemMountRouter router = new(repository);
        return new FileSystemService(
            router,
            new FileSystemPathResolver(router),
            new FileSystemAuthorizer(),
            new InMemoryTopicMessageBus(new CapabilityGrantRepository()),
            () => new Guid(transactionId++, 0, 0, new byte[8]));
    }

    private static AppCatalog CreateEmptyCatalog() =>
        Assert.IsType<AppCatalog>(AppCatalog.Build([]).Catalog);

    private sealed class TestJsObjectReference : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            new(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            new(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapabilityGrantRepository : IPersistentCapabilityGrantRepository
    {
        public long PolicyRevision { get; set; } = 1L;

        public ValueTask<long> GetCurrentPolicyRevisionAsync(CancellationToken cancellationToken = default) =>
            new(PolicyRevision);

        public ValueTask<CapabilityGrantMutationResult> GrantAsync(
            string appId,
            string userId,
            string capability,
            CapabilityGrantSource source,
            AppAuthority actingAuthority,
            IEnumerable<CapabilityConstraint>? constraints = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<CapabilityGrantMutationResult> RevokeAsync(
            CapabilityGrantId grantId,
            AppAuthority actingAuthority,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<CapabilityGrantMutationResult> ImportAsync(
            CapabilityGrantId id,
            string appId,
            string userId,
            string capability,
            CapabilityGrantSource source,
            IEnumerable<CapabilityConstraint>? constraints,
            bool isRevoked,
            AppAuthority actingAuthority,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<CapabilityPolicyEvaluation> EvaluateAsync(
            string appId,
            string userId,
            string capability,
            AppAuthority actingAuthority,
            AppAuthority requiredAuthority,
            CapabilityResourceCandidate? resourceCandidate = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeAppCatalogRepository : IPersistentAppCatalogRepository
    {
        public IReadOnlyList<AppManifest> SelectedManifests { get; private set; } = [];
        public IReadOnlyList<PersistedAppCatalogEntry> ReconcileResult { get; set; } = [];

        public ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReconcileAsync(
            IEnumerable<AppManifest> selectedManifests,
            CancellationToken cancellationToken = default)
        {
            SelectedManifests = [.. selectedManifests];
            return new(ReconcileResult);
        }

        public ValueTask<IReadOnlyList<PersistedAppCatalogEntry>> ReadAllAsync(
            CancellationToken cancellationToken = default) =>
            new(new List<PersistedAppCatalogEntry>());

        public ValueTask<bool> SetEnabledAsync(
            string appId,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeLocalGroupRepository : ILocalGroupRepository
    {
        public ValueTask<LocalGroup?> FindByNameAsync(
            LocalLoginName name,
            CancellationToken cancellationToken = default) =>
            new((LocalGroup?)null);

        public ValueTask<LocalGroup?> FindByIdAsync(
            LocalGroupId id,
            CancellationToken cancellationToken = default) =>
            new((LocalGroup?)null);

        public ValueTask<LocalGroup> CreateGroupAsync(
            LocalLoginName name,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeLocalUserRepository : ILocalUserRepository
    {
        public ValueTask<IReadOnlyList<LocalUser>> GetAllAsync(
            CancellationToken cancellationToken = default) =>
            new(new List<LocalUser>());

        public ValueTask<LocalUser?> FindByLoginNameAsync(
            LocalLoginName loginName,
            CancellationToken cancellationToken = default) =>
            new((LocalUser?)null);

        public ValueTask<LocalUser?> FindByIdAsync(
            LocalUserId id,
            CancellationToken cancellationToken = default) =>
            new((LocalUser?)null);

        public ValueTask<LocalUser> CreateUserAsync(
            LocalLoginName loginName,
            string displayName,
            AppAuthority authority,
            LocalGroupId primaryGroupId,
            IReadOnlyCollection<LocalGroupId>? additionalGroupIds = null,
            LocalPasswordCredential? credential = null,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<LocalUser> SetEnabledAsync(
            LocalUserId id,
            bool enabled,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public ValueTask<LocalUser> SetAuthorityAsync(
            LocalUserId id,
            AppAuthority authority,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
