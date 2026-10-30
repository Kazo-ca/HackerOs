using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HackerOs.App.Abstractions;
using HackerOs.AppSdk;
using HackerOs.Simulation.Abstractions.Diagnostics;
using HackerOs.Simulation.Abstractions.Events;
using HackerOs.Simulation.Abstractions.FileSystem;
using HackerOs.Simulation.Abstractions.Processes;
using HackerOs.Simulation.Abstractions.Sessions;

namespace HackerOs.Platform.WorkerHost.Protocol;

/// <summary>
/// Turns the payload of a topic message or a typed event into JSON and back when it crosses between
/// a worker and the OS (ADR 0124, section 2). Both runtimes use the same codec for a type, so what a
/// worker reads is exactly what the other side wrote.
/// </summary>
/// <remarks>
/// <para>
/// Two sources, tried in order: the event types of the OS's own contracts, written by hand like the
/// rest of the wire format (<see cref="WireCodec"/>: their constructors validate and some values,
/// such as <see cref="VirtualPath"/>, are only built through factories); then the
/// <see cref="JsonSerializerContext"/> an app assembly declares with <see cref="HackWireContextAttribute"/>,
/// whose metadata is generated at build time (no reflection-based serialization, ADR 0113).
/// </para>
/// <para>A type covered by neither cannot cross: <see cref="Unsupported"/> says what to add.</para>
/// </remarks>
internal static class WirePayloadCodec
{
    private static readonly ConcurrentDictionary<Type, Codec?> s_codecs = new();
    private static readonly ConcurrentDictionary<Type, JsonSerializerContext?> s_contexts = new();

    private static readonly Dictionary<Type, Codec> s_builtIn = new()
    {
        [typeof(FileSystemChangeEvent)] = Codec.Of<FileSystemChangeEvent>(WriteChange, ReadChange),
        [typeof(AppDisabledEvent)] = Codec.Of<AppDisabledEvent>(
            static (writer, value) => writer.WriteString("id", value.AppId),
            static element => new AppDisabledEvent(element.GetString("id"))),
        [typeof(ProcessStateChangedEvent)] = Codec.Of<ProcessStateChangedEvent>(
            static (writer, value) =>
            {
                writer.WriteNumber("pid", value.Pid.Value);
                writer.WriteNumber("o", (int)value.OldState);
                writer.WriteNumber("n", (int)value.NewState);
                WireCodec.WriteTime(writer, "at", value.AtUtc);
            },
            static element => new ProcessStateChangedEvent(
                ProcessId.FromInt64(element.GetInt64("pid")), (ProcessState)element.GetInt32("o"), (ProcessState)element.GetInt32("n"),
                WireCodec.ReadTime(element, "at"))),
        [typeof(SessionActivatedEvent)] = Codec.Of<SessionActivatedEvent>(
            static (writer, value) => WriteSession(writer, value.SessionId, value.UserId, value.AtUtc),
            static element => new SessionActivatedEvent(ReadSessionId(element), ReadUserId(element), WireCodec.ReadTime(element, "at"))),
        [typeof(SessionLoggedOutEvent)] = Codec.Of<SessionLoggedOutEvent>(
            static (writer, value) => WriteSession(writer, value.SessionId, value.UserId, value.AtUtc),
            static element => new SessionLoggedOutEvent(ReadSessionId(element), ReadUserId(element), WireCodec.ReadTime(element, "at"))),
        // ADR 0201: switching user.
        [typeof(SessionSwitchedOutEvent)] = Codec.Of<SessionSwitchedOutEvent>(
            static (writer, value) => WriteSession(writer, value.SessionId, value.UserId, value.AtUtc),
            static element => new SessionSwitchedOutEvent(ReadSessionId(element), ReadUserId(element), WireCodec.ReadTime(element, "at"))),
        [typeof(SessionResumedEvent)] = Codec.Of<SessionResumedEvent>(
            static (writer, value) => WriteSession(writer, value.SessionId, value.UserId, value.AtUtc),
            static element => new SessionResumedEvent(ReadSessionId(element), ReadUserId(element), WireCodec.ReadTime(element, "at"))),
        [typeof(SessionShutDownEvent)] = Codec.Of<SessionShutDownEvent>(
            static (writer, value) =>
            {
                if (value.SessionId is { } session)
                {
                    writer.WriteString("s", session.Value.ToString("N"));
                }

                WireCodec.WriteTime(writer, "at", value.AtUtc);
            },
            static element => new SessionShutDownEvent(
                element.GetOptionalString("s") is { } session ? SessionId.FromGuid(Guid.ParseExact(session, "N")) : null,
                WireCodec.ReadTime(element, "at"))),
        [typeof(DiagnosticLogClearedEvent)] = Codec.Of<DiagnosticLogClearedEvent>(
            static (writer, value) => WireCodec.WriteTime(writer, "at", value.AtUtc),
            static element => new DiagnosticLogClearedEvent(WireCodec.ReadTime(element, "at")))
    };

    /// <summary>Gets whether <paramref name="type"/> can cross between a worker and the OS.</summary>
    public static bool CanCarry(Type type) => Find(type) is not null;

    /// <summary>Writes <paramref name="value"/>, of type <paramref name="type"/>, as JSON.</summary>
    /// <exception cref="NotSupportedException">The type has no codec (<see cref="Unsupported"/>).</exception>
    public static string Serialize(Type type, object value) => (Find(type) ?? throw Unsupported(type)).Write(value);

    /// <summary>Rebuilds a value of type <paramref name="type"/> from the JSON <see cref="Serialize"/> wrote.</summary>
    /// <exception cref="NotSupportedException">The type has no codec (<see cref="Unsupported"/>).</exception>
    public static object Deserialize(Type type, string json) => (Find(type) ?? throw Unsupported(type)).Read(json);

    /// <summary>The error of a payload type that cannot cross: it names the type and the attribute to add.</summary>
    public static NotSupportedException Unsupported(Type type) =>
        new($"'{type.FullName}' cannot cross between a worker and the OS: it is neither an event type of the OS " +
            "nor declared by a JsonSerializerContext of its assembly. Add [JsonSerializable(typeof(" + type.Name + "))] " +
            "to a context and [assembly: HackWireContext(typeof(TheContext))] to the assembly (ADR 0124), " +
            "or declare [AppManifest(Execution = AppExecutionHost.MainThread)].");

    /// <summary>Parses a topic name a worker sent, validating every segment again (a worker cannot forge one).</summary>
    /// <exception cref="ArgumentException">The text is not a valid topic name.</exception>
    public static TopicName ParseTopic(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string[] parts = value.Split('/');
        if (parts.Length < 3)
        {
            throw new ArgumentException($"'{value}' is not a topic name.", nameof(value));
        }

        TopicNameBuilder builder = parts[0] switch
        {
            "app" => TopicNames.ForApp(parts[1]),
            "shared" => TopicNames.Shared(parts[1]),
            _ => throw new ArgumentException($"'{value}' is not a topic name.", nameof(value))
        };
        foreach (string segment in parts.AsSpan(2))
        {
            builder.Segment(segment);
        }

        return builder.Build();
    }

    private static Codec? Find(Type type) => s_codecs.GetOrAdd(type, Create);

    private static Codec? Create(Type type)
    {
        if (s_builtIn.TryGetValue(type, out Codec? builtIn))
        {
            return builtIn;
        }

        foreach (HackWireContextAttribute declared in type.Assembly.GetCustomAttributes<HackWireContextAttribute>())
        {
            if (Context(declared) is { } context && context.GetTypeInfo(type) is { } info)
            {
                return new Codec(
                    value => JsonSerializer.Serialize(value, info),
                    json => JsonSerializer.Deserialize(json, info) ?? throw new JsonException($"The payload of type '{type.FullName}' is null."));
            }
        }

        return null;
    }

    /// <summary>Creates the declared context once; a type that is not a context is ignored, like a missing one.</summary>
    private static JsonSerializerContext? Context(HackWireContextAttribute declared) =>
        s_contexts.GetOrAdd(declared.ContextType, _ =>
            typeof(JsonSerializerContext).IsAssignableFrom(declared.ContextType) && !declared.ContextType.IsAbstract
                ? Activator.CreateInstance(declared.ContextType) as JsonSerializerContext
                : null);

    private static void WriteChange(Utf8JsonWriter writer, FileSystemChangeEvent change)
    {
        writer.WriteString("p", change.Path.Value);
        writer.WriteNumber("k", (int)change.Kind);
        writer.WriteNumber("ek", (int)change.EntryKind);
        writer.WriteNumber("r", change.Revision);
        WireCodec.WriteTime(writer, "at", change.OccurredAtUtc);
        if (change.MovedToPath is { } moved)
        {
            writer.WriteString("mt", moved.Value);
        }
    }

    private static FileSystemChangeEvent ReadChange(JsonElement element) => new(
        WireCodec.ReadPath(element, "p"),
        (FileSystemChangeKind)element.GetInt32("k"),
        (FileSystemEntryKind)element.GetInt32("ek"),
        element.GetInt64("r"),
        WireCodec.ReadTime(element, "at"),
        WireCodec.ReadOptionalPath(element, "mt"));

    private static void WriteSession(Utf8JsonWriter writer, SessionId session, LocalUserId user, DateTimeOffset at)
    {
        writer.WriteString("s", session.Value.ToString("N"));
        writer.WriteString("u", user.Value.ToString("N"));
        WireCodec.WriteTime(writer, "at", at);
    }

    private static SessionId ReadSessionId(JsonElement element) => SessionId.FromGuid(Guid.ParseExact(element.GetString("s"), "N"));

    private static LocalUserId ReadUserId(JsonElement element) => LocalUserId.FromGuid(Guid.ParseExact(element.GetString("u"), "N"));

    /// <summary>The two directions of one payload type.</summary>
    private sealed class Codec(Func<object, string> write, Func<string, object> read)
    {
        public Func<object, string> Write { get; } = write;

        public Func<string, object> Read { get; } = read;

        /// <summary>A hand-written codec: one JSON object written and read with the wire format's helpers.</summary>
        public static Codec Of<T>(Action<Utf8JsonWriter, T> write, Func<JsonElement, T> read) where T : notnull => new(
            value =>
            {
                ArrayBufferWriter<byte> buffer = new(128);
                using (Utf8JsonWriter writer = new(buffer))
                {
                    writer.WriteStartObject();
                    write(writer, (T)value);
                    writer.WriteEndObject();
                }

                return Encoding.UTF8.GetString(buffer.WrittenSpan);
            },
            json =>
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return read(document.RootElement);
            });
    }
}
