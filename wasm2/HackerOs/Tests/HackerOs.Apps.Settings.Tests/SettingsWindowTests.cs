using HackerOs.App.Abstractions;
using Xunit;

namespace HackerOs.Apps.Settings.Tests;

public sealed class SettingsWindowTests
{
    [Fact]
    public void Manifest_HasWindowKind_AndRequiredCapabilities()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SettingsApp());
        Assert.Equal(AppKind.Window, manifest.Kind);
        Assert.True(manifest.SingleInstancePerUser);
        Assert.Contains(AppCapabilities.FileSystemSystemRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.FileSystemSystemWrite, manifest.Capabilities);
        Assert.DoesNotContain(manifest.Capabilities, capability => capability.StartsWith("settings.", StringComparison.Ordinal));
        Assert.Contains(AppCapabilities.FileAssociationsRead, manifest.Capabilities);
        Assert.Contains(AppCapabilities.FileAssociationsWrite, manifest.Capabilities);
    }
}

/// <summary>Settings' <c>--app=&lt;id&gt;</c> argument opens an app's page (ADR 0096, section 4).</summary>
public sealed class SettingsAppArgumentTests
{
    [Fact]
    public void The_app_argument_names_the_page_to_show()
    {
        Assert.Equal("org.hackeros.notepad", SettingsWindow.ReadAppArgument(["--app=org.hackeros.notepad"]));
    }

    [Fact]
    public void The_last_app_argument_wins_and_an_empty_one_is_ignored()
    {
        Assert.Equal("org.b", SettingsWindow.ReadAppArgument(["--app=org.a", "--app=org.b", "--app= "]));
    }

    [Fact]
    public void Without_an_app_argument_there_is_no_page()
    {
        Assert.Null(SettingsWindow.ReadAppArgument(["--show-settings"]));
        Assert.Null(SettingsWindow.ReadAppArgument([]));
    }

    [Fact]
    public void A_page_argument_opens_a_known_page_and_ignores_unknown_pages()
    {
        Assert.Equal("appearance", SettingsWindow.ReadPageArgument(["--page=appearance"]));
        Assert.Equal("desktop", SettingsWindow.ReadPageArgument(["--page=Desktop"]));
        Assert.Equal("desktop", SettingsWindow.ReadPageArgument(["--page=desktop", "--page=nowhere"]));
        Assert.Equal("users", SettingsWindow.ReadPageArgument(["--page=desktop", "--page=users"]));
        Assert.Null(SettingsWindow.ReadPageArgument(["--page=nowhere"]));
        Assert.Null(SettingsWindow.ReadPageArgument([]));
    }

    [Fact]
    public void The_application_page_is_only_opened_by_the_app_argument()
    {
        Assert.Null(SettingsWindow.ReadPageArgument(["--page=application"]));
    }

    [Fact]
    public void Every_known_page_id_is_accepted_by_the_page_argument()
    {
        foreach (SettingsPage page in SettingsWindow.AllPages.Where(static page => page.Id != SettingsWindow.ApplicationPageId))
        {
            Assert.Equal(page.Id, SettingsWindow.ReadPageArgument([SettingsWindow.PageArgumentPrefix + page.Id]));
        }
    }

    [Fact]
    public void Settings_can_start_and_stop_services_from_an_app_page()
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new SettingsApp());

        Assert.Contains(AppCapabilities.ServicesManage, manifest.Capabilities);
    }
}

/// <summary>The side menu of Settings (ADR 0170): its pages, their order and their optional entries.</summary>
public sealed class SettingsNavigationTests
{
    [Fact]
    public void Appearance_is_first_and_About_is_last()
    {
        IReadOnlyList<SettingsPage> pages = SettingsWindow.PagesFor(backupSupported: true, hasAppPage: true);

        Assert.Equal(SettingsWindow.AppearancePageId, pages[0].Id);
        Assert.Equal("about", pages[^1].Id);
    }

    [Fact]
    public void Backup_and_Application_are_listed_only_when_available()
    {
        IReadOnlyList<SettingsPage> minimal = SettingsWindow.PagesFor(backupSupported: false, hasAppPage: false);
        IReadOnlyList<SettingsPage> full = SettingsWindow.PagesFor(backupSupported: true, hasAppPage: true);

        Assert.DoesNotContain(minimal, static page => page.Id == SettingsWindow.BackupPageId);
        Assert.DoesNotContain(minimal, static page => page.Id == SettingsWindow.ApplicationPageId);
        Assert.Contains(full, static page => page.Id == SettingsWindow.BackupPageId);
        Assert.Contains(full, static page => page.Id == SettingsWindow.ApplicationPageId);
        Assert.Equal(17, minimal.Count);
        Assert.Equal(SettingsWindow.AllPages.Count, full.Count);
    }

    [Fact]
    public void Page_ids_are_unique_and_pages_of_a_group_are_contiguous()
    {
        Assert.Equal(SettingsWindow.AllPages.Count, SettingsWindow.AllPages.Select(static page => page.Id).Distinct().Count());

        List<string> groupsInOrder = [];
        foreach (SettingsPage page in SettingsWindow.AllPages)
        {
            if (groupsInOrder.Count == 0 || groupsInOrder[^1] != page.GroupKey)
            {
                Assert.DoesNotContain(page.GroupKey, groupsInOrder);
                groupsInOrder.Add(page.GroupKey);
            }
        }
    }

    [Fact]
    public void Every_label_and_group_of_the_menu_is_translated()
    {
        System.Resources.ResourceManager resources = HackerOs.AppSdk.Globalization.HackLocalization.GetResourceManager(typeof(Strings));
        foreach (string key in SettingsWindow.AllPages.SelectMany(static page => new[] { page.LabelKey, page.GroupKey })
                     .Concat(["Nav.AriaLabel", "Nav.Collapse", "Nav.Expand"]))
        {
            Assert.False(string.IsNullOrEmpty(resources.GetString(key, System.Globalization.CultureInfo.InvariantCulture)), key);
            Assert.False(string.IsNullOrEmpty(resources.GetString(key, System.Globalization.CultureInfo.GetCultureInfo("fr"))), key);
        }
    }

    [Theory]
    [InlineData("en", "Author:", "GitHub project:")]
    [InlineData("fr", "Auteur :", "Projet GitHub :")]
    public void About_credit_labels_follow_the_session_language(string culture, string author, string project)
    {
        System.Resources.ResourceManager resources = HackerOs.AppSdk.Globalization.HackLocalization.GetResourceManager(typeof(Strings));
        System.Globalization.CultureInfo info = System.Globalization.CultureInfo.GetCultureInfo(culture);
        Assert.Equal(author, resources.GetString("About.Author", info));
        Assert.Equal(project, resources.GetString("About.Repository", info));
    }
}
