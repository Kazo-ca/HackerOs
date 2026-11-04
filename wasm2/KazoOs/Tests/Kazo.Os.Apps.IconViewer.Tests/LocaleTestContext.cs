using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;

namespace Kazo.Os.Apps.IconViewer.Tests;

/// <summary>
/// An app execution context whose process runs with <c>LANG=&lt;locale&gt;</c> (ADR 0104): only the
/// language and format culture are real, every gateway throws.
/// </summary>
internal sealed class LocaleTestContext(string locale) : IAppExecutionContext
{
    public AppManifest Manifest => AppDefinitionMapper.ToManifest(new IconViewerApp());
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string UserId => "user-1";
    public AppAuthority UserAuthority => AppAuthority.User;
    public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
    public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
    public ProcessId ProcessId { get; } = ProcessId.FromInt64(100);
    public CancellationToken CancellationToken => CancellationToken.None;
    public ICapabilityChecker Capabilities => throw new NotSupportedException();
    public IAppFileSystemGateway FileSystem => throw new NotSupportedException();
    public IAppEventGateway Events => throw new NotSupportedException();
    public IAppNotificationGateway Notifications => throw new NotSupportedException();
    public IAppLoggingGateway Logging => throw new NotSupportedException();
    public IAppClockGateway Clock => throw new NotSupportedException();
    public IAppProcessGateway Processes => throw new NotSupportedException();

    /// <summary>The language resolved from <c>LANG</c>, as a live process does.</summary>
    public CultureInfo Culture => HackLocale.ResolveLanguage(Variable);

    /// <summary>The format culture resolved from <c>LANG</c>.</summary>
    public CultureInfo FormatCulture => HackLocale.ResolveFormatCulture(Variable);

    private string? Variable(string name) => name == HackLocale.LangVariable ? locale : null;
}
