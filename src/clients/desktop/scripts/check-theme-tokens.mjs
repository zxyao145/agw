// 构建检查：推送给 extension 的主题变量（THEME_TOKEN_NAMES）在 renderer 构建的 CSS 中都有定义。
// Build check: every theme variable pushed to extensions (THEME_TOKEN_NAMES) is defined in the renderer's built CSS.
import { readdir, readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { parse, walk } from "css-tree";
import { build } from "esbuild";

const desktopDirectory = resolve(fileURLToPath(new URL("..", import.meta.url)));
const staticDirectory = join(desktopDirectory, "renderer", "out", "_next", "static");

async function loadTokenNames() {
  const moduleFile = join(desktopDirectory, ".scratch", "check-theme-tokens", "theme-tokens.mjs");
  await build({
    entryPoints: [join(desktopDirectory, "src", "shared", "theme-tokens.ts")],
    outfile: moduleFile,
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node24",
    logLevel: "warning",
  });
  return (await import(`${pathToFileURL(moduleFile).href}?t=${Date.now()}`)).THEME_TOKEN_NAMES;
}

export async function checkThemeTokens() {
  const files = (await readdir(staticDirectory, { recursive: true, withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".css"))
    .map((entry) => join(entry.parentPath, entry.name));
  if (files.length === 0) throw new Error(`No built CSS was found under ${staticDirectory}.`);
  const defined = new Set();
  for (const file of files) {
    walk(parse(await readFile(file, "utf8"), { parseValue: false }), {
      visit: "Declaration",
      enter(node) {
        if (node.property.startsWith("--")) defined.add(node.property);
      },
    });
  }
  const missing = (await loadTokenNames()).filter((name) => !defined.has(name));
  if (missing.length > 0) {
    throw new Error(
      `The built renderer CSS does not define these theme variables: ${missing.join(", ")}`,
    );
  }
  return files;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const files = await checkThemeTokens();
  console.log(`Theme variables are defined in ${files.length} built CSS file(s).`);
}
