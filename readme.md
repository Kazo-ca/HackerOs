> [Version française](readme-fr.md)

# Kazo OS

Kazo OS is a **simulated operating system that runs in the browser**, written
in C# with Blazor WebAssembly (.NET 10). It provides a complete environment for
building and running applications: desktop and windows, a persistent file
system, processes, a POSIX shell and Linux commands, a simulated network,
internal web sites, and a **public SDK** (NuGet packages) to create your own
apps, commands, services and sites.

- **Try it**: <https://hackos.kazo.ca/> (PWA, works offline).
- **Features**: [Kazo OS overview and features](doc/en/wasm/features.md): what Kazo OS offers today, the goal of the ecosystem, ways to deploy it, and what is in development.
- **Code**: [`wasm2/KazoOs/`](wasm2/KazoOs/README.md).
- **Documentation**: [`doc/en/wasm/README.md`](doc/en/wasm/README.md) (French: [`doc/fr/wasm/README.md`](doc/fr/wasm/README.md)).
- Source repository: [Kazo-ca/Os](https://github.com/Kazo-ca/Os).

## Getting started

Prerequisite: the .NET 10 SDK (`wasm2/KazoOs/global.json`).

```bash
cd wasm2/KazoOs
dotnet build Kazo.Os.sln
dotnet test Kazo.Os.sln
dotnet run --project OS/Kazo.Os.Ecosystem     # the OS as a standalone PWA
```

Debug hosts, the trimmed publish, known workarounds and how to run the E2E
tests are described in
[`building-and-testing.md`](doc/en/wasm/dev/building-and-testing.md).

## Documentation

| To… | Read |
| --- | --- |
| Discover what Kazo OS offers and what is coming | [`features.md`](doc/en/wasm/features.md) |
| Understand the OS code | [`project-overview.md`](doc/en/wasm/dev/project-overview.md), then [`doc/en/wasm/dev/`](doc/en/wasm/dev/README.md) |
| Write an app, a command or a service | [`sdk-developer-guide.md`](doc/en/wasm/framework/sdk-developer-guide.md) and [`sdk-reference.md`](doc/en/wasm/framework/sdk-reference.md) |
| Build on Kazo OS from another repository | [`building-on-hackeros.md`](doc/en/wasm/framework/building-on-hackeros.md) |
| Create an internal web site (ASP.Hack, Blazor) | [`asp-hack-developer-guide.md`](doc/en/wasm/framework/asp-hack-developer-guide.md) |
| Avoid problems already met | [`pitfalls.md`](doc/en/wasm/dev/pitfalls.md) |
| Know the project state and its conventions | [`doc/en/wasm/memory/`](doc/en/wasm/memory/README.md) |
| Architecture decisions | [`doc/en/wasm/dev/adr/`](doc/en/wasm/dev/adr/README.md) |

## Repository layout

| Folder | Contents |
| --- | --- |
| [`wasm2/KazoOs/`](wasm2/KazoOs/README.md) | **Active version**: the Kazo OS solution (OS, SDK, apps, hosts, tests). |
| [`doc/`](doc/README.md) | Documentation, in English ([`doc/en/`](doc/en/README.md)) and French ([`doc/fr/`](doc/fr/README.md)). |
| [`src/`](src/README.md) | Legacy TypeScript version, kept read-only as a reference. |
| [`.github/workflows/`](.github/workflows/) | Verification and GitHub Pages publishing. |
| `Art/` | Icons and screenshots. |

## Legacy version (TypeScript)

The first version, written in TypeScript with Webpack, stays in
[`src/`](src/README.md) and is published at <https://hackos.kazo.ca/legacy/>. It
no longer gets new features. Its documentation (building with Node.js 20,
layout, features) is in [`src/README.md`](src/README.md).

Both versions are published by a single
[GitHub Pages workflow](.github/workflows/deploy-to-pages.yml)
([deployment guide](doc/en/wasm/dev/github-pages-deployment.md)).

## Asking for a feature

1. Check the [features overview](doc/en/wasm/features.md): the feature may
   already exist or be in development.
2. Search the [open issues](https://github.com/Kazo-ca/Os/issues): if
   someone already asked for it, add your use case in a comment.
3. Otherwise, open an issue with the
   [**Feature request** form](https://github.com/Kazo-ca/Os/issues/new?template=feature_request.yml):
   describe the need (what you want to do and why), the solution you imagine
   and the part of the OS it touches. French or English are both fine.
4. The maintainer answers in the issue. To report a problem, use the
   [**Bug report** form](https://github.com/Kazo-ca/Os/issues/new?template=bug_report.yml)
   instead.

Details are in [`CONTRIBUTING.md`](CONTRIBUTING.md#asking-for-a-feature).

## Contributing

Contribution rules are in [`CONTRIBUTING.md`](CONTRIBUTING.md) and
[`AGENTS.md`](AGENTS.md) (where code goes, docs kept current with every change
in both languages, folder indexes, working with the maintainer: questions,
`TODO` issues, branches, merges). Claude Code reads them through
[`CLAUDE.md`](CLAUDE.md); the project's state and memory are in
[`doc/en/wasm/memory/`](doc/en/wasm/memory/README.md).

## License

See [`LICENSE.md`](LICENSE.md).
