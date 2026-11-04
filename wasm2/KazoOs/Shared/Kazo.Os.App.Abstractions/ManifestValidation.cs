namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Describes one deterministic manifest validation failure.
/// </summary>
/// <param name="Code">Stable machine-readable error code.</param>
/// <param name="Path">Manifest property path associated with the error.</param>
/// <param name="Message">Human-readable diagnostic text.</param>
public sealed record ManifestValidationError(string Code, string Path, string Message);

/// <summary>
/// Contains all validation failures found in one manifest.
/// </summary>
/// <param name="Errors">Validation failures in deterministic validation order.</param>
public sealed record ManifestValidationResult(IReadOnlyList<ManifestValidationError> Errors)
{
    /// <summary>Gets a value indicating whether the manifest is valid.</summary>
    public bool IsValid => Errors.Count == 0;
}