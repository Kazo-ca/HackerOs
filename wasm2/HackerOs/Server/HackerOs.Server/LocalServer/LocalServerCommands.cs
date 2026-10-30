using HackerOs.Server.Services;

namespace HackerOs.Server.LocalServer;

/// <summary>
/// Commands of the installable server run from a terminal on its machine, instead of starting it
/// (ADR 0191, <c>doc/en/wasm/dev/installable-server.md</c>):
/// <c>HackerOs.Server pair &lt;account&gt;</c> prints a one-time pairing code for that account,
/// creating the account first when it does not exist (the install scripts run it for <c>admin</c>).
/// </summary>
/// <remarks>
/// The command runs against the same configuration as the server (its folder's
/// <c>appsettings.json</c> and <c>hackeros-server.json</c>), so it reaches the same database and
/// pairing folder while the server keeps running as a service.
/// </remarks>
public sealed class LocalServerCommand
{
    private LocalServerCommand(string name, string? account)
    {
        Name = name;
        Account = account;
    }

    /// <summary>The command: <c>pair</c> or <c>help</c>.</summary>
    public string Name { get; }

    /// <summary>Account of <c>pair</c>.</summary>
    public string? Account { get; }

    /// <summary>
    /// Reads a command from the start of the program arguments; <see langword="null"/> when the
    /// arguments start the server (no command, or only <c>--key=value</c> settings).
    /// </summary>
    /// <param name="args">Program arguments.</param>
    public static LocalServerCommand? Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count == 0)
        {
            return null;
        }

        return args[0] switch
        {
            "pair" => new LocalServerCommand("pair", args.Count > 1 && !args[1].StartsWith('-') ? args[1] : null),
            "help" or "--help" or "-h" => new LocalServerCommand("help", null),
            _ => null
        };
    }

    /// <summary>The program arguments left for the host once the command and its operand are removed.</summary>
    /// <param name="args">Program arguments.</param>
    public static string[] HostArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        LocalServerCommand? command = Parse(args);
        if (command is null)
        {
            return args;
        }

        int consumed = command.Account is null ? 1 : 2;
        return args[consumed..];
    }

    /// <summary>Runs the command; returns the process exit code.</summary>
    /// <param name="services">The built server's services (database, pairing store).</param>
    /// <param name="output">Where to write.</param>
    /// <param name="cancellationToken">Cancels the command.</param>
    public async Task<int> RunAsync(IServiceProvider services, TextWriter output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(output);
        if (Name == "help" || Account is null)
        {
            await output.WriteLineAsync("Usage:").ConfigureAwait(false);
            await output.WriteLineAsync("  HackerOs.Server                  start the server").ConfigureAwait(false);
            await output.WriteLineAsync("  HackerOs.Server pair <account>   print a one-time pairing code for <account> (created if missing)").ConfigureAwait(false);
            return Name == "help" ? 0 : 2;
        }

        using IServiceScope scope = services.CreateScope();
        IAccountService accounts = scope.ServiceProvider.GetRequiredService<IAccountService>();
        bool created = await accounts.EnsurePairingAccountAsync(Account, cancellationToken).ConfigureAwait(false);
        string code = scope.ServiceProvider.GetRequiredService<PairingCodeStore>().Create(Account);

        if (created)
        {
            await output.WriteLineAsync($"Account '{Account}' created.").ConfigureAwait(false);
        }

        await output.WriteLineAsync($"Pairing code for '{Account}': {code}").ConfigureAwait(false);
        await output.WriteLineAsync(
            $"Valid once, for {PairingCodeStore.Lifetime.TotalMinutes:0} minutes. In HackerOS: Settings > Server connection > Local server, type the server address and this code.")
            .ConfigureAwait(false);
        return 0;
    }
}

/// <summary>Registers the services of the installable server.</summary>
public static class LocalServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <see cref="PairingCodeStore"/>, over <c>HackerOs:Pairing:Root</c> (default
    /// <c>pairing</c>, relative to the content root).
    /// </summary>
    /// <param name="services">The services.</param>
    /// <param name="configuration">The server configuration.</param>
    /// <param name="contentRoot">The server's content root.</param>
    public static IServiceCollection AddLocalServer(this IServiceCollection services, IConfiguration configuration, string contentRoot)
    {
        string root = configuration[PairingCodeStore.RootSettingKey] is { Length: > 0 } configured ? configured : "pairing";
        string path = Path.IsPathRooted(root) ? root : Path.Combine(contentRoot, root);
        services.AddSingleton(provider => new PairingCodeStore(path, provider.GetService<TimeProvider>()));
        return services;
    }
}
