using System.Collections.Immutable;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.Commands.Passwd;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Xunit;

namespace Kazo.Os.Commands.Wave5.Tests;

/// <summary><c>passwd</c> (ADR 0120, section 5): prompts with echo off, own change, sudo reset, -d and -S.</summary>
public sealed class PasswdCommandTests
{
    private const string NewPassword = "correct horse battery";

    [Fact]
    public async Task Passwd_asks_the_current_password_then_the_new_one_twice_with_echo_off()
    {
        FakeAccounts accounts = new();
        FakeTerminal input = new("hunter2", NewPassword, NewPassword);

        (int exit, string stdout, _) = await RunAsync(root: false, accounts, input);

        Assert.Equal(0, exit);
        Assert.Contains("Changing password for alice.", stdout, StringComparison.Ordinal);
        Assert.Contains("Current password: ", stdout, StringComparison.Ordinal);
        Assert.Contains("Retype new password: ", stdout, StringComparison.Ordinal);
        Assert.Contains("passwd: password updated successfully", stdout, StringComparison.Ordinal);
        Assert.Equal(("hunter2", NewPassword), accounts.Changed);
        Assert.All(input.EchoWhileReading, Assert.False);
        Assert.True(input.EchoEnabled);
    }

    [Fact]
    public async Task Different_retyped_passwords_change_nothing()
    {
        FakeAccounts accounts = new();

        (int exit, _, string stderr) = await RunAsync(root: false, accounts, new FakeTerminal("hunter2", NewPassword, "something else"));

        Assert.Equal(10, exit);
        Assert.Contains("Sorry, passwords do not match.", stderr, StringComparison.Ordinal);
        Assert.Null(accounts.Changed);
    }

    [Fact]
    public async Task A_short_password_is_a_bad_password()
    {
        FakeAccounts accounts = new();

        (int exit, _, string stderr) = await RunAsync(root: false, accounts, new FakeTerminal("hunter2", "short"));

        Assert.Equal(1, exit);
        Assert.Contains("BAD PASSWORD: The password is shorter than 8 characters.", stderr, StringComparison.Ordinal);
        Assert.Null(accounts.Changed);
    }

    [Fact]
    public async Task A_wrong_current_password_is_an_authentication_token_error()
    {
        FakeAccounts accounts = new() { RefuseCurrent = true };

        (int exit, _, string stderr) = await RunAsync(root: false, accounts, new FakeTerminal("bad", NewPassword, NewPassword));

        Assert.Equal(10, exit);
        Assert.Contains("passwd: Authentication token manipulation error", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("-d", "alice")]
    public async Task Without_sudo_another_account_or_a_removal_is_refused(params string[] args)
    {
        FakeAccounts accounts = new();

        (int exit, _, string stderr) = await RunAsync(root: false, accounts, new FakeTerminal(), args);

        Assert.Equal(1, exit);
        Assert.Contains("You may not view or modify password information for", stderr, StringComparison.Ordinal);
        Assert.Null(accounts.Reset);
    }

    [Fact]
    public async Task Under_sudo_another_accounts_password_is_set_without_the_old_one()
    {
        FakeAccounts accounts = new();

        (int exit, string stdout, _) = await RunAsync(root: true, accounts, new FakeTerminal(NewPassword, NewPassword), "bob");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("Current password", stdout, StringComparison.Ordinal);
        Assert.Equal(("bob", NewPassword), accounts.Reset);
    }

    [Fact]
    public async Task Under_sudo_minus_d_removes_the_password_and_without_a_name_it_refuses()
    {
        FakeAccounts accounts = new();

        (int removed, _, _) = await RunAsync(root: true, accounts, new FakeTerminal(), "-d", "bob");
        (int nameless, _, string stderr) = await RunAsync(root: true, accounts, new FakeTerminal());

        Assert.Equal(0, removed);
        Assert.Equal(("bob", (string?)null), accounts.Reset);
        Assert.Equal(1, nameless);
        Assert.Contains("sudo passwd <user>", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Minus_S_prints_the_status_line()
    {
        (int exit, string stdout, _) = await RunAsync(root: false, new FakeAccounts(), new FakeTerminal(), "-S");

        Assert.Equal(0, exit);
        Assert.Equal("alice P 2026-10-17 0 99999 7 -1", stdout.Trim());
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(
        bool root, FakeAccounts accounts, TextReader input, params string[] args)
    {
        AppManifest manifest = AppDefinitionMapper.ToManifest(new PasswdApp());
        StringWriter stdout = new();
        StringWriter stderr = new();
        TerminalExecutionContext context = new(
            app: new PasswdContext(manifest, root, accounts),
            arguments: args.ToImmutableArray(),
            standardInput: input,
            standardOutput: stdout,
            standardError: stderr,
            workingDirectory: "/home/alice",
            environment: ImmutableDictionary<string, string>.Empty);
        int exit = await new PasswdCommand(manifest).ExecuteAsync(context, CancellationToken.None);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    /// <summary>A terminal input that records whether echo was on at each line read.</summary>
    private sealed class FakeTerminal(params string[] lines) : TextReader, ITerminalInputMode
    {
        private readonly Queue<string> _lines = new(lines);

        public bool EchoEnabled { get; set; } = true;

        public List<bool> EchoWhileReading { get; } = [];

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            EchoWhileReading.Add(EchoEnabled);
            return ValueTask.FromResult(_lines.TryDequeue(out string? line) ? line : null);
        }
    }

    private sealed class FakeAccounts : IAppUserAccountsGateway
    {
        public bool RefuseCurrent { get; init; }

        public (string Current, string New)? Changed { get; private set; }

        public (string Login, string? New)? Reset { get; private set; }

        public ValueTask<IReadOnlyList<KazoUserAccount>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<KazoUserAccount>>(
            [
                new("alice", "Alice", AppAuthority.User, true, true, new DateTimeOffset(2026, 10, 17, 9, 0, 0, TimeSpan.Zero), true),
                new("bob", "Bob", AppAuthority.User, true, false, DateTimeOffset.UnixEpoch, false)
            ]);

        public ValueTask ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken = default)
        {
            if (RefuseCurrent)
            {
                throw new UnauthorizedAccessException("wrong");
            }

            Changed = (currentPassword, newPassword);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResetPasswordAsync(string loginName, string? newPassword, CancellationToken cancellationToken = default)
        {
            Reset = (loginName, newPassword);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PasswdContext(AppManifest manifest, bool root, IAppUserAccountsGateway accounts) : IAppExecutionContext
    {
        public AppManifest Manifest { get; } = manifest;
        public Guid InstanceId { get; } = Guid.NewGuid();
        public string UserId => "user";
        public string UserLoginName => "alice";
        public AppAuthority UserAuthority => AppAuthority.User;
        public bool IsRootElevated => root;
        public IAppUserAccountsGateway UserAccounts => accounts;
        public IReadOnlySet<string> GrantedCapabilities => new HashSet<string>(Manifest.Capabilities);
        public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
        public ProcessId ProcessId { get; } = ProcessId.FromInt64(1);
        public CancellationToken CancellationToken => CancellationToken.None;
        public ICapabilityChecker Capabilities => throw new NotImplementedException();
        public IAppFileSystemGateway FileSystem => throw new NotImplementedException();
        public IAppEventGateway Events => throw new NotImplementedException();
        public IAppNotificationGateway Notifications => throw new NotImplementedException();
        public IAppLoggingGateway Logging => throw new NotImplementedException();
        public IAppClockGateway Clock => throw new NotImplementedException();
        public IAppProcessGateway Processes => throw new NotImplementedException();
    }
}
