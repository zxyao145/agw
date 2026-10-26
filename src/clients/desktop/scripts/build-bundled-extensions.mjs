// 构建 src/clients/packages/extensions 下的内置 extension，校验后复制到 resources/extensions/<id>。
// Builds the bundled extensions in src/clients/packages/extensions, validates them and copies them into
// resources/extensions/<id>.
import { spawn } from "node:child_process";
import { cp, mkdir, readdir, readFile, rm } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { parse } from "acorn";
import { full } from "acorn-walk";
import { build } from "esbuild";

const desktopDirectory = resolve(fileURLToPath(new URL("..", import.meta.url)));
const clientsDirectory = resolve(desktopDirectory, "..");
const sourcesDirectory = join(clientsDirectory, "packages", "extensions");
const pnpmCommand = process.platform === "win32" ? "cmd.exe" : "pnpm";
const pnpmArguments = process.platform === "win32" ? ["/d", "/s", "/c", "pnpm.cmd"] : [];
const SDK_SPECIFIER = "/__agw/sdk.js";
// 宿主 `/__agw/sdk.js` 导出的名称。Names exported by the host's `/__agw/sdk.js`.
const SDK_EXPORTS = new Set(["connect", "defineBackground", "defineView"]);

export const bundledExtensionsDirectory = join(desktopDirectory, "resources", "extensions");

function run(command, args, cwd) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, { cwd, stdio: "inherit" });
    child.once("error", reject);
    child.once("exit", (code) =>
      code === 0
        ? resolvePromise()
        : reject(new Error(`${command} ${args.join(" ")} exited with ${code}`)),
    );
  });
}

/**
 * 打包 Desktop 读取 extension 目录时使用的校验：manifest、包内路径、图标，以及 JSON 与 CSS 主题的加载。
 * Bundles the validation Desktop runs when it reads an extension directory: the manifest, package paths, icons, and
 * JSON and CSS theme loading.
 */
async function loadManifestTools() {
  const moduleFile = join(desktopDirectory, ".scratch", "bundled-extensions-build", "manifest.mjs");
  await build({
    stdin: {
      contents:
        'export { readExtensionDirectory } from "./src/main/extensions/extension-catalog.ts";',
      resolveDir: desktopDirectory,
      loader: "ts",
    },
    outfile: moduleFile,
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node24",
    // postcss 等 CommonJS 依赖在 ESM 产物中通过 require 读取 Node 内置模块。CommonJS dependencies such as postcss
    // require Node built-in modules from the ESM output.
    banner: {
      js: 'import { createRequire } from "node:module"; const require = createRequire(import.meta.url);',
    },
    logLevel: "warning",
  });
  return import(`${pathToFileURL(moduleFile).href}?t=${Date.now()}`);
}

async function javascriptFiles(directory) {
  const entries = await readdir(directory, { recursive: true, withFileTypes: true });
  return entries
    .filter((entry) => entry.isFile() && /\.m?js$/u.test(entry.name))
    .map((entry) => join(entry.parentPath, entry.name));
}

/**
 * 产物只能从 SDK 导入 connect、defineBackground、defineView；其他名称在运行时不存在。
 * Built code may import only connect, defineBackground and defineView from the SDK; other names do not exist at runtime.
 */
async function checkSdkImports(id, distDirectory) {
  for (const file of await javascriptFiles(distDirectory)) {
    const program = parse(await readFile(file, "utf8"), {
      ecmaVersion: "latest",
      sourceType: "module",
    });
    full(program, (node) => {
      const source = node.source?.value;
      if (source !== SDK_SPECIFIER) return;
      if (node.type === "ImportExpression" || node.type === "ExportAllDeclaration") {
        throw new Error(
          `${id}: ${file} imports the whole SDK module; import named functions instead.`,
        );
      }
      for (const specifier of node.specifiers ?? []) {
        const imported = specifier.imported ?? specifier.local;
        const name =
          specifier.type === "ImportSpecifier" || specifier.type === "ExportSpecifier"
            ? (imported.name ?? imported.value)
            : specifier.type;
        if (!SDK_EXPORTS.has(name)) {
          throw new Error(`${id}: ${file} imports "${name}" from ${SDK_SPECIFIER}.`);
        }
      }
    });
  }
}

/**
 * 校验一个内置 extension 的 dist/，返回 manifest id。源码目录以 extension 名称命名，dist/ 的目录名都不是 id。
 * Validates one bundled extension's dist/ and returns the manifest id. Source directories are named after the
 * extension name, so neither they nor dist/ are the id.
 */
async function validatePackage(tools, appVersion, name, distDirectory) {
  const entry = await tools.readExtensionDirectory(distDirectory, appVersion, null);
  if (entry.validation !== "valid") {
    throw new Error(`${name}: the package is ${entry.validation}: ${JSON.stringify(entry.errors)}`);
  }
  await checkSdkImports(name, distDirectory);
  return entry.manifest.id;
}

export async function buildBundledExtensions() {
  const names = (await readdir(sourcesDirectory, { withFileTypes: true }))
    .filter((entry) => entry.isDirectory())
    .map((entry) => entry.name)
    .sort();
  await run(
    pnpmCommand,
    [...pnpmArguments, "--filter", "./packages/extensions/*", "run", "build"],
    clientsDirectory,
  );

  const tools = await loadManifestTools();
  const { version } = JSON.parse(await readFile(join(desktopDirectory, "package.json"), "utf8"));
  // Desktop 要求 resources/extensions 下的目录名等于 manifest id。
  // Desktop requires each directory under resources/extensions to be named after its manifest id.
  const namesById = new Map();
  for (const name of names) {
    const id = await validatePackage(tools, version, name, join(sourcesDirectory, name, "dist"));
    if (namesById.has(id)) {
      throw new Error(`${name}: the manifest id "${id}" is also used by ${namesById.get(id)}.`);
    }
    namesById.set(id, name);
  }

  await rm(bundledExtensionsDirectory, { recursive: true, force: true });
  await mkdir(bundledExtensionsDirectory, { recursive: true });
  for (const [id, name] of namesById) {
    await cp(join(sourcesDirectory, name, "dist"), join(bundledExtensionsDirectory, id), {
      recursive: true,
    });
  }
  return names;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const names = await buildBundledExtensions();
  console.log(`Bundled extensions: ${names.join(", ")}`);
}
