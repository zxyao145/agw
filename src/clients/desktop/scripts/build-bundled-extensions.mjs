// 构建 src/clients/packages/extensions 下的内置 extension，校验后复制到 resources/extensions/<id>。
// Builds the bundled extensions in src/clients/packages/extensions, validates them and copies them into
// resources/extensions/<id>.
import { spawn } from "node:child_process";
import { access, cp, mkdir, readdir, readFile, rm } from "node:fs/promises";
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

/** 与 build-extension-sdk.mjs 相同地打包 manifest 校验模块。Bundles the manifest module as build-extension-sdk.mjs does. */
async function loadManifestTools() {
  const moduleFile = join(desktopDirectory, ".scratch", "bundled-extensions-build", "manifest.mjs");
  await build({
    entryPoints: [join(desktopDirectory, "src", "shared", "extension-manifest.ts")],
    outfile: moduleFile,
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node24",
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

async function validatePackage(tools, appVersion, name, distDirectory) {
  const manifest = JSON.parse(await readFile(join(distDirectory, "manifest.json"), "utf8"));
  const result = tools.evaluateExtensionManifest(manifest, appVersion);
  if (result.validation !== "valid") {
    throw new Error(
      `${name}: the manifest is ${result.validation}: ${JSON.stringify(result.errors)}`,
    );
  }
  if (result.manifest.id !== name) {
    throw new Error(
      `${name}: the manifest id "${result.manifest.id}" must equal the package directory name.`,
    );
  }
  for (const { path, field } of tools.manifestPackagePaths(result.manifest)) {
    await access(join(distDirectory, ...path.split("/"))).catch(() => {
      throw new Error(`${name}: ${field} refers to ${path}, which is not in dist/.`);
    });
  }
  await checkSdkImports(name, distDirectory);
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
  for (const name of names) {
    await validatePackage(tools, version, name, join(sourcesDirectory, name, "dist"));
  }

  await rm(bundledExtensionsDirectory, { recursive: true, force: true });
  await mkdir(bundledExtensionsDirectory, { recursive: true });
  for (const name of names) {
    await cp(join(sourcesDirectory, name, "dist"), join(bundledExtensionsDirectory, name), {
      recursive: true,
    });
  }
  return names;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const names = await buildBundledExtensions();
  console.log(`Bundled extensions: ${names.join(", ")}`);
}
