using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Kazo.Os.Platform.WorkerHost.Protocol;

/// <summary>
/// The message types of the worker protocol (ADR 0070, section 4). Every message is one JSON object
/// whose <c>t</c> property is one of these; the pipe keeps them in order in each direction.
/// </summary>
internal static class WorkerMessageType
{
    // ---- OS → worker ------------------------------------------------------------------------

    /// <summary>First message: worker number, heartbeat period, whether to meter CPU.</summary>
    public const string Init = "init";

    /// <summary>Run one process: its identity snapshot, entry point, streams and environment.</summary>
    public const string Start = "start";

    /// <summary>Reply to a <see cref="Call"/>: <c>v</c> (value) or <c>e</c> (error).</summary>
    public const string Result = "result";

    /// <summary><c>SIGTERM</c>: raise <c>StopRequested</c>, then cancel the process token (ADR 0069).</summary>
    public const string Stop = "stop";

    /// <summary><c>SIGKILL</c> while the worker still answers (escalation step 3): drop the process.</summary>
    public const string Kill = "kill";

    /// <summary>
    /// An intent for an already running process <c>p</c> (ADR 0096, section 5): the second activation
    /// of a single-instance service, raised as <c>IntentReceived</c>.
    /// </summary>
    public const string Intent = "intent";

    /// <summary>Run <c>OnStoppingAsync</c> of a hosted service (logout, shutdown, disable).</summary>
    public const string ServiceStop = "sstop";

    /// <summary>Reply to a heartbeat: the simulated clock now, so the worker's clock stays in step.</summary>
    public const string Clock = "clock";

    /// <summary>A permission-class error the OS raised on the process's gateways.</summary>
    public const string PermissionDenied = "perm";

    /// <summary>The OS wrote <c>n</c> characters of a process's output: flow-control credit.</summary>
    public const string OutputAck = "ack";

    /// <summary>The reader of a process's stream <c>f</c> went away (broken pipe).</summary>
    public const string PipeClosed = "epipe";

    /// <summary>Ctrl+C: cancel the process token, without a stop request.</summary>
    public const string Interrupt = "int";

    /// <summary>
    /// An HTTP request for a listening socket <c>sk</c> of the process (ADR 0074, section 2): the
    /// exchange <c>ex</c>, answered by an <see cref="HttpResponse"/>.
    /// </summary>
    public const string HttpRequest = "hreq";

    /// <summary>
    /// One delivery of the subscription <c>s</c> of process <c>p</c> (ADR 0124, section 1): the payload
    /// <c>j</c> as JSON, and for a topic message its topic <c>top</c>, publisher <c>pa</c> and time <c>at</c>.
    /// The worker answers with an <c>ev.ack</c> note once it delivered it.
    /// </summary>
    public const string Event = "ev";

    /// <summary>
    /// A progress report <c>f</c>, <c>m</c> of the background task <c>j</c> that process <c>p</c> started
    /// from its worker with a <c>bg.run</c> call (ADR 0128, section 6).
    /// </summary>
    public const string BackgroundJobProgress = "bgp";

    /// <summary>
    /// An input <c>d</c> (a JSON object) for the window process <c>p</c> rendered in this worker (ADR
    /// 0129 trial, ADR 0133): a DOM event, the answer to a dialog, the window's close. Opaque to the
    /// worker host; read by the process's <see cref="Worker.IWorkerWindowRunner"/>.
    /// </summary>
    public const string UiInput = "uin";

    // ---- worker → OS ------------------------------------------------------------------------

    /// <summary>The worker's .NET side is up and its assemblies are loaded.</summary>
    public const string Hello = "hello";

    /// <summary>Every 250 ms: CPU time per process, runtime memory (ADR 0070, section 5).</summary>
    public const string Heartbeat = "hb";

    /// <summary>A gateway call that needs an answer.</summary>
    public const string Call = "call";

    /// <summary>A gateway call with no answer (log line, environment change): kept in order with the others.</summary>
    public const string Notify = "note";

    /// <summary>Abandon a pending <see cref="Call"/> (its token was cancelled).</summary>
    public const string Cancel = "cancel";

    /// <summary>Text written to standard output (<c>f</c> = 1) or error (<c>f</c> = 2).</summary>
    public const string Output = "out";

    /// <summary>The process ended: status and exit code.</summary>
    public const string Exit = "exit";

    /// <summary>The process was dropped after a <see cref="Kill"/>: the worker is still healthy.</summary>
    public const string KillAck = "killed";

    /// <summary>The work deferred on a stop request ended (ADR 0069, <c>Defer</c>).</summary>
    public const string StopDone = "stopdone";

    /// <summary>A hosted service finished its <c>OnStoppingAsync</c> after a <see cref="ServiceStop"/>.</summary>
    public const string ServiceStopped = "sstopped";

    /// <summary>A diagnostic line for <c>/var/log/syslog</c>.</summary>
    public const string Log = "log";

    /// <summary>The reply <c>r</c> of the process to the exchange <c>x</c> of its socket <c>sk</c>.</summary>
    public const string HttpResponse = "hres";

    /// <summary>
    /// An output <c>d</c> (a JSON object) of the window process <c>p</c> (ADR 0129 trial): a batch of
    /// DOM changes, a dialog request. Opaque to the worker host; read by the window's host on the page.
    /// </summary>
    public const string UiOutput = "ui";
}

/// <summary>Writes and reads protocol messages: compact JSON, UTF-16 strings on the pipe.</summary>
internal static class WorkerMessage
{
    /// <summary>Builds one message of type <paramref name="type"/>.</summary>
    /// <param name="type">One of <see cref="WorkerMessageType"/>.</param>
    /// <param name="body">Writes the other properties of the object.</param>
    /// <returns>The JSON text.</returns>
    public static string Create(string type, Action<Utf8JsonWriter>? body = null)
    {
        ArrayBufferWriter<byte> buffer = new(256);
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("t", type);
            body?.Invoke(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Parses one message; the caller disposes the document.</summary>
    /// <param name="message">The JSON text.</param>
    /// <param name="type">The message type.</param>
    /// <returns>The parsed document.</returns>
    /// <exception cref="JsonException">The text is not a protocol message.</exception>
    public static JsonDocument Parse(string message, out string type)
    {
        JsonDocument document = JsonDocument.Parse(message);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("t", out JsonElement typeElement)
            || typeElement.GetString() is not { } value)
        {
            document.Dispose();
            throw new JsonException("Not a worker protocol message.");
        }

        type = value;
        return document;
    }

    /// <summary>Gets a required 64-bit integer property.</summary>
    public static long GetInt64(this JsonElement element, string name) => element.GetProperty(name).GetInt64();

    /// <summary>Gets a required 32-bit integer property.</summary>
    public static int GetInt32(this JsonElement element, string name) => element.GetProperty(name).GetInt32();

    /// <summary>Gets a required number property.</summary>
    public static double GetDouble(this JsonElement element, string name) => element.GetProperty(name).GetDouble();

    /// <summary>Gets a required string property.</summary>
    public static string GetString(this JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new JsonException($"'{name}' is null.");

    /// <summary>Gets an optional string property.</summary>
    public static string? GetOptionalString(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Gets an optional boolean property (false when absent).</summary>
    public static bool GetFlag(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Gets an optional property, or <see langword="null"/> when absent or JSON null.</summary>
    public static JsonElement? GetOptional(this JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;
}
