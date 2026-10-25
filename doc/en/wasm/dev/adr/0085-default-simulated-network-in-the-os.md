# ADR 0085: the default simulated network is wired into the OS

## Status

Accepted on 2026-10-16, with the fix of the browser home page
(branch `web/browser-home-page-g9yyu5`).

- **Supersedes:** the sentence of [ADR 0034](0034-wire-terminal-command-catalog.md)
  "`EcosystemServiceCollectionExtensions.cs` registers
  `ISimulatedWebsiteRegistry`/`ISimulatedNetworkService` from this seed". The
  rest of ADR 0034 (command catalog, `.hackeros` test hosts) is
  kept.
- **Does not touch:** [ADR 0023](0023-optional-game-domain-and-proxy-fallback.md).
  The sites of the default catalog are part of the OS's simulated network, not of
  the game domain (missions, contracts), which remains optional.
- **Documentation:** [`listening-ports-and-dns.md`](../../framework/listening-ports-and-dns.md),
  [`browser-html-rendering.md`](../browser-html-rendering.md).

## Context

The browser opens on `https://hackersearch.net` and sends the searches
typed in the address bar to `https://hackersearch.net/search?q=`. Its
bookmarks also point to HackMail, CryptoBank, the darknet market and the
forum. These hosts exist in `DefaultSimulatedHostCatalog`, and their sites
in `Network/Websites/` (work item `web/2` for `/api/search` and the
form). But the Ecosystem host composed the simulated network only with
`SmokeTestNetworkSeed` (`example.hackeros`, `empty.hackeros`). Only the unit
tests (`Wave4NetworkTests`) wired the default catalog.

Consequence: in all three hosts, the browser home page displayed
"This site can't be reached" (`ERR_NAME_NOT_RESOLVED`), like `curl
hackersearch.net` or `ping hackmail.com`. `browser-html-rendering.md` noted it
as a known problem.

## Decision

1. `DefaultSimulatedHostCatalog.BuildWebsites()` returns the controllers of the
   catalog's sites (HackerSearch, HackMail, CryptoBank, darknet market, forum),
   a new instance per call, hence sessions specific to each composed
   network.
2. `AddHackerOsEcosystem` composes the simulated network with
   `DefaultSimulatedHostCatalog.Build()` **and** `SmokeTestNetworkSeed.Hosts`,
   and the site registry with their controllers. The `.hackeros` hosts
   remain: the E2E tests and the documentation use them.
3. The default catalog keeps its `localhost` host (`127.0.0.1`, ssh 22 and
   mysql 3306 without PID, ADR 0074). The `hackdns` resolver always answers
   `localhost` with the loopback before consulting the simulated network: listening
   apps keep priority.

## Consequences

- The browser displays HackerSearch on opening and the address bar search
  gives results. `curl`, `ping`, `nmap` and `nslookup` see
  the catalog's hosts.
- The hosts and addresses are those of `src/core/network.ts` (`192.168.1.x`), with no
  collision with the `10.0.0.x` of the test set.
- The test `Tests/HackerOs.UI.E2E.Tests/BrowserHomePageTests.cs` opens the
  browser outside demo mode and verifies the home page then a search.
- A future content pack (ADR 0023) could add hosts to the same
  registry; this is not settled here.
