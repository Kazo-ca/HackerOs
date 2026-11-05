using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;

namespace Kazo.Os.Template.Command;

/// <summary>The command itself: reads its arguments, writes to standard output, returns an exit code.</summary>
public sealed class Command : TerminalAppBase
{
    /// <summary>
    /// Creates the command for the manifest the OS loaded from <c>manifest.json</c>. The OS starts
    /// an app through this constructor (one <see cref="AppManifest"/> argument), as <c>exec</c> hands a
    /// program its arguments.
    /// </summary>
    /// <param name="manifest">This app's manifest.</param>
    public Command(AppManifest manifest)
        : base(manifest)
    {
    }

    /// <summary>Creates the command with the manifest built from <see cref="TemplateCommandApp"/>, for unit tests.</summary>
    public Command()
        : this(AppDefinitionMapper.ToManifest(new TemplateCommandApp()))
    {
    }

    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        string who = context.Arguments.FirstOrDefault() ?? context.App.UserId;
        await context.StandardOutput.WriteLineAsync($"Hello, {who}!".AsMemory(), cancellationToken);
        return 0;
    }
}
