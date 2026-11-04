using System.Reflection;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0155 §2: <c>KazoFileView</c> holds only the contract. It asks the OS for
/// <c>hackeros.file-view</c> and hands the provider every one of its parameters by name, so a
/// parameter added to the SDK reaches the File Explorer's control without another change.
/// </summary>
public sealed partial class KazoContractComponentTests
{
    [Fact]
    public async Task The_file_view_forwards_every_parameter_to_the_provider_of_its_contract()
    {
        MinimalAppExecutionContext context = new(WindowManifest());
        RecordingComponentHost host = new();
        using ContractRenderer renderer = new(host);
        FileViewWindow window = new();

        await renderer.RenderAsync(window, context);
        await renderer.WaitUntilAsync(() => CapturingFileViewProvider.Last?.UnmatchedParameters is not null);

        Assert.Equal(KazoComponentContracts.FileView, host.Contract);
        IReadOnlyDictionary<string, object> received = CapturingFileViewProvider.Last!.UnmatchedParameters!;
        string[] forwarded = typeof(KazoFileView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ParameterAttribute>() is not null)
            // The host's own parameters: the controller comes back through ControllerChanged, and
            // Unavailable is drawn by the host when no provider exists.
            .Where(property => property.Name is not (nameof(KazoFileView.ControllerChanged) or nameof(KazoFileView.Unavailable)))
            .Select(property => property.Name)
            .ToArray();
        Assert.All(forwarded, name => Assert.True(received.ContainsKey(name), $"'{name}' is not forwarded to the provider."));
        Assert.Equal("/home/user/projects", received[nameof(KazoFileView.InitialDirectory)]);
        Assert.Equal(KazoFileViewMode.Tree, received[nameof(KazoFileView.Mode)]);
        Assert.Equal(KazoFileViewFeatures.Clipboard | KazoFileViewFeatures.Rename, received[nameof(KazoFileView.Features)]);
        Assert.Equal(false, received[nameof(KazoFileView.AllowMultiSelect)]);
    }

    /// <summary>A window that shows the file control with a few non-default parameters.</summary>
    private sealed class FileViewWindow : KazoComponentBase
    {
        [Parameter]
        public IAppExecutionContext Context { get; set; } = null!;

        protected override void OnParametersSet() => AppContext = Context;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<IAppExecutionContext>>(0);
            builder.AddComponentParameter(1, "Value", Context);
            builder.AddComponentParameter(2, "IsFixed", true);
            builder.AddComponentParameter(3, "ChildContent", (RenderFragment)(child =>
            {
                child.OpenComponent<KazoFileView>(0);
                child.AddComponentParameter(1, nameof(KazoFileView.InitialDirectory), "/home/user/projects");
                child.AddComponentParameter(2, nameof(KazoFileView.Mode), KazoFileViewMode.Tree);
                child.AddComponentParameter(3, nameof(KazoFileView.Features), KazoFileViewFeatures.Clipboard | KazoFileViewFeatures.Rename);
                child.AddComponentParameter(4, nameof(KazoFileView.AllowMultiSelect), false);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A provider declaring no parameter: everything the host sends lands in <c>UnmatchedParameters</c>.</summary>
    public sealed class CapturingFileViewProvider : KazoEmbeddableComponent
    {
        public static CapturingFileViewProvider? Last { get; private set; }

        protected override void OnParametersSet() => Last = this;
    }

    /// <summary>Remembers which contract was asked for and resolves it to <see cref="CapturingFileViewProvider"/>.</summary>
    private sealed class RecordingComponentHost : IHackComponentHost
    {
        public string? Contract { get; private set; }

        public Task<KazoComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default)
        {
            Contract = contract;
            return Task.FromResult(KazoComponentResolution.Resolved(contract, typeof(CapturingFileViewProvider), "org.hackeros.file-explorer"));
        }
    }
}
