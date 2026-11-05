namespace Kazo.Os.AppSdk.Blazor;

/// <summary>
/// Local stand-in for the real Blazor <c>WindowAppBase</c>, sharing its exact namespace and name
/// so <see cref="Kazo.Os.Platform.Core.Discovery.AppEntryPointDiscovery"/>'s name-based base-type
/// matching resolves it identically without this test project referencing the real Blazor
/// assembly (Platform Core itself never references it either; see remarks on
/// <see cref="Kazo.Os.Platform.Core.Discovery.AppEntryPointDiscovery"/>).
/// </summary>
internal abstract class WindowAppBase;

/// <summary>
/// Local stand-in for the real Blazor <c>IAppBackHandler</c>, sharing its exact namespace and
/// name so <see cref="Kazo.Os.Platform.Core.Discovery.AppEntryPointDiscovery"/>'s name-based
/// interface matching resolves it identically without this test project referencing the real
/// Blazor assembly. Members are irrelevant to discovery, which only checks interface identity.
/// </summary>
internal interface IAppBackHandler;
