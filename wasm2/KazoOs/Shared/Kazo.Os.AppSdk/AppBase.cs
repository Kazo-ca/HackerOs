using Kazo.Os.App.Abstractions;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Provides common manifest validation for all Kazo OS application base types.
/// </summary>
public abstract class AppBase
{
    /// <summary>
    /// Initializes an application and verifies its manifest before any app code runs.
    /// </summary>
    /// <param name="manifest">Manifest belonging to the application.</param>
    /// <param name="expectedKind">App kind required by the concrete base class.</param>
    /// <exception cref="ArgumentException">The manifest is invalid or has the wrong app kind.</exception>
    protected AppBase(AppManifest manifest, AppKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ManifestValidationResult validation = AppManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            string diagnostics = string.Join(
                Environment.NewLine,
                validation.Errors.Select(error => $"{error.Path}: {error.Message}"));
            throw new ArgumentException($"The application manifest is invalid:{Environment.NewLine}{diagnostics}", nameof(manifest));
        }

        if (manifest.Kind != expectedKind)
        {
            throw new ArgumentException(
                $"Manifest kind '{manifest.Kind}' cannot be used with a '{expectedKind}' app base.",
                nameof(manifest));
        }

        Manifest = manifest;
    }

    /// <summary>Gets the validated immutable application manifest.</summary>
    public AppManifest Manifest { get; }
}