// 构建 extension 集成测试使用的 fixture：每个 fixture 输出为以 manifest id 命名的目录，另有 sample-v2.zip。
// Builds the extension integration test fixtures: one directory per fixture named after its manifest id, plus sample-v2.zip.
import { spawn } from "node:child_process";
import { createWriteStream } from "node:fs";
import { cp, mkdir, readdir, readFile, rm, stat, writeFile } from "node:fs/promises";
import { createRequire } from "node:module";
import { basename, dirname, join, relative, resolve, sep } from "node:path";
import { pipeline } from "node:stream/promises";
import { fileURLToPath, pathToFileURL } from "node:url";
import { parse } from "acorn";
import { full } from "acorn-walk";
import { build as esbuild } from "esbuild";
import { JSDOM } from "jsdom";
import { build as viteBuild } from "vite";
import createWabt from "wabt";
import yazl from "yazl";

import { buildExtensionSdk, extensionSdkDirectory } from "./build-extension-sdk.mjs";

const require = createRequire(import.meta.url);
const desktopDirectory = resolve(fileURLToPath(new URL("..", import.meta.url)));
const sourceDirectory = join(desktopDirectory, "test-fixtures", "extensions-src");
const buildDirectory = join(desktopDirectory, ".scratch", "extension-fixtures-build");
// 当前只有 Extension API 版本 1。Only Extension API version 1 exists today.
const sdkDirectory = join(extensionSdkDirectory, "v1");

export const defaultFixturesDirectory = join(
  desktopDirectory,
  ".scratch",
  "extension-tests",
  "fixtures",
);

/** 测试复制 fixture 时替换为本地 HTTP 服务端口的占位符。Replaced with the local HTTP service port when the test copies fixtures. */
export const HTTP_PORT_PLACEHOLDER = "__HTTP_PORT__";

// 宿主提供 SDK 的地址。Where the host serves the SDK.
const SDK_SPECIFIER = "/__agw/sdk.js";

// 不需要构建、按原样复制的 fixture。Fixtures copied as-is without a build step.
const PLAIN_FIXTURES = [
  "sample",
  "broken",
  "slow",
  "stuck-deactivate",
  "busy",
  "bad-view",
  "no-background",
  "undeclared",
  "wasm",
];
// 只在构建时使用、不进入 fixture 的源文件。Source files used by the build only and left out of the fixture.
const BUILD_ONLY_EXTENSIONS = [".wat"];

function run(command, args) {
  return new Promise((resolveRun, reject) => {
    const child = spawn(command, args, { stdio: "inherit" });
    child.on("error", reject);
    child.on("exit", (code, signal) => {
      if (code === 0) resolveRun();
      else reject(new Error(`${command} ${args.join(" ")} exited with ${signal ?? code}.`));
    });
  });
}

let manifestModule = null;

/** 与 build-extension-sdk.mjs 生成 schema 的方式相同，打包 manifest 校验模块。Bundles the manifest module the way build-extension-sdk.mjs does for the schema. */
function loadManifestModule() {
  manifestModule ??= (async () => {
    const outfile = join(buildDirectory, "extension-manifest.mjs");
    await esbuild({
      entryPoints: [join(desktopDirectory, "src", "shared", "extension-manifest.ts")],
      outfile,
      bundle: true,
      format: "esm",
      platform: "node",
      target: "node24",
      logLevel: "warning",
    });
    return import(`${pathToFileURL(outfile).href}?t=${Date.now()}`);
  })();
  return manifestModule;
}

/** 校验 manifest 文本，端口占位符按 1 处理。Validates manifest text with the port placeholder read as 1. */
async function validateManifest(text, label) {
  const { evaluateExtensionManifest } = await loadManifestModule();
  const { version } = JSON.parse(await readFile(join(desktopDirectory, "package.json"), "utf8"));
  const evaluation = evaluateExtensionManifest(
    JSON.parse(text.replaceAll(HTTP_PORT_PLACEHOLDER, "1")),
    version,
  );
  if (evaluation.validation !== "valid") {
    throw new Error(
      `${label}: manifest is ${evaluation.validation}: ${JSON.stringify(evaluation.errors)}`,
    );
  }
  return evaluation.manifest;
}

function packageFile(directory, path) {
  return join(directory, ...path.split("/"));
}

async function assertFile(path, label) {
  if (!(await stat(path)).isFile()) throw new Error(`${label} is not a file.`);
}

/**
 * 检查 HTML 符合 extension CSP：没有内联脚本与事件属性，脚本是外部 ES module（Blazor 的加载脚本除外）且文件存在。
 * Checks HTML against the extension CSP: no inline scripts or event handler attributes, and scripts are existing
 * external ES modules (Blazor's loader excepted).
 */
async function assertCspCompatibleHtml(directory, id) {
  const entries = await readdir(directory, { recursive: true });
  for (const entry of entries.filter((name) => name.endsWith(".html"))) {
    const path = entry.split(sep).join("/");
    const label = `${id}/${path}`;
    const dom = new JSDOM(await readFile(join(directory, entry), "utf8"), {
      url: `https://${id}.fixture.invalid/${path}`,
    });
    const { document } = dom.window;
    for (const element of document.querySelectorAll("*")) {
      for (const attribute of element.attributes) {
        if (attribute.name.startsWith("on")) {
          throw new Error(`${label}: inline event handler "${attribute.name}".`);
        }
      }
    }
    for (const script of document.querySelectorAll("script")) {
      if (script.textContent.trim() !== "" || !script.hasAttribute("src")) {
        throw new Error(`${label}: inline <script>.`);
      }
      const url = new URL(script.src);
      // Blazor 的加载脚本是外部经典脚本。Blazor's loader is an external classic script.
      const blazorLoader = id === "blazor" && url.pathname === "/_framework/blazor.webassembly.js";
      if (script.type !== "module" && !blazorLoader) {
        throw new Error(`${label}: script ${url.pathname} is not type="module".`);
      }
      if (!url.pathname.startsWith("/__agw/")) {
        const file = packageFile(directory, decodeURIComponent(url.pathname.slice(1)));
        await assertFile(file, `${label}: script ${url.pathname}`);
      }
    }
    dom.window.close();
  }
}

/** 校验输出目录：manifest 合格、id 与目录名相同、引用的包路径都存在、HTML 符合 CSP。Validates an output directory. */
async function validateFixture(directory) {
  const id = basename(directory);
  const manifest = await validateManifest(
    await readFile(join(directory, "manifest.json"), "utf8"),
    id,
  );
  if (manifest.id !== id) throw new Error(`${id}: manifest id is "${manifest.id}".`);
  const { manifestPackagePaths } = await loadManifestModule();
  for (const { path, field } of manifestPackagePaths(manifest)) {
    await assertFile(packageFile(directory, path), `${id}: ${field} (${path})`);
  }
  await assertCspCompatibleHtml(directory, id);
}

async function copyPlainFixture(id, outputDirectory) {
  await cp(join(sourceDirectory, id), join(outputDirectory, id), {
    recursive: true,
    filter: (source) => !BUILD_ONLY_EXTENSIONS.some((extension) => source.endsWith(extension)),
  });
}

/** 用 wabt 把 .wat 编译为 .wasm。Compiles .wat to .wasm with wabt. */
async function compileWat(source, target) {
  const wabt = await createWabt();
  const module = wabt.parseWat(basename(source), await readFile(source, "utf8"));
  try {
    module.validate();
    await writeFile(target, module.toBinary({}).buffer);
  } finally {
    module.destroy();
  }
}

/** 用 fixture 自己的 vite.config 构建，再加上 manifest.json。Builds with the fixture's own vite.config, then adds manifest.json. */
async function buildViteFixture(id, configFile, outputDirectory) {
  const root = join(sourceDirectory, id);
  const outDir = join(outputDirectory, id);
  await viteBuild({
    root,
    configFile: join(root, configFile),
    logLevel: "warn",
    build: { outDir, emptyOutDir: true },
  });
  await cp(join(root, "manifest.json"), join(outDir, "manifest.json"));
}

/**
 * 构建产物对 `/__agw/sdk.js` 的导入只有 `defineView`：单独安装的 extension 只使用公开 SDK 的视图入口，其余能力都来自
 * `defineView` 交给它的 api。
 * The built output imports only `defineView` from `/__agw/sdk.js`: a separately installed extension uses only the
 * public SDK's view entry, and every other capability comes from the api `defineView` hands it.
 */
async function assertSdkImportsOnlyDefineView(directory) {
  const id = basename(directory);
  const entries = await readdir(directory, { recursive: true });
  const imported = [];
  for (const entry of entries.filter((name) => name.endsWith(".js"))) {
    const label = `${id}/${entry.split(sep).join("/")}`;
    const program = parse(await readFile(join(directory, entry), "utf8"), {
      ecmaVersion: "latest",
      sourceType: "module",
    });
    full(program, (node) => {
      if (node.type === "ImportExpression" && node.source.type !== "Literal") {
        throw new Error(`${label}: a dynamic import with a computed specifier.`);
      }
      if (node.source?.value !== SDK_SPECIFIER) return;
      if (node.type !== "ImportDeclaration") {
        throw new Error(`${label}: ${node.type} of ${SDK_SPECIFIER}.`);
      }
      for (const specifier of node.specifiers) {
        if (specifier.type !== "ImportSpecifier") {
          throw new Error(`${label}: ${specifier.type} of ${SDK_SPECIFIER}.`);
        }
        imported.push(specifier.imported.name);
      }
    });
  }
  if (imported.length === 0 || imported.some((name) => name !== "defineView")) {
    throw new Error(
      `${id}: the build imports ${JSON.stringify(imported)} from ${SDK_SPECIFIER}; only defineView is allowed.`,
    );
  }
}

async function buildReactTs(outputDirectory) {
  const root = join(sourceDirectory, "react-ts");
  // 与 Extensions 页面的 "Save SDK types…" 相同，把类型与 schema 保存到 agw/。Like "Save SDK types…", save the types and schema to agw/.
  await mkdir(join(root, "agw"), { recursive: true });
  await cp(join(sdkDirectory, "sdk.d.ts"), join(root, "agw", "sdk.d.ts"));
  await cp(join(sdkDirectory, "manifest.schema.json"), join(root, "agw", "manifest.schema.json"));
  const tsc = join(dirname(require.resolve("typescript/package.json")), "bin", "tsc");
  await run(process.execPath, [tsc, "--noEmit", "-p", join(root, "tsconfig.json")]);
  await buildViteFixture("react-ts", "vite.config.mts", outputDirectory);
}

/** `dotnet publish` 输出的 wwwroot 加上 manifest.json 作为 extension 根目录。The published wwwroot plus manifest.json is the extension root. */
async function buildBlazor(outputDirectory) {
  const root = join(sourceDirectory, "blazor");
  const publishDirectory = join(buildDirectory, "blazor-publish");
  await rm(publishDirectory, { recursive: true, force: true });
  // bin、obj 放在 scratch 目录，源码目录保持干净。bin and obj go to the scratch directory, keeping the sources clean.
  await run("dotnet", [
    "publish",
    join(root, "AgwBlazorFixture.csproj"),
    "--configuration",
    "Release",
    "--output",
    publishDirectory,
    "--artifacts-path",
    join(buildDirectory, "blazor-artifacts"),
    "--nologo",
  ]);
  const outDir = join(outputDirectory, "blazor");
  await cp(join(publishDirectory, "wwwroot"), outDir, { recursive: true });
  await cp(join(root, "manifest.json"), join(outDir, "manifest.json"));
}

/**
 * 把 sample 目录打包为 zip（文件位于压缩包根目录）：版本 1.1.0，多声明 fs.local.read。
 * 测试替换端口占位符后可以用替换后的 sample 目录重新生成。
 * Packs a sample directory as a zip with files at the archive root: version 1.1.0 with fs.local.read added.
 * The test can regenerate it from a sample directory whose port placeholder was replaced.
 */
export async function writeSampleV2Zip(sampleDirectory, zipPath) {
  const manifest = JSON.parse(await readFile(join(sampleDirectory, "manifest.json"), "utf8"));
  const upgraded = {
    ...manifest,
    version: "1.1.0",
    permissions: [...manifest.permissions, "fs.local.read"],
  };
  const text = `${JSON.stringify(upgraded, null, 2)}\n`;
  await validateManifest(text, "sample-v2.zip");

  const zip = new yazl.ZipFile();
  const entries = await readdir(sampleDirectory, { recursive: true, withFileTypes: true });
  const files = entries
    .filter((entry) => entry.isFile())
    .map((entry) => join(entry.parentPath, entry.name))
    .sort();
  for (const file of files) {
    const name = relative(sampleDirectory, file).split(sep).join("/");
    if (name !== "manifest.json") zip.addFile(file, name);
  }
  zip.addBuffer(Buffer.from(text, "utf8"), "manifest.json");
  zip.end();
  await pipeline(zip.outputStream, createWriteStream(zipPath));
}

export async function buildExtensionFixtures(outputDirectory = defaultFixturesDirectory) {
  await buildExtensionSdk();
  await rm(outputDirectory, { recursive: true, force: true });
  await mkdir(outputDirectory, { recursive: true });

  for (const id of PLAIN_FIXTURES) await copyPlainFixture(id, outputDirectory);
  await compileWat(
    join(sourceDirectory, "wasm", "add.wat"),
    join(outputDirectory, "wasm", "app.wasm"),
  );
  for (const id of PLAIN_FIXTURES) await validateFixture(join(outputDirectory, id));

  await buildReactTs(outputDirectory);
  await validateFixture(join(outputDirectory, "react-ts"));
  await buildViteFixture("vue", "vite.config.mjs", outputDirectory);
  await validateFixture(join(outputDirectory, "vue"));
  // 测试用 "Install from folder" 安装 todo-editor，不预先放进安装目录。The test installs todo-editor with
  // "Install from folder"; it is not placed in the install directory beforehand.
  await buildViteFixture("todo-editor", "vite.config.mjs", outputDirectory);
  await validateFixture(join(outputDirectory, "todo-editor"));
  await assertSdkImportsOnlyDefineView(join(outputDirectory, "todo-editor"));
  await buildBlazor(outputDirectory);
  await validateFixture(join(outputDirectory, "blazor"));

  await writeSampleV2Zip(join(outputDirectory, "sample"), join(outputDirectory, "sample-v2.zip"));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await buildExtensionFixtures(defaultFixturesDirectory);
  console.log(`Extension fixtures: ${defaultFixturesDirectory}`);
}
