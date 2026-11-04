using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Web.Mvc;

namespace Kazo.Os.AppSdk.Web.Hosting;

/// <summary>
/// The JSON settings of a site: <see cref="HackObjectResult"/>, <see cref="HackJsonResult"/> and
/// <c>[FromBody]</c> use them (ASP.NET's <c>JsonOptions</c>). The web defaults apply: camelCase,
/// case-insensitive reads, numbers read from strings.
/// </summary>
/// <remarks>
/// For a trimmed publish, add the site's source-generated context first, as an ASP.NET Core
/// native AOT app does:
/// <code>builder.Json.SerializerOptions.TypeInfoResolverChain.Insert(0, IntranetJsonContext.Default);</code>
/// Without it, types are read by reflection, which works until the trimmer removes their members.
/// </remarks>
public sealed class HackWebJsonOptions
{
    /// <summary>Gets the options used when no site options are registered.</summary>
    internal static JsonSerializerOptions DefaultOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the serializer options.</summary>
    public JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web);
}

/// <summary>Source-generated metadata of the framework's own JSON (problem details).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HackProblemDetails))]
[JsonSerializable(typeof(HackValidationProblemDetails))]
[JsonSerializable(typeof(Dictionary<string, string[]>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
internal sealed partial class HackWebJsonContext : JsonSerializerContext;
