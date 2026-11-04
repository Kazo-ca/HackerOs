using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Processes;
using Kazo.Os.Platform.Core.Tests.FileSystem.Virtual;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.Tests.Processes;

/// <summary>Login environment, <c>/etc/environment</c> and <c>/etc/hostname</c> (ADR 0050).</summary>
public sealed class LoginEnvironmentTests
{
    [Fact]
    public void Without_a_file_the_fixed_login_variables_are_set()
    {
        IReadOnlyDictionary<string, string> variables = LoginEnvironment.Build("alice", "/home/alice", environmentFile: null);

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["HOME"] = "/home/alice",
                ["USER"] = "alice",
                ["LOGNAME"] = "alice",
                ["SHELL"] = "/bin/hsh",
                ["PATH"] = "/bin:/usr/bin",
                ["PWD"] = "/home/alice",
                ["LANG"] = "en_US.UTF-8"
            },
            variables);
    }

    [Fact]
    public void The_file_adds_and_replaces_variables_like_pam_env()
    {
        List<string> warnings = [];
        IReadOnlyDictionary<string, string> variables = LoginEnvironment.Build(
            "alice",
            "/home/alice",
            "# managed by the administrator\r\n\nEDITOR=nano\nLANG='fr_CA.UTF-8'\nGREETING=\"hello world\"\n" +
            "HOME=/root\nUSER=root\nLOGNAME=root\nnot a pair\n1BAD=x\nEMPTY=\n",
            warnings);

        Assert.Equal("nano", variables["EDITOR"]);
        Assert.Equal("fr_CA.UTF-8", variables["LANG"]);
        Assert.Equal("hello world", variables["GREETING"]);
        Assert.Equal(string.Empty, variables["EMPTY"]);
        Assert.Equal("/home/alice", variables["HOME"]);
        Assert.Equal("alice", variables["USER"]);
        Assert.Equal("alice", variables["LOGNAME"]);
        Assert.False(variables.ContainsKey("1BAD"));

        Assert.Equal(5, warnings.Count);
        Assert.Contains(warnings, warning => warning.Contains("/etc/environment:6:", StringComparison.Ordinal) && warning.Contains("HOME", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("/etc/environment:9: ignored invalid line", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "hackeros")]
    [InlineData("", "hackeros")]
    [InlineData("\n# comment\n  black-ice  \nignored\n", "black-ice")]
    public void The_host_name_is_the_first_meaningful_line(string? content, string expected) =>
        Assert.Equal(expected, LoginEnvironment.ParseHostName(content));

    [Fact]
    public async Task The_seeder_creates_both_files_root_owned_0644_and_never_overwrites_them()
    {
        VirtualFileSystemFixture fixture = await VirtualFileSystemFixture.CreateSeededAsync();

        Assert.Equal("hackeros\n", await fixture.ReadTextAsync("/etc/hostname", fixture.AppContext(null, AppCapabilities.FileSystemSystemRead)));
        Assert.StartsWith("# /etc/environment", await fixture.ReadTextAsync("/etc/environment", fixture.AppContext(null, AppCapabilities.FileSystemSystemRead)));
        foreach (string path in new[] { "/etc/hostname", "/etc/environment" })
        {
            FileSystemResult<FileSystemEntrySnapshot> stat = await fixture.Service.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(path)), fixture.SystemContext());
            Assert.Equal(0x1A4, stat.Value!.Metadata.Permissions.Mode); // 0644
            Assert.Equal("system", stat.Value.Metadata.OwnerId);
        }

        FileSystemMutationResult edit = await fixture.Service.WriteAsync(
            new FileSystemWriteRequest(VirtualPath.Parse("/etc/hostname")),
            new VirtualFileSystemFixture.BytesSource("black-ice\n"u8.ToArray()),
            fixture.SystemContext());
        Assert.True(edit.Succeeded);

        await fixture.Seeder.SeedAsync("alice", "users");

        Assert.Equal("black-ice\n", await fixture.ReadTextAsync("/etc/hostname", fixture.AppContext(null, AppCapabilities.FileSystemSystemRead)));
    }
}
