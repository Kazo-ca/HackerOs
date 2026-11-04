using Kazo.Os.Platform.Blazor.Windows;
using Kazo.Os.Platform.Core.Settings;
using Kazo.Os.Simulation.Abstractions;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Tests;

public sealed class WindowGeometryPersistenceTests
{
    private const string AppId = "org.hackeros.files";

    [Fact]
    public void Geometry_lives_in_device_local_state_not_in_config()
    {
        OsSettingsFile file = WindowGeometrySettings.GetFile(AppId);

        Assert.Equal("/home/alice/.local/state/hackeros/windows/org.hackeros.files.config", file.Resolve("/home/alice").Value);
        Assert.Throws<ArgumentException>(() => WindowGeometrySettings.GetFile("../escape"));
    }

    [Fact]
    public async Task Clean_profile_restores_nothing_then_saved_geometry_round_trips()
    {
        FakeOsSettingsService settings = new();
        WindowGeometryPersistence persistence = new(settings);

        Assert.Null(await persistence.RestoreAsync(AppId));
        Assert.True(await persistence.SaveAsync(AppId, new SavedWindowGeometry(640, 480, IsMaximized: false)));

        Assert.Equal(new SavedWindowGeometry(640, 480, false), await persistence.RestoreAsync(AppId));
        Assert.Equal(
            "# Window size of org.hackeros.files (ADR 0116, ADR 0122)\nwidth=640\nheight=480\nmaximized=false\n",
            settings.Files["windows/org.hackeros.files.config"]);
    }

    [Fact]
    public async Task Maximized_state_round_trips_with_the_restore_geometry()
    {
        FakeOsSettingsService settings = new();
        WindowGeometryPersistence persistence = new(settings);

        await persistence.SaveAsync(AppId, new SavedWindowGeometry(900, 600, IsMaximized: true));

        Assert.Equal(new SavedWindowGeometry(900, 600, true), await persistence.RestoreAsync(AppId));
    }

    [Fact]
    public async Task Unchanged_geometry_is_not_rewritten()
    {
        FakeOsSettingsService settings = new();
        WindowGeometryPersistence persistence = new(settings);
        SavedWindowGeometry geometry = new(400, 300, false);

        Assert.True(await persistence.SaveAsync(AppId, geometry));
        Assert.False(await persistence.SaveAsync(AppId, geometry));
        Assert.Equal(1, settings.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("x=1\ny=2\nwidth=3\n")]
    [InlineData("x=1\ny=2\nwidth=-3\nheight=4\n")]
    [InlineData("width=one\nheight=4\n")]
    [InlineData("maximized=true\n")]
    public void Partial_or_invalid_file_means_default_placement(string content)
    {
        Assert.Null(WindowGeometrySettings.Parse(content));
    }

    [Theory]
    [InlineData("x=1\ny=2\nwidth=3\nheight=4\n", false)]
    [InlineData("x=1\ny=2\nwidth=3\nheight=4\nmaximized=true\n", true)]
    [InlineData("x=1\ny=2\nwidth=3\nheight=4\nmaximized=yes\n", false)]
    [InlineData("width=3\nheight=4\nmaximized=true\n", true)]
    public void Maximized_is_optional_and_only_a_literal_true_maximizes(string content, bool maximized)
    {
        // Files written before ADR 0122 (with x= and y=) still restore their size; the position is ignored.
        Assert.Equal(new SavedWindowGeometry(3, 4, maximized), WindowGeometrySettings.Parse(content));
    }

    [Fact]
    public async Task Save_without_a_session_is_best_effort()
    {
        FakeOsSettingsService settings = new() { NoSession = true };
        WindowGeometryPersistence persistence = new(settings);

        Assert.False(await persistence.SaveAsync(AppId, new SavedWindowGeometry(100, 100, false)));
    }

    /// <summary>Whole-file part of <see cref="IOsSettingsService"/> over a dictionary keyed by relative path.</summary>
    internal sealed class FakeOsSettingsService : IOsSettingsService
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public bool NoSession { get; init; }

        public int Writes { get; private set; }

        public event EventHandler<OsSettingsChangedEventArgs>? DocumentChanged { add { } remove { } }

        public string? CurrentHomeDirectory => NoSession ? null : "/home/alice";

        public ValueTask<string?> ReadFileAsync(OsSettingsFile file, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Files.GetValueOrDefault(file.RelativePath));

        public ValueTask WriteFileAsync(OsSettingsFile file, string content, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default)
        {
            if (NoSession)
            {
                throw new InvalidOperationException("A user settings file needs an active session.");
            }

            Files[file.RelativePath] = content;
            Writes++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<LayeredConfigSnapshot> ReadAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetAsync(OsSettingsDocument document, string qualifiedKey, string value, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetManyAsync(OsSettingsDocument document, IReadOnlyList<KeyValuePair<string, string>> values, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask ResetAsync(OsSettingsDocument document, string qualifiedKey, SettingLayer target = SettingLayer.User, IAppFileSystemGateway? writer = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string> ReadValidatedFileAsync(OsSettingsFile file, ISettingsDocumentValidator validator, string defaultContent, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask WatchAsync(OsSettingsDocument document, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
