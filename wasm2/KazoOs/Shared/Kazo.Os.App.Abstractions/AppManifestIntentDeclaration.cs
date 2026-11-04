namespace Kazo.Os.App.Abstractions;

/// <summary>
/// Declares one custom typed intent an application supports, beyond the core intents defined in
/// <see cref="AppIntentIds"/> which every app may receive implicitly.
/// </summary>
/// <param name="IntentId">Stable namespaced intent identifier, e.g. <c>org.hackeros.intent.launch-app.v1</c>.</param>
/// <param name="PayloadSchemaAssetPath">
/// Package-relative path, declared in <see cref="AppManifest.Assets"/>, to the JSON Schema describing
/// the intent's payload contract, or <see langword="null"/> when the intent carries no payload.
/// </param>
public sealed record AppIntentDeclarationManifest(string IntentId, string? PayloadSchemaAssetPath = null);
