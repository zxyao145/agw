// 构建 extension 内置 SDK：单文件 ES module、单文件类型定义与 manifest 的 JSON Schema。
// Builds the built-in extension SDK: a single ES module, a single type definition file and the manifest JSON Schema.
import { mkdir, rm, writeFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { generateDtsBundle } from "dts-bundle-generator";
import { build } from "esbuild";

const desktopDirectory = resolve(fileURLToPath(new URL("..", import.meta.url)));
// 只导出 connect、defineBackground、defineView 的浏览器入口。The browser entry exporting only connect, defineBackground and defineView.
const sdkEntry = resolve(
  desktopDirectory,
  "..",
  "packages",
  "extension-sdk",
  "src",
  "sdk",
  "index.ts",
);
// 当前只有 Extension API 版本 1。Only Extension API version 1 exists today.
const manifestVersion = 1;

export const extensionSdkDirectory = join(desktopDirectory, "resources", "extension-sdk");

export async function buildExtensionSdk() {
  const outputDirectory = join(extensionSdkDirectory, `v${manifestVersion}`);
  await rm(outputDirectory, { recursive: true, force: true });
  await mkdir(outputDirectory, { recursive: true });

  await build({
    entryPoints: [sdkEntry],
    outfile: join(outputDirectory, "sdk.js"),
    bundle: true,
    format: "esm",
    platform: "browser",
    target: "es2022",
    logLevel: "warning",
  });

  // SDK 的 tsconfig 把 `@agw/execution-core` 映射到源码，这些类型因此作为本地文件打包进 sdk.d.ts。
  // The SDK tsconfig maps `@agw/execution-core` to its sources, so those types are bundled into sdk.d.ts as local files.
  const [declarations] = generateDtsBundle(
    [{ filePath: sdkEntry, output: { noBanner: true, exportReferencedTypes: true } }],
    { preferredConfigPath: join(desktopDirectory, "scripts", "extension-sdk-dts.tsconfig.json") },
  );
  await writeFile(join(outputDirectory, "sdk.d.ts"), declarations, "utf8");

  const schemaModule = join(
    desktopDirectory,
    ".scratch",
    "extension-sdk-build",
    "manifest-schema.mjs",
  );
  await build({
    entryPoints: [join(desktopDirectory, "src", "shared", "extension-manifest.ts")],
    outfile: schemaModule,
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node24",
    logLevel: "warning",
  });
  const { extensionManifestJsonSchema } = await import(
    `${pathToFileURL(schemaModule).href}?t=${Date.now()}`
  );
  await writeFile(
    join(outputDirectory, "manifest.schema.json"),
    `${JSON.stringify(extensionManifestJsonSchema(), null, 2)}\n`,
    "utf8",
  );
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  await buildExtensionSdk();
}
