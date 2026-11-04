using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.WorkerHost.Protocol;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Xunit;

namespace Kazo.Os.Platform.WorkerHost.Tests;

/// <summary>
/// ADR 0096: the intents' results and an intent delivered to a running process cross the worker
/// boundary (ADR 0070) unchanged.
/// </summary>
public sealed class WireCodecIntentTests
{
    [Fact]
    public void Intent_result_keeps_its_outcome_candidates_and_code()
    {
        AppIntentResult result = new(AppIntentOutcome.ChooserRequired, ["org.a", "org.b"], "intent.custom.chooser-required");

        AppIntentResult copy = RoundTrip(writer => WireCodec.WriteIntentResult(writer, result), WireCodec.ReadIntentResult);

        Assert.Equal(result.Outcome, copy.Outcome);
        Assert.Equal(result.CandidateAppIds, copy.CandidateAppIds);
        Assert.Equal(result.ErrorCode, copy.ErrorCode);
    }

    [Fact]
    public void Open_file_result_keeps_the_fall_back_to_open_flag()
    {
        AppIntentOpenFileResult result = new(AppIntentOpenFileOutcome.Opened, FellBackToOpen: true);

        AppIntentOpenFileResult copy = RoundTrip(writer => WireCodec.WriteOpenFileResult(writer, result), WireCodec.ReadOpenFileResult);

        Assert.True(copy.FellBackToOpen);
        Assert.Null(copy.CandidateAppIds);
    }

    [Fact]
    public void Intent_delivery_keeps_id_payload_sender_and_arguments()
    {
        AppIntentDelivery delivery = new("org.test.intent.share.v1", "{\"text\":\"hi\"}", "org.hackeros.shell")
        {
            Arguments = ["/home/alice", "--select=/home/alice/notes.txt"]
        };

        AppIntentDelivery copy = RoundTrip(
            writer => WireCodec.WriteIntentDelivery(writer, "it", delivery),
            root => WireCodec.ReadIntentDelivery(root.GetProperty("it")));

        Assert.Equal(delivery.IntentId, copy.IntentId);
        Assert.Equal(delivery.PayloadJson, copy.PayloadJson);
        Assert.Equal(delivery.SenderAppId, copy.SenderAppId);
        Assert.Equal(delivery.Arguments, copy.Arguments);
    }

    [Fact]
    public void Default_handlers_keep_target_action_app_and_layer()
    {
        // ADR 0121: cat.defaults carries both layers to xdg-mime in a worker.
        Kazo.Os.AppSdk.Apps.KazoFileAssociation[] rows =
        [
            new(".md", "open", "org.hackeros.code-editor", Kazo.Os.AppSdk.Apps.KazoFileAssociationLayer.User),
            new("x-scheme-handler/https", "open", "org.hackeros.browser", Kazo.Os.AppSdk.Apps.KazoFileAssociationLayer.System)
        ];

        IReadOnlyList<Kazo.Os.AppSdk.Apps.KazoFileAssociation> copy = RoundTrip(
            writer => WireCodec.WriteAssociations(writer, rows), WireCodec.ReadAssociations);

        Assert.Equal(rows, copy);
    }

    private static T RoundTrip<T>(Action<Utf8JsonWriter> write, Func<JsonElement, T> read)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
        return read(document.RootElement);
    }
}
