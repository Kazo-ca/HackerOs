using System.Reflection;
using Kazo.Os.AppSdk.Blazor.Components;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.AppSdk.Components;
using Kazo.Os.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.AppSdk.Blazor.Tests;

/// <summary>
/// ADR 0155 §2: <c>HackFileView</c> holds only the contract. It asks the OS for
/// <c>hackeros.file-view</c> and hands the provider every one of its parameters by name, so a
/// parameter added to the SDK reaches the File Explorer's control without another change.
/// </summary>
public sealed partial class HackContractComponentTests
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

        Assert.Equal(HackComponentContracts.FileView, host.Contract);
        IReadOnlyDictionary<string, object> received = CapturingFileViewProvider.Last!.UnmatchedParameters!;
        string[] forwarded = typeof(HackFileView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetCustomAttribute<ParameterAttribute>() is not null)
            // The host's own parameters: the controller comes back through ControllerChanged, and
            // Unavailable is drawn by the host when no provider exists.
            .Where(property => property.Name is not (nameof(HackFileView.ControllerChanged) or nameof(HackFileView.Unavailable)))
            .Select(property => property.Name)
            .ToArray();
        Assert.All(forwarded, name => Assert.True(received.ContainsKey(name), $"'{name}' is not forwarded to the provider."));
        Assert.Equal("/home/user/projects", received[nameof(HackFileView.InitialDirectory)]);
        Assert.Equal(HackFileViewMode.Tree, received[nameof(HackFileView.Mode)]);
        Assert.Equal(HackFileViewFeatures.Clipboard | HackFileViewFeatures.Rename, received[nameof(HackFileView.Features)]);
        Assert.Equal(false, received[nameof(HackFileView.AllowMultiSelect)]);
    }

    /// <summary>A window that shows the file control with a few non-default parameters.</summary>
    private sealed class FileViewWindow : HackComponentBase
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
                child.OpenComponent<HackFileView>(0);
                child.AddComponentParameter(1, nameof(HackFileView.InitialDirectory), "/home/user/projects");
                child.AddComponentParameter(2, nameof(HackFileView.Mode), HackFileViewMode.Tree);
                child.AddComponentParameter(3, nameof(HackFileView.Features), HackFileViewFeatures.Clipboard | HackFileViewFeatures.Rename);
                child.AddComponentParameter(4, nameof(HackFileView.AllowMultiSelect), false);
                child.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    /// <summary>A provider declaring no parameter: everything the host sends lands in <c>UnmatchedParameters</c>.</summary>
    public sealed class CapturingFileViewProvider : HackEmbeddableComponent
    {
        public static CapturingFileViewProvider? Last { get; private set; }

        protected override void OnParametersSet() => Last = this;
    }

    /// <summary>Remembers which contract was asked for and resolves it to <see cref="CapturingFileViewProvider"/>.</summary>
    private sealed class RecordingComponentHost : IHackComponentHost
    {
        public string? Contract { get; private set; }

        public Task<HackComponentResolution> ResolveAsync(string contract, CancellationToken cancellationToken = default)
        {
            Contract = contract;
            return Task.FromResult(HackComponentResolution.Resolved(contract, typeof(CapturingFileViewProvider), "org.hackeros.file-explorer"));
        }
    }
}
