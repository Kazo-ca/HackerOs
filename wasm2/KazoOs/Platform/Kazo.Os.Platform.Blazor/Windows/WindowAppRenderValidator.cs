using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.Platform.Core.Discovery;
using Kazo.Os.Windowing.Core;
using Kazo.Os.Windowing.Abstractions;

namespace Kazo.Os.Platform.Blazor.Windows;

/// <summary>Validates trusted window render inputs before app component construction.</summary>
public static class WindowAppRenderValidator
{
    /// <summary>Rejects mismatched or non-window descriptors and execution contexts.</summary>
    public static void Validate(
        WindowRuntimeState window,
        AppDescriptor descriptor,
        IAppExecutionContext appContext)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(appContext);

        ManifestValidationResult manifestValidation = AppManifestValidator.Validate(descriptor.Manifest);
        if (!manifestValidation.IsValid || descriptor.Manifest.Kind != AppKind.Window)
        {
            throw new InvalidOperationException("Only a validated Window app descriptor can be rendered in a window host.");
        }

        Type entryPointType = descriptor.EntryPointType;
        if (!typeof(WindowAppBase).IsAssignableFrom(entryPointType)
            || entryPointType.IsAbstract
            || entryPointType.IsInterface
            || entryPointType.ContainsGenericParameters)
        {
            throw new InvalidOperationException("The Window app entry point must be a closed, concrete WindowAppBase component.");
        }

        if (entryPointType.Assembly != descriptor.Assembly)
        {
            throw new InvalidOperationException("The Window app descriptor assembly does not own its entry point.");
        }

        if (!string.Equals(window.AppId, descriptor.Manifest.Id, StringComparison.Ordinal)
            || !string.Equals(appContext.Manifest.Id, descriptor.Manifest.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Window, descriptor, and execution context app IDs must match.");
        }

        if (window.OwnerInstanceId.Value != appContext.InstanceId)
        {
            throw new InvalidOperationException("Window and execution context instance identities must match.");
        }
    }
}