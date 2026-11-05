using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Kazo.Os.Windowing.SampleHost;

/// <summary>Describes one sample application the launcher can open.</summary>
public sealed record SampleAppDescriptor(string Id, string Title, RenderFragment Content);

/// <summary>
/// The sample host's own tiny "app source" -- standing in for whatever a real consumer would use
/// (a plugin registry, a fixed list, a remote catalog). Nothing here comes from Kazo OS.
/// </summary>
public static class SampleAppCatalog
{
    /// <summary>Gets the fixed set of sample applications.</summary>
    public static IReadOnlyList<SampleAppDescriptor> Apps { get; } =
    [
        new("sample.welcome", "Welcome", BuildContent<WelcomeWindow>()),
        new("sample.counter", "Counter", BuildContent<CounterWindow>()),
    ];

    private static RenderFragment BuildContent<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] TComponent>()
        where TComponent : IComponent => builder =>
    {
        builder.OpenComponent<TComponent>(0);
        builder.CloseComponent();
    };
}
