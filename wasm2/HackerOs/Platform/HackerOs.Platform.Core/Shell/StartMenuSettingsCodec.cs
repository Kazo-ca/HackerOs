using System.Text;
using System.Text.Json;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.Core.Shell;

/// <summary>Converts the strictly validated start-menu document to immutable in-memory profiles.</summary>
internal static class StartMenuSettingsCodec
{
    internal static bool TryDecode(
        string content,
        out Dictionary<LocalUserId, IReadOnlyList<string>> profiles)
    {
        profiles = [];
        if (new StartMenuSettingsValidator().Validate(content).Count != 0)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            foreach (JsonProperty profile in document.RootElement.GetProperty("profiles").EnumerateObject())
            {
                Guid.TryParseExact(profile.Name, "N", out Guid userGuid);
                LocalUserId userId = LocalUserId.FromGuid(userGuid);
                string[] pinnedAppIds = profile.Value
                    .GetProperty("pinnedAppIds")
                    .EnumerateArray()
                    .Select(element => element.GetString()!)
                    .ToArray();
                profiles.Add(userId, Array.AsReadOnly(pinnedAppIds));
            }

            return true;
        }
        catch (JsonException)
        {
            profiles = [];
            return false;
        }
    }

    /// <summary>Reads each profile's <c>defaultPinsApplied</c> (ADR 0212 §6); a profile without it gives an empty list.</summary>
    internal static Dictionary<LocalUserId, IReadOnlyList<string>> DecodeDefaultPinsApplied(string content)
    {
        Dictionary<LocalUserId, IReadOnlyList<string>> applied = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            foreach (JsonProperty profile in document.RootElement.GetProperty("profiles").EnumerateObject())
            {
                Guid.TryParseExact(profile.Name, "N", out Guid userGuid);
                applied[LocalUserId.FromGuid(userGuid)] = profile.Value.TryGetProperty(StartMenuSettingsDocuments.DefaultPinsAppliedProperty, out JsonElement list)
                    && list.ValueKind == JsonValueKind.Array
                        ? Array.AsReadOnly(list.EnumerateArray().Select(static element => element.GetString() ?? string.Empty).ToArray())
                        : Array.AsReadOnly(Array.Empty<string>());
            }
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            applied.Clear();
        }

        return applied;
    }

    internal static string Encode(
        IReadOnlyDictionary<LocalUserId, IReadOnlyList<string>> profiles,
        IReadOnlyDictionary<LocalUserId, IReadOnlyList<string>>? defaultPinsApplied = null)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteStartObject("profiles");

            IEnumerable<LocalUserId> users = profiles.Keys.Concat(defaultPinsApplied?.Keys ?? []).Distinct();
            foreach (LocalUserId userId in users.OrderBy(user => user.ToString(), StringComparer.Ordinal))
            {
                writer.WriteStartObject(userId.ToString());
                writer.WriteStartArray("pinnedAppIds");
                foreach (string appId in profiles.TryGetValue(userId, out IReadOnlyList<string>? pinnedAppIds) ? pinnedAppIds : [])
                {
                    writer.WriteStringValue(appId);
                }

                writer.WriteEndArray();
                if (defaultPinsApplied is not null
                    && defaultPinsApplied.TryGetValue(userId, out IReadOnlyList<string>? applied)
                    && applied.Count > 0)
                {
                    writer.WriteStartArray(StartMenuSettingsDocuments.DefaultPinsAppliedProperty);
                    foreach (string appId in applied)
                    {
                        writer.WriteStringValue(appId);
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
