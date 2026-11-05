using Kazo.Os.AppSdk.Web.Generated;
using Kazo.Os.AppSdk.Web.Hosting;
using Kazo.Os.AppSdk.Web.Http;
using Kazo.Os.AppSdk.Web.Logging;
using Kazo.Os.AppSdk.Web.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.AppSdk.Web.Tests.Logging;

/// <summary>A site's debug log (ADR 0088 §5): off by default, then requests, <c>ILogger&lt;T&gt;</c> lines and errors.</summary>
public sealed class KazoWebDebugLogTests
{
    private const string Site = "http://debuglog.hack";

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 1, 22, 14, 123, TimeSpan.Zero);
    }

    private sealed class Marker;

    /// <summary>The category of <c>ILogger&lt;Marker&gt;</c>: nested types are written with a dot, as ASP.NET Core does.</summary>
    private static readonly string Category = typeof(Marker).FullName!.Replace('+', '.');

    private static async Task<KazoWebDebugLogMemorySink> RunAsync(Action<KazoWebLoggingOptions> logging)
    {
        KazoWebApplicationBuilder builder = new("com.example.debuglog", "Debug log", [Site + "/"], KazoWebGeneratedRoutes.Table);
        KazoWebDebugLogMemorySink sink = new();
        builder.Services.AddSingleton<IKazoWebDebugLogSink>(sink);
        builder.Services.AddSingleton<TimeProvider>(new FixedTime());
        logging(builder.Logging);
        await using KazoWebApplication app = builder.Build();
        app.MapGet("/hello", context =>
        {
            context.RequestServices.GetRequiredService<ILogger<Marker>>().LogInformation("Hello {Who}", "world");
            context.RequestServices.GetRequiredService<ILogger<Marker>>().LogDebug("details");
            return KazoResults.Ok("hi");
        });
        app.MapGet("/boom", IKazoActionResult () => throw new InvalidOperationException("kaboom"));

        await app.ProcessAsync(KazoWebRequestData.Create("GET", Site + "/hello"));
        KazoWebResponseData failed = await app.ProcessAsync(KazoWebRequestData.Create("GET", Site + "/boom"));
        Assert.Equal(500, failed.StatusCode);
        return sink;
    }

    [Fact]
    public async Task Nothing_is_written_until_the_site_enables_its_debug_log()
    {
        KazoWebDebugLogMemorySink sink = await RunAsync(_ => { });

        Assert.Empty(sink.Lines);
    }

    [Fact]
    public async Task The_debug_log_holds_requests_logger_lines_and_unhandled_errors()
    {
        KazoWebDebugLogMemorySink sink = await RunAsync(logging => logging.EnableDebugLog());

        Assert.Contains(sink.Lines, line => line.Contains("info: ", StringComparison.Ordinal) && line.Contains("Request starting HTTP/1.1 GET http://debuglog.hack/hello", StringComparison.Ordinal));
        Assert.Contains(sink.Lines, line => line.Contains("Request finished HTTP/1.1 GET http://debuglog.hack/hello - 200", StringComparison.Ordinal));
        Assert.Contains($"2026-10-17T01:22:14.123Z info: {Category}[0] Hello world", sink.Lines);
        Assert.Contains($"2026-10-17T01:22:14.123Z dbug: {Category}[0] details", sink.Lines);
        Assert.Contains(sink.Lines, line => line.Contains("fail: ", StringComparison.Ordinal) && line.Contains("kaboom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_minimum_level_and_the_request_lines_can_be_chosen()
    {
        KazoWebDebugLogMemorySink sink = await RunAsync(logging =>
        {
            logging.EnableDebugLog(LogLevel.Information);
            logging.LogRequests = false;
        });

        Assert.Contains(sink.Lines, line => line.EndsWith("Hello world", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, line => line.Contains("details", StringComparison.Ordinal));
        Assert.DoesNotContain(sink.Lines, line => line.Contains("Request starting", StringComparison.Ordinal));
    }
}
