using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Diagnostics;
using Kazo.Os.AppSdk.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.SystemMonitor.Tabs;

/// <summary>
/// Detail of one process (ADR 0058, section 2): the whole <c>status</c>, the command line, the
/// working directory, the environment when readable, the <c>io</c> counters, the parent and the
/// children. Everything comes from <see cref="HackProcess"/>, that is from <c>/proc/&lt;pid&gt;</c>.
/// </summary>
public partial class ProcessDetailPanel
{
    private IReadOnlyDictionary<string, string>? _environment;
    private long? _environmentPid;
    private bool _loadingEnvironment;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The panel's texts in the language of the System Monitor process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The culture of the numbers and dates shown (the process's locale).</summary>
    private CultureInfo Formats => AppContext?.FormatCulture ?? CultureInfo.InvariantCulture;

    /// <summary>The kind of the process (window app, command, service, site), translated.</summary>
    private string KindLabel => Process.Kind switch
    {
        AppKind.Window => L["Kind.Window"],
        AppKind.Terminal => L["Kind.Terminal"],
        AppKind.Service => L["Kind.Service"],
        AppKind.Web => L["Kind.Web"],
        _ => Process.Kind.ToString()
    };

    /// <summary>Gets or sets the process shown (refreshed every second by the window).</summary>
    [Parameter, EditorRequired]
    public HackProcess Process { get; set; } = default!;

    /// <summary>Gets or sets every visible process, to find the parent and the children.</summary>
    [Parameter, EditorRequired]
    public IEnumerable<HackProcess> AllProcesses { get; set; } = [];

    /// <summary>Gets or sets the handler that closes the panel.</summary>
    [Parameter]
    public EventCallback OnClose { get; set; }

    /// <summary>Gets or sets the handler that shows another process (parent or child).</summary>
    [Parameter]
    public EventCallback<long> OnSelectProcess { get; set; }

    private HackProcess? Parent => Process.ParentId is { } parentId
        ? AllProcesses.FirstOrDefault(process => process.Id == parentId)
        : null;

    private IReadOnlyList<HackProcess> Children => Process.GetChildren(AllProcesses);

    /// <inheritdoc />
    protected override async Task OnParametersSetAsync()
    {
        // The environment rarely changes: read it when another process is shown, or on "reload".
        if (_environmentPid != Process.Id)
        {
            await LoadEnvironmentAsync();
        }
    }

    private async Task LoadEnvironmentAsync()
    {
        _environmentPid = Process.Id;
        _loadingEnvironment = true;
        try
        {
            _environment = await Process.GetEnvironmentVariablesAsync();
        }
        finally
        {
            _loadingEnvironment = false;
        }
    }
}
