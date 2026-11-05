> [Version française](../../../../fr/wasm/dev/adr/0229-kazo-os-display-name-and-new-address.md)

# ADR 0229: "Kazo OS" with a space, `Os-App-*` repositories, redirect to os.kazo.ca

## Status

Accepted on 2026-11-04 (asked by Benoit, series `rename/base`, branch
`rename/2b-display-space`).

- **Complements:** [ADR 0228](0228-rename-to-kazoos.md).
- **Replaces:** the displayed name "KazoOS" of ADR 0228 (context, point 1,
  and §1) and work item 7 of ADR 0228 §3 ("KazoOS has moved" notice and
  restoring a backup).

## Context

After ADR 0228, Benoit (2026-11-04):

1. On the social image: "A space is missing between Kazo and Os in the
   image", then he chose **"Kazo OS"** with a space.
2. Names of the app repositories: he first chose `Kazo.Os.App.*`, then
   renamed every repository himself by removing "Hacker": **`Os-App-*`**,
   `Os-AppTemplate`, `Os-AppMarketPlace` ("I renamed all the git
   repositories and removed the `Hacker` from `HackerOs` in the name").
3. Site address: "we drop the migration. But we must do a redirect". The
   redirect is done by Cloudflare; "I do the Cloudflare redirect in another
   session".

## Decision

1. **Displayed name: "Kazo OS".** Title, PWA, sign-in screen,
   notifications, app descriptions, readmes and current docs say "Kazo OS".
   Technical names do not change: `Kazo.Os.*`, `Kazo*`, `kazoos`
   (databases, paths, `.kazoos` extension).
2. **Repositories.** The main repository is `Kazo-ca/Os`. App repositories
   lose "Hacker": `HackerOs-App-Chess` → `Os-App-Chess` (same
   for Webamp, IRC, EmulatorJS, BoxedWine), `HackerOs-AppTemplate` →
   `Os-AppTemplate`, `HackerOs-AppMarketPlace` →
   `Os-AppMarketPlace`. Benoit renamed them; the links are fixed in
   work item 3 and their code in work item 8.
3. **Site address.** `os.kazo.ca` is the only address served by GitHub
   Pages. `hackos.kazo.ca` is redirected by a Cloudflare Worker: it answers
   `/service-worker.js` with a script that clears the cache, unregisters
   itself and sends the tabs to `os.kazo.ca`, and redirects everything else
   with the same path (PR #362). **No data recovery**: the accounts and
   files the browser keeps on `hackos.kazo.ca` are no longer reachable after
   the switch (another address is another origin for the browser).
4. Work item 7 is therefore limited to the `CNAME` of the GitHub Pages
   publication, and the links and docs that still point to
   `hackos.kazo.ca`.
5. The migration of work item 6 stays: it serves every host that keeps its
   address (installable server, personal deployment).

## Consequences

- Users of `hackos.kazo.ca` start again from an empty machine on
  `os.kazo.ca`. Those who want to keep their data make a backup before the
  switch.
- ADRs 0228 and 0229 are read together: the mapping of technical names stays
  the one of ADR 0228.
