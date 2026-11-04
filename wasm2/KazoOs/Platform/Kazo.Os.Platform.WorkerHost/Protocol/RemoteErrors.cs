using System.Text.Json;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.WorkerHost.Protocol;

/// <summary>
/// Carries an exception thrown by a gateway on the OS side back to the worker, as the closest
/// exception of the same family (ADR 0070, section 4): the SDK facades in the worker catch exactly
/// what they would catch in-process (<see cref="AppGatewayAccessDeniedException"/>,
/// <see cref="KeyNotFoundException"/>, <see cref="InvalidOperationException"/>,
/// <see cref="HackProcessKilledException"/>…).
/// </summary>
internal static class RemoteErrors
{
    /// <summary>Writes <paramref name="exception"/> as the <c>e</c> object of a result.</summary>
    public static void Write(Utf8JsonWriter writer, Exception exception)
    {
        writer.WriteStartObject("e");
        writer.WriteString("em", exception.Message);
        switch (exception)
        {
            case Kazo.Os.AppSdk.WebData.HackWebDbException database:
                writer.WriteString("et", "webdb");
                writer.WriteString("dn", database.Name);
                break;
            case AppGatewayAccessDeniedException denied:
                writer.WriteString("et", "denied");
                writer.WriteString("cap", denied.Capability);
                writer.WriteNumber("rev", denied.Evaluation.PolicyRevision);
                break;
            case HackProcessKilledException killed:
                writer.WriteString("et", "killed");
                writer.WriteNumber("pid", killed.ProcessId);
                writer.WriteNumber("rs", (int)killed.Reason);
                break;
            case Kazo.Os.AppSdk.Threading.HackBackgroundJobException fault:
                // ADR 0128 section 4: the type, message and stack of what escaped the task.
                writer.WriteString("et", "bgfault");
                writer.WriteString("ty", fault.OriginalTypeName);
                if (fault.OriginalStackTrace is { } stack)
                {
                    writer.WriteString("st", stack);
                }

                break;
            case OperationCanceledException:
                writer.WriteString("et", "cancel");
                break;
            case ArgumentException argument:
                writer.WriteString("et", "arg");
                if (argument.ParamName is { } parameter)
                {
                    writer.WriteString("pn", parameter);
                }

                break;
            case NotSupportedException:
                writer.WriteString("et", "notsup");
                break;
            case KeyNotFoundException:
                writer.WriteString("et", "notfound");
                break;
            case FormatException:
                writer.WriteString("et", "format");
                break;
            case HackUnauthorizedAccessException unauthorized:
                writer.WriteString("et", "unauth");
                WriteFileDetails(writer, unauthorized.ErrorCode, unauthorized.Path);
                if (unauthorized.RequiredCapability is { } capability)
                {
                    writer.WriteString("cap", capability);
                }

                break;
            case UnauthorizedAccessException:
                writer.WriteString("et", "unauth");
                break;
            case HackIOException io:
                writer.WriteString("et", "io");
                WriteFileDetails(writer, io.ErrorCode, io.Path);
                break;
            case IOException:
                writer.WriteString("et", "io");
                break;
            case InvalidOperationException:
                writer.WriteString("et", "inval");
                break;
            default:
                writer.WriteString("et", "error");
                writer.WriteString("ty", exception.GetType().FullName);
                break;
        }

        writer.WriteEndObject();
    }

    /// <summary>Rebuilds the exception described by the <c>e</c> object of a result.</summary>
    public static Exception Read(JsonElement error)
    {
        string message = error.GetOptionalString("em") ?? "The OS refused the call.";
        return error.GetOptionalString("et") switch
        {
            "denied" => new AppGatewayAccessDeniedException(
                error.GetString("cap"),
                CapabilityPolicyEvaluation.DenyMissing(Math.Max(1, error.GetOptional("rev")?.GetInt64() ?? 1))),
            "killed" => new HackProcessKilledException(
                error.GetInt64("pid"),
                error.GetOptional("rs") is { } reason ? (Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason)reason.GetInt32() : Kazo.Os.Simulation.Abstractions.Processes.ProcessExitReason.Killed),
            "bgfault" => new Kazo.Os.AppSdk.Threading.HackBackgroundJobException(
                error.GetOptionalString("ty") ?? "System.Exception", message, error.GetOptionalString("st")),
            "webdb" => new Kazo.Os.AppSdk.WebData.HackWebDbException(error.GetOptionalString("dn") ?? Kazo.Os.AppSdk.WebData.HackWebDbException.InvalidStateError, StripName(message, error.GetOptionalString("dn"))),
            "cancel" => new OperationCanceledException(message),
            "arg" => new ArgumentException(message, error.GetOptionalString("pn")),
            "notsup" => new NotSupportedException(message),
            "notfound" => new KeyNotFoundException(message),
            "format" => new FormatException(message),
            "unauth" => new HackUnauthorizedAccessException(
                message, ReadCode(error), error.GetOptionalString("path"), error.GetOptionalString("cap")),
            "io" => new HackIOException(message, ReadCode(error), error.GetOptionalString("path")),
            "inval" => new InvalidOperationException(message),
            _ => new WorkerRemoteException(error.GetOptionalString("ty") ?? "Exception", message)
        };
    }

    // HackWebDbException's message starts with its name; the rebuilt exception adds it again.
    private static string StripName(string message, string? name) =>
        name is not null && message.StartsWith(name + ": ", StringComparison.Ordinal) ? message[(name.Length + 2)..] : message;

    private static void WriteFileDetails(Utf8JsonWriter writer, FileSystemErrorCode? code, string? path)
    {
        if (code is { } value)
        {
            writer.WriteNumber("fc", (int)value);
        }

        if (path is not null)
        {
            writer.WriteString("path", path);
        }
    }

    private static FileSystemErrorCode? ReadCode(JsonElement error) =>
        error.GetOptional("fc") is { } code ? (FileSystemErrorCode)code.GetInt32() : null;
}

/// <summary>An exception of a type the protocol does not carry, thrown by a gateway on the OS side.</summary>
public sealed class WorkerRemoteException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="remoteType">Full name of the original exception type.</param>
    /// <param name="message">Its message.</param>
    public WorkerRemoteException(string remoteType, string message)
        : base($"{remoteType}: {message}")
    {
        RemoteType = remoteType;
    }

    /// <summary>Gets the full name of the original exception type.</summary>
    public string RemoteType { get; }
}
