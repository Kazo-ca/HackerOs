using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Windowing.Core.Tests;

/// <summary>ADR 0117 §1 and §3: the window manager cleans and shows the title an app writes.</summary>
public sealed class WindowTitleTests
{
    [Fact]
    public void SetTitle_replaces_the_title_and_reports_the_change()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        List<WindowEvent> applied = [];
        runtime.EventsApplied += events => applied.AddRange(events);

        IReadOnlyList<WindowEvent> events = runtime.SetTitle(window.Id, "photo.png — Hack Paint");

        WindowTitleChangedEvent changed = Assert.IsType<WindowTitleChangedEvent>(Assert.Single(events));
        Assert.Equal("Test 1", changed.PreviousTitle);
        Assert.Equal("photo.png — Hack Paint", changed.Title);
        Assert.Equal("photo.png — Hack Paint", Assert.Single(runtime.Windows).Title);
        Assert.Equal(events, applied);
    }

    [Fact]
    public void An_unchanged_title_produces_no_event_and_no_state_change()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.SetTitle(window.Id, "notes.txt — Text Editor");
        int changes = 0;
        runtime.StateChanged += () => changes++;

        Assert.Empty(runtime.SetTitle(window.Id, " notes.txt — Text Editor\n"));
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Only_the_named_window_changes()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState first = CreateState(1);
        WindowRuntimeState second = CreateState(2);
        runtime.Apply(new CreateWindowCommand(first));
        runtime.Apply(new CreateWindowCommand(second));

        runtime.SetTitle(first.Id, "a.png — Hack Paint");

        Assert.Equal("a.png — Hack Paint", runtime.Windows.Single(window => window.Id == first.Id).Title);
        Assert.Equal("Test 2", runtime.Windows.Single(window => window.Id == second.Id).Title);
    }

    [Fact]
    public void A_title_change_keeps_geometry_focus_and_state()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));
        runtime.Apply(new MaximizeWindowCommand(window.Id));
        WindowRuntimeState before = Assert.Single(runtime.Windows);

        runtime.SetTitle(window.Id, "Renamed");

        WindowRuntimeState after = Assert.Single(runtime.Windows);
        Assert.Equal("Renamed", after.Title);
        Assert.Equal(before.Bounds, after.Bounds);
        Assert.Equal(before.RestoreBounds, after.RestoreBounds);
        Assert.Equal(before.VisualState, after.VisualState);
        Assert.Equal(before.ZOrder, after.ZOrder);
        Assert.Equal(before.IsFocused, after.IsFocused);
    }

    [Fact]
    public void An_empty_title_or_an_unknown_window_is_refused()
    {
        WindowRuntime runtime = new(new WindowBounds(0, 0, 1280, 720));
        WindowRuntimeState window = CreateState(1);
        runtime.Apply(new CreateWindowCommand(window));

        Assert.Throws<ArgumentException>(() => runtime.SetTitle(window.Id, " \t\r\n"));
        Assert.Throws<KeyNotFoundException>(() => runtime.SetTitle(CreateState(9).Id, "Ghost"));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  notes.txt — Text Editor  ", "notes.txt — Text Editor")]
    [InlineData("line one\nline two\t\u001b[31m", "line oneline two[31m")]
    [InlineData("\u0007*photo.png — Hack Paint", "*photo.png — Hack Paint")]
    public void Sanitize_removes_control_characters_and_trims(string? title, string expected) =>
        Assert.Equal(expected, WindowTitles.Sanitize(title));

    [Fact]
    public void Sanitize_cuts_long_titles_with_an_ellipsis()
    {
        string cleaned = WindowTitles.Sanitize(new string('a', 400));

        Assert.Equal(WindowTitles.MaxLength, cleaned.Length);
        Assert.EndsWith(WindowTitles.Ellipsis.ToString(), cleaned, StringComparison.Ordinal);
        Assert.Equal(new string('a', 256), WindowTitles.Sanitize(new string('a', 256)));
    }

    [Fact]
    public void Sanitize_never_splits_a_surrogate_pair()
    {
        // 254 letters then emoji: the cut falls inside the first emoji's pair.
        string title = new string('a', 254) + string.Concat(Enumerable.Repeat("😀", 10));

        string cleaned = WindowTitles.Sanitize(title);

        Assert.False(char.IsHighSurrogate(cleaned[^2]));
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
        Assert.True(cleaned.Length <= WindowTitles.MaxLength);
    }

    [Fact]
    public void SanitizeOrDefault_restores_the_default_for_an_empty_title()
    {
        Assert.Equal("Text Editor", WindowTitles.SanitizeOrDefault(null, "Text Editor"));
        Assert.Equal("Text Editor", WindowTitles.SanitizeOrDefault("\n", "Text Editor"));
        Assert.Equal("a.txt — Text Editor", WindowTitles.SanitizeOrDefault("a.txt — Text Editor", "Text Editor"));
    }

    private static WindowRuntimeState CreateState(int seed) =>
        new(
            WindowId.FromGuid(Guid.Parse($"10000000-0000-0000-0000-{seed:D12}")),
            $"org.hackeros.test{seed}",
            WindowOwnerId.FromGuid(Guid.Parse($"20000000-0000-0000-0000-{seed:D12}")),
            $"Test {seed}",
            null,
            new WindowBounds(20 * seed, 30 * seed, 640, 480),
            null,
            0,
            WindowVisualState.Normal,
            new WindowConstraints(true, 320, 240, 1200, 900));
}
