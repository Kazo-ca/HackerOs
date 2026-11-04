using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kazo.Os.Apps.Browser.Storage;

/// <summary>
/// The browser's form history (ADR 0073 §3, ADR 0088 §6), like Firefox's <c>formhistory.sqlite</c>: what
/// the user typed in submitted form fields, by field name, for every site, kept in the user's files at
/// <c>~/.local/share/hackeros/browser/form-history.json</c>. The page bridge offers it back as a
/// <c>&lt;datalist&gt;</c> when a field of the same name gets the focus.
/// </summary>
/// <remarks>
/// <para>
/// A field name keeps at most <see cref="MaxValuesPerField"/> values and the file at most
/// <see cref="MaxEntries"/>: the least used, then the oldest, go first. Values longer than
/// <see cref="MaxValueLength"/> characters and payment card numbers are never kept.
/// </para>
/// <para>The file is read once, then written after each change (coalesced, as <see cref="PageStorage"/>).</para>
/// </remarks>
public sealed class FormHistory
{
    /// <summary>Name of the file under the browser's profile directory.</summary>
    public const string FileName = "form-history.json";

    /// <summary>Most values kept for one field name.</summary>
    public const int MaxValuesPerField = 100;

    /// <summary>Most values kept in all.</summary>
    public const int MaxEntries = 2000;

    /// <summary>Longest value kept, in characters.</summary>
    public const int MaxValueLength = 200;

    /// <summary>Most suggestions offered for one field.</summary>
    public const int MaxSuggestions = 20;

    private readonly IBrowserStorageFiles _files;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<string>? _onError;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<FormHistoryEntry>? _entries;

    /// <summary>Initializes the history kept in <paramref name="path"/>.</summary>
    /// <param name="files">File access.</param>
    /// <param name="path">The history file (<see cref="GetPath"/>).</param>
    /// <param name="clock">The time of each use; the real time when omitted.</param>
    /// <param name="onError">Called with a message when the file cannot be read or written.</param>
    public FormHistory(IBrowserStorageFiles files, string path, Func<DateTimeOffset>? clock = null, Action<string>? onError = null)
    {
        _files = files ?? throw new ArgumentNullException(nameof(files));
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = path;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
        _onError = onError;
    }

    /// <summary>Gets the history file.</summary>
    public string Path { get; }

    /// <summary>Gets the history file of a user: <c>~/.local/share/hackeros/browser/form-history.json</c>.</summary>
    /// <param name="homeDirectory">The user's home directory.</param>
    public static string GetPath(string homeDirectory) => $"{homeDirectory.TrimEnd('/')}/.local/share/hackeros/browser/{FileName}";

    /// <summary>
    /// Whether a submitted value may be kept: not empty, not too long, and not a payment card number
    /// (13 to 19 digits that pass the Luhn check, spaces and dashes ignored), as browsers refuse to keep them.
    /// </summary>
    /// <param name="value">The value.</param>
    public static bool IsRememberable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength)
        {
            return false;
        }

        string digits = new([.. value.Where(c => c is not (' ' or '-'))]);
        return !(digits.Length is >= 13 and <= 19 && digits.All(char.IsAsciiDigit) && PassesLuhn(digits));
    }

    /// <summary>Returns the values typed in fields named <paramref name="fieldName"/>, most used first.</summary>
    /// <param name="fieldName">The field's <c>name</c>.</param>
    /// <param name="cancellationToken">Cancels the first read of the file.</param>
    public async Task<IReadOnlyList<string>> SuggestAsync(string fieldName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(fieldName))
        {
            return [];
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<FormHistoryEntry> entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            return [.. entries
                .Where(entry => string.Equals(entry.Field, fieldName, StringComparison.Ordinal))
                .OrderByDescending(entry => entry.TimesUsed)
                .ThenByDescending(entry => entry.LastUsed)
                .Take(MaxSuggestions)
                .Select(entry => entry.Value)];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Remembers the fields of a submitted form, then saves the file.</summary>
    /// <param name="fields">Field name and value pairs; the ones that may not be kept are skipped.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>How many values were recorded.</returns>
    public async Task<int> RecordAsync(IEnumerable<KeyValuePair<string, string>> fields, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fields);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<FormHistoryEntry> entries = await LoadAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = _clock();
            int recorded = 0;
            foreach ((string field, string rawValue) in fields)
            {
                string value = rawValue?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(field) || field.Length > MaxValueLength || !IsRememberable(value))
                {
                    continue;
                }

                int index = entries.FindIndex(entry => entry.Field == field && entry.Value == value);
                entries.Add(index < 0
                    ? new FormHistoryEntry(field, value, 1, now, now)
                    : entries[index] with { TimesUsed = entries[index].TimesUsed + 1, LastUsed = now });
                if (index >= 0)
                {
                    entries.RemoveAt(index);
                }

                recorded++;
            }

            if (recorded == 0)
            {
                return 0;
            }

            Trim(entries);
            await SaveAsync(entries).ConfigureAwait(false);
            return recorded;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets every value ("Clear browsing data → Form history").</summary>
    /// <param name="cancellationToken">Cancels the call.</param>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries = [];
            await SaveAsync(_entries).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Trim(List<FormHistoryEntry> entries)
    {
        // Least used first, then oldest: what Firefox expires first.
        foreach (IGrouping<string, FormHistoryEntry> field in entries.GroupBy(entry => entry.Field, StringComparer.Ordinal).ToList())
        {
            foreach (FormHistoryEntry extra in field.OrderBy(entry => entry.TimesUsed).ThenBy(entry => entry.LastUsed).Take(Math.Max(0, field.Count() - MaxValuesPerField)).ToList())
            {
                entries.Remove(extra);
            }
        }

        foreach (FormHistoryEntry extra in entries.OrderBy(entry => entry.TimesUsed).ThenBy(entry => entry.LastUsed).Take(Math.Max(0, entries.Count - MaxEntries)).ToList())
        {
            entries.Remove(extra);
        }
    }

    private async Task<List<FormHistoryEntry>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return _entries;
        }

        List<FormHistoryEntry> loaded = [];
        try
        {
            string? json = await _files.ReadTextAsync(Path, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json)
                && JsonSerializer.Deserialize(json, FormHistoryJsonContext.Default.FormHistoryDocument) is { } document)
            {
                loaded = [.. document.Entries.Where(entry => !string.IsNullOrEmpty(entry.Field) && IsRememberable(entry.Value))];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _onError?.Invoke($"The form history could not be read: {exception.Message}");
        }

        _entries = loaded;
        return loaded;
    }

    private async Task SaveAsync(List<FormHistoryEntry> entries)
    {
        try
        {
            string json = JsonSerializer.Serialize(new FormHistoryDocument(1, [.. entries]), FormHistoryJsonContext.Default.FormHistoryDocument);
            await _files.WriteTextAsync(Path, json).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _onError?.Invoke($"The form history could not be saved: {exception.Message}");
        }
    }

    private static bool PassesLuhn(string digits)
    {
        int sum = 0;
        bool doubled = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int digit = digits[i] - '0';
            if (doubled)
            {
                digit *= 2;
                if (digit > 9)
                {
                    digit -= 9;
                }
            }

            sum += digit;
            doubled = !doubled;
        }

        return sum % 10 == 0;
    }
}

/// <summary>One remembered value.</summary>
/// <param name="Field">The field's <c>name</c>.</param>
/// <param name="Value">What was typed.</param>
/// <param name="TimesUsed">How many submissions carried it.</param>
/// <param name="FirstUsed">First submission.</param>
/// <param name="LastUsed">Latest submission.</param>
public sealed record FormHistoryEntry(string Field, string Value, int TimesUsed, DateTimeOffset FirstUsed, DateTimeOffset LastUsed);

/// <summary>The file: a version and the entries.</summary>
/// <param name="Version">Format version (1).</param>
/// <param name="Entries">The entries.</param>
public sealed record FormHistoryDocument(int Version, List<FormHistoryEntry> Entries);

/// <summary>Source-generated JSON of the history file.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(FormHistoryDocument))]
internal sealed partial class FormHistoryJsonContext : JsonSerializerContext;
