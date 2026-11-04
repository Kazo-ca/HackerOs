using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Xunit;

namespace Kazo.Os.Apps.Settings.Tests;

/// <summary>Settings &gt; Default applications builds its rows from the apps and both layers (ADR 0121, section 4).</summary>
public sealed class FileAssociationsPanelTests
{
    private static readonly KazoAppInfo Notepad = Window("org.kazoos.notepad", "Notepad",
        new KazoAppFileHandler("text/plain", [".txt"], ["open", "edit"]));

    private static readonly KazoAppInfo Editor = Window("org.kazoos.text-editor", "Text Editor",
        new KazoAppFileHandler("text/plain", [".TXT", ".log"], ["open"]));

    private static readonly KazoAppInfo Browser = Window("org.kazoos.browser", "Browser",
        new KazoAppFileHandler("x-scheme-handler/https", [], ["open"]));

    [Fact]
    public void Rows_list_extensions_then_media_types_then_schemes_with_their_candidates_by_name()
    {
        IReadOnlyList<FileAssociationRow> rows = FileAssociationsPanel.BuildRows([Notepad, Editor, Browser], []);

        Assert.Equal(
            [".log|open", ".txt|open", ".txt|edit", "text/plain|open", "text/plain|edit", "x-scheme-handler/https|open"],
            rows.Select(row => row.Key));
        Assert.Equal(["Notepad", "Text Editor"], rows.Single(row => row.Key == ".txt|open").Candidates.Select(app => app.Name));
        Assert.Equal("https:", rows[^1].Label);
    }

    [Fact]
    public void A_row_shows_the_users_choice_and_the_system_default_apart()
    {
        IReadOnlyList<FileAssociationRow> rows = FileAssociationsPanel.BuildRows(
            [Notepad, Editor],
            [
                new KazoFileAssociation(".txt", "open", Editor.Id, KazoFileAssociationLayer.User),
                new KazoFileAssociation(".txt", "open", Notepad.Id, KazoFileAssociationLayer.System)
            ]);

        FileAssociationRow txt = rows.Single(row => row.Key == ".txt|open");
        Assert.Equal(Editor.Id, txt.UserAppId);
        Assert.Equal(Notepad.Id, txt.SystemAppId);
        Assert.Equal(Notepad.Id, txt.FallbackAppId);
    }

    [Fact]
    public void A_default_naming_an_app_that_is_not_a_candidate_is_ignored_and_a_sole_candidate_is_the_fallback()
    {
        IReadOnlyList<FileAssociationRow> rows = FileAssociationsPanel.BuildRows(
            [Notepad, Editor],
            [new KazoFileAssociation(".log", "open", "org.example.uninstalled", KazoFileAssociationLayer.User)]);

        FileAssociationRow log = rows.Single(row => row.Key == ".log|open");
        Assert.Null(log.UserAppId);
        Assert.Equal(Editor.Id, log.FallbackAppId);
        Assert.Null(rows.Single(row => row.Key == ".txt|open").FallbackAppId);
    }

    [Fact]
    public void Apps_that_are_not_windows_are_not_candidates()
    {
        KazoAppInfo command = Notepad with { Id = "org.kazoos.commands.cat", Name = "cat", Kind = AppKind.Terminal };

        Assert.Empty(FileAssociationsPanel.BuildRows([command], []));
    }

    private static KazoAppInfo Window(string id, string name, params KazoAppFileHandler[] handlers) =>
        new(id, name, name, AppKind.Window, "test", null, "1.0.0", "pub.test", false, false, handlers);
}
