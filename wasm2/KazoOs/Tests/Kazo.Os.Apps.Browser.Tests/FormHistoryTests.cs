using Kazo.Os.Apps.Browser.Storage;
using Xunit;

namespace Kazo.Os.Apps.Browser.Tests;

/// <summary>The browser's form history (ADR 0088 §6): an OS file, offered back per field name.</summary>
public sealed class FormHistoryTests
{
    private const string Path = "/home/alice/.local/share/hackeros/browser/form-history.json";

    private sealed class MemoryFiles : IBrowserStorageFiles
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

        public Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(Files.TryGetValue(path, out string? text) ? text : null);

        public Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default)
        {
            Files[path] = text;
            return Task.CompletedTask;
        }
    }

    private static KeyValuePair<string, string> Field(string name, string value) => new(name, value);

    [Fact]
    public void The_file_lives_in_the_browser_profile_of_the_user()
    {
        Assert.Equal(Path, FormHistory.GetPath("/home/alice/"));
    }

    [Fact]
    public async Task Submitted_values_come_back_for_the_same_field_name_most_used_first()
    {
        MemoryFiles files = new();
        DateTimeOffset now = new(2026, 10, 17, 1, 0, 0, TimeSpan.Zero);
        FormHistory history = new(files, Path, () => now = now.AddMinutes(1));

        await history.RecordAsync([Field("q", "nmap"), Field("email", "alice@hack")]);
        await history.RecordAsync([Field("q", "ssh")]);
        await history.RecordAsync([Field("q", "nmap")]);

        Assert.Equal(["nmap", "ssh"], await history.SuggestAsync("q"));
        Assert.Equal(["alice@hack"], await history.SuggestAsync("email"));
        Assert.Empty(await history.SuggestAsync("other"));
        Assert.Contains("\"timesUsed\": 2", files.Files[Path], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_history_survives_a_new_browser_and_can_be_cleared()
    {
        MemoryFiles files = new();
        await new FormHistory(files, Path).RecordAsync([Field("title", "Hello")]);

        FormHistory reopened = new(files, Path);
        Assert.Equal(["Hello"], await reopened.SuggestAsync("title"));
        await reopened.ClearAsync();

        Assert.Empty(await new FormHistory(files, Path).SuggestAsync("title"));
    }

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("5500-0000-0000-0004")]
    [InlineData("   ")]
    [InlineData("")]
    public void Card_numbers_and_blank_values_are_never_kept(string value)
    {
        Assert.False(FormHistory.IsRememberable(value));
    }

    [Theory]
    [InlineData("4111 1111 1111 1112")]
    [InlineData("555-0100")]
    [InlineData("alice")]
    public void Ordinary_values_are_kept(string value)
    {
        Assert.True(FormHistory.IsRememberable(value));
    }

    [Fact]
    public async Task A_field_keeps_its_most_used_values_up_to_the_limit()
    {
        MemoryFiles files = new();
        FormHistory history = new(files, Path);
        await history.RecordAsync([Field("q", "favorite")]);
        await history.RecordAsync([Field("q", "favorite")]);
        await history.RecordAsync(Enumerable.Range(0, FormHistory.MaxValuesPerField + 10).Select(i => Field("q", $"value {i}")));

        IReadOnlyList<string> suggestions = await history.SuggestAsync("q");
        int kept = System.Text.RegularExpressions.Regex.Count(files.Files[Path], "\"field\": \"q\"");

        Assert.Equal("favorite", suggestions[0]);
        Assert.Equal(FormHistory.MaxSuggestions, suggestions.Count);
        Assert.Equal(FormHistory.MaxValuesPerField, kept);
    }

    [Fact]
    public async Task A_broken_file_is_reported_and_replaced()
    {
        MemoryFiles files = new();
        files.Files[Path] = "{ not json";
        List<string> errors = [];
        FormHistory history = new(files, Path, onError: errors.Add);

        Assert.Empty(await history.SuggestAsync("q"));
        await history.RecordAsync([Field("q", "fresh")]);

        Assert.Single(errors);
        Assert.Equal(["fresh"], await new FormHistory(files, Path).SuggestAsync("q"));
    }
}
