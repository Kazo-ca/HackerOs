using System.Text;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.AppSdk;

/// <summary>
/// Base class of a web app (<see cref="AppKind.Web"/>, ADR 0073): a process that listens on the URLs
/// of its manifest (<c>web.urls</c>) and answers each request in <see cref="ProcessRequestAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The OS starts the process on demand, when a first request reaches one of its URLs (ADR 0074,
/// section 3), in the shared web worker; the process then shows in the task manager, <c>ps</c> and
/// <c>netstat -tlnp</c>, and can be stopped; the next request starts it again.
/// </para>
/// <para>
/// Requests are served concurrently. An exception thrown by <see cref="ProcessRequestAsync"/> becomes
/// a <c>500 Internal Server Error</c>; a response the method did not close is sent when it returns.
/// The ASP.Kazo MVC host (ADR 0076) is built on this class.
/// </para>
/// </remarks>
public abstract class WebServerAppBase : ServiceAppBase
{
    /// <summary>Initializes a web app with its validated manifest.</summary>
    /// <param name="manifest">Web application manifest (kind <c>web</c>).</param>
    protected WebServerAppBase(AppManifest manifest)
        : base(manifest, AppKind.Web)
    {
    }

    /// <summary>Gets the URLs the site listens on (<c>web.urls</c> of its manifest).</summary>
    public IReadOnlyList<string> Urls => Manifest.Web?.Urls ?? [];

    /// <summary>Handles one request: fill <see cref="KazoHttpListenerContext.Response"/>.</summary>
    /// <param name="context">The request and its response.</param>
    /// <param name="cancellationToken">Signals that the site is stopping.</param>
    /// <returns>A task that ends when the response is ready.</returns>
    protected abstract Task ProcessRequestAsync(KazoHttpListenerContext context, CancellationToken cancellationToken);

    /// <summary>Called once the listener is open, before the first request is read.</summary>
    /// <param name="listener">The open listener.</param>
    /// <param name="cancellationToken">Signals that the site is stopping.</param>
    protected virtual Task OnListeningAsync(KazoHttpListener listener, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    protected sealed override async Task RunCoreAsync(IAppExecutionContext context, CancellationToken sessionCancellationToken)
    {
        await using KazoHttpListener listener = new();
        foreach (string url in Urls)
        {
            listener.Prefixes.Add(url);
        }

        await listener.StartAsync(sessionCancellationToken).ConfigureAwait(false);
        await OnListeningAsync(listener, sessionCancellationToken).ConfigureAwait(false);
        while (!sessionCancellationToken.IsCancellationRequested)
        {
            KazoHttpListenerContext request;
            try
            {
                request = await listener.GetContextAsync(sessionCancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (sessionCancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = ServeAsync(request, sessionCancellationToken);
        }
    }

    private async Task ServeAsync(KazoHttpListenerContext request, CancellationToken cancellationToken)
    {
        try
        {
            await ProcessRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Like Kestrel without the developer page: a bare 500, the detail goes to the log.
            KazoExecutionContext.Current.Logging.Log(
                Kazo.Os.Simulation.Abstractions.Diagnostics.DiagnosticSeverity.Error,
                $"{request.Request.HttpMethod} {request.Request.RawUrl}: {exception.GetType().Name}: {exception.Message}");
            if (request.Response.IsClosed)
            {
                return;
            }

            request.Response.StatusCode = 500;
            request.Response.ContentType = "text/plain; charset=utf-8";
            request.Response.Close(Encoding.UTF8.GetBytes("500 Internal Server Error"), willBlock: false);
        }
        finally
        {
            request.Response.Close();
        }
    }
}
