using System.Text.Json.Serialization;
using Kazo.Os.AppSdk;
using Kazo.Os.Samples.ServiceApp;

// ADR 0124, section 2: the service runs in a Web Worker, so its topic payload travels to the OS (and to
// subscribers in other processes) as JSON, with metadata generated at build time.
[assembly: HackWireContext(typeof(SampleTickerJsonContext))]

namespace Kazo.Os.Samples.ServiceApp;

/// <summary>Serializer context of the payloads this app publishes (<see cref="SampleTickerEvent"/>).</summary>
[JsonSerializable(typeof(SampleTickerEvent))]
internal sealed partial class SampleTickerJsonContext : JsonSerializerContext;
