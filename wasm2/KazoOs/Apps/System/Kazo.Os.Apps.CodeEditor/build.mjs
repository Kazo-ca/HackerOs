import { build } from "esbuild";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";

// esbuild can't bundle an ESM entry point and IIFE web workers in a single
// call (one output format per invocation), and Monaco's editor.js creates
// workers with `new Worker(url)` (classic scripts), so the worker bundles
// must be IIFE while the main module stays ESM for Blazor's dynamic import().
const outdir = "wwwroot/monaco";

rmSync(outdir, { recursive: true, force: true });
mkdirSync(outdir, { recursive: true });

const shared = {
  bundle: true,
  minify: true,
  target: "es2020",
  outdir,
  entryNames: "[name]",
  assetNames: "[name]",
  loader: { ".ttf": "file" },
  logLevel: "info"
};

await build({
  ...shared,
  format: "esm",
  entryPoints: { "monaco.bundle": "CodeEditor.monaco.entry.js" },
  metafile: false
});

await build({
  ...shared,
  format: "iife",
  entryPoints: {
    "editor.worker": "node_modules/monaco-editor/esm/vs/editor/editor.worker.js",
    "json.worker": "node_modules/monaco-editor/esm/vs/language/json/json.worker.js",
    "css.worker": "node_modules/monaco-editor/esm/vs/language/css/css.worker.js",
    "html.worker": "node_modules/monaco-editor/esm/vs/language/html/html.worker.js",
    "ts.worker": "node_modules/monaco-editor/esm/vs/language/typescript/ts.worker.js"
  }
});

// The outdir wipe above would otherwise discard this on every rebuild, so it
// is regenerated here rather than hand-maintained in wwwroot.
const monacoLicense = readFileSync("node_modules/monaco-editor/LICENSE", "utf8");
writeFileSync(
  `${outdir}/monaco.bundle.LICENSE.txt`,
  "Monaco Editor and bundled language services\n" +
    "============================================\n\n" +
    "The locally bundled editor includes the pinned monaco-editor package and its\n" +
    "web workers, recorded in ../../package-lock.json.\n\n" +
    monacoLicense
);

