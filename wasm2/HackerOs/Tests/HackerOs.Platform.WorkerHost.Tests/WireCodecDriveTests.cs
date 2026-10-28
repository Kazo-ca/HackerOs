using System.Text.Json;
using HackerOs.AppSdk.IO;
using HackerOs.Platform.WorkerHost.Protocol;
using Xunit;

namespace HackerOs.Platform.WorkerHost.Tests;

/// <summary>ADR 0181: a network drive crosses the worker boundary (ADR 0070) with its type and readiness.</summary>
public sealed class WireCodecDriveTests
{
    [Fact]
    public void Drives_keep_their_type_and_readiness()
    {
        HackDriveSnapshot[] drives =
        [
            new("/", "idbfs", "hda", 100, 40),
            new("/mnt/Projects", "hostfs", "Projects", 0, 0) { DriveType = HackDriveType.Network, IsReady = false }
        ];

        IReadOnlyList<HackDriveSnapshot> copy = RoundTrip(writer => WireCodec.WriteDrives(writer, drives), WireCodec.ReadDrives);

        Assert.Equal(drives, copy);
    }

    [Fact]
    public void A_closed_dialog_crosses_as_no_drive_and_old_payloads_read_as_fixed_and_ready()
    {
        Assert.Null(RoundTrip(writer => WireCodec.WriteDrive(writer, null), WireCodec.ReadDrive));

        using JsonDocument old = JsonDocument.Parse("{\"d\":{\"n\":\"/\",\"f\":\"idbfs\",\"l\":\"hda\",\"t\":1,\"a\":1}}");
        HackDriveSnapshot drive = WireCodec.ReadDrive(old.RootElement)!;

        Assert.Equal(HackDriveType.Fixed, drive.DriveType);
        Assert.True(drive.IsReady);
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
