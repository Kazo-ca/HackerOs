import { build } from "esbuild";
import { mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";

// Bundles xterm.js and its fit addon into one ES module that TerminalPanel.razor.js imports
// (served as _content/Kazo.Os.AppSdk.Terminal/xterm/xterm.bundle.js). The CSS imported by
// the entry is emitted next to it as xterm.bundle.css. Run `npm ci && npm run build` after
// changing a pinned version in package.json; the output is committed (ADR 0061).
const outdir = "wwwroot/xterm";

rmSync(outdir, { recursive: true, force: true });
mkdirSync(outdir, { recursive: true });

await build({
  bundle: true,
  minify: true,
  target: "es2020",
  format: "esm",
  outdir,
  entryNames: "[name]",
  entryPoints: { "xterm.bundle": "TerminalPanel.xterm.entry.js" },
  logLevel: "info"
});

// The outdir wipe above would otherwise discard this on every rebuild, so it is regenerated
// here rather than hand-maintained in wwwroot.
const license = (name) => readFileSync(`node_modules/${name}/LICENSE`, "utf8");
writeFileSync(
  `${outdir}/xterm.bundle.LICENSE.txt`,
  "xterm.js and its fit addon\n" +
    "==========================\n\n" +
    "The locally bundled terminal includes the pinned @xterm/xterm and @xterm/addon-fit\n" +
    "packages, recorded in ../../package-lock.json.\n\n" +
    "@xterm/xterm\n------------\n\n" +
    license("@xterm/xterm") +
    "\n\n@xterm/addon-fit\n----------------\n\n" +
    license("@xterm/addon-fit")
);
