> [Version française](../../../fr/wasm/memory/manifest-serializer-notes.md)

- Implemented a strict source-generated AppManifest JSON serializer in wasm2/KazoOs/Shared/Kazo.Os.App.Abstractions/AppManifestJsonSerializer.cs.
- The serializer uses lower-camel property names, lower-camel enum values, rejects unknown/duplicate properties, and emits canonical single-LF JSON.
- Added golden fixture coverage in wasm2/KazoOs/Shared/Kazo.Os.App.Abstractions/Schema/Fixtures/app-manifest.canonical.json plus focused tests in wasm2/KazoOs/Tests/Kazo.Os.App.Abstractions.Tests/Serialization/AppManifestJsonSerializerTests.cs.
- Full solution validation command: dotnet test Kazo.Os.sln --no-restore.
