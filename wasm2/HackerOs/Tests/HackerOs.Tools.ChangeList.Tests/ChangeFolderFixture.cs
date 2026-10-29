namespace HackerOs.Tools.ChangeList.Tests;

/// <summary>A temporary <c>changes/</c> folder, deleted with the test.</summary>
internal sealed class ChangeFolderFixture : IDisposable
{
    public ChangeFolderFixture()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hackeros-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(System.IO.Path.Combine(Path, "unreleased"));
    }

    public string Path { get; }

    /// <summary>The repository's own <c>wasm2/HackerOs/changes</c> folder.</summary>
    public static string RepositoryChanges
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "HackerOs.sln")))
            {
                directory = directory.Parent;
            }

            return System.IO.Path.Combine(directory?.FullName ?? throw new InvalidOperationException("HackerOs.sln not found."), "changes");
        }
    }

    public void AddUnreleased(string id, string category, string audience = "\"user\"", int? pr = null, string links = "[]") =>
        File.WriteAllText(System.IO.Path.Combine(Path, "unreleased", id + ".json"), Entry(id, category, audience, pr, links));

    public void AddVersion(string version, string date, params string[] entries) =>
        File.WriteAllText(
            System.IO.Path.Combine(Path, version + ".json"),
            $$"""{ "version": "{{version}}", "date": "{{date}}", "entries": [ {{string.Join(", ", entries)}} ] }""");

    public static string Entry(string id, string category, string audience = "\"user\"", int? pr = null, string links = "[]") =>
        $$"""
        {
          "id": "{{id}}",
          "category": "{{category}}",
          "audience": [ {{audience}} ],
          "title": { "en": "Title {{id}}", "fr": "Titre {{id}}" },
          "description": { "en": "Text {{id}}", "fr": "Texte {{id}}" },
          {{(pr is { } number ? $"\"pr\": {number}," : "")}}
          "links": {{links}}
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
