namespace Kazo.Os.Simulation.Abstractions;

/// <summary>
/// Validates a complete candidate settings document before commit.
/// </summary>
/// <remarks>
/// Since ADR 0044 settings are ordinary files; this validator remains for the strict whole-file
/// OS documents (JSON such as <c>/etc/kazoos/file-associations.json</c>) whose readers reject an
/// invalid document as a whole instead of parsing it tolerantly.
/// </remarks>
public interface ISettingsDocumentValidator
{
    /// <summary>Returns every syntax or schema failure in deterministic order.</summary>
    /// <param name="content">Complete serialized candidate document.</param>
    /// <returns>An empty list when the document is valid.</returns>
    IReadOnlyList<string> Validate(string content);
}
