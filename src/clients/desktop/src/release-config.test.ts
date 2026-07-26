import assert from "node:assert/strict";
import { resolve } from "node:path";
import test from "node:test";

interface ForgeConfig {
  packagerConfig: {
    executableName?: string;
  };
  makers: Array<{
    name: string;
    platforms?: string[];
    config?: {
      options?: {
        bin?: string;
      };
    };
  }>;
}

const forgeConfigPath = resolve(process.cwd(), "forge.config.cjs");

function loadForgeConfig(flavor: "full" | "client"): ForgeConfig {
  const originalFlavor = process.env.AGW_PACKAGE_FLAVOR;
  process.env.AGW_PACKAGE_FLAVOR = flavor;
  delete require.cache[require.resolve(forgeConfigPath)];
  try {
    return require(forgeConfigPath) as ForgeConfig;
  } finally {
    if (originalFlavor === undefined) delete process.env.AGW_PACKAGE_FLAVOR;
    else process.env.AGW_PACKAGE_FLAVOR = originalFlavor;
    delete require.cache[require.resolve(forgeConfigPath)];
  }
}

const forgeConfig = loadForgeConfig("full");
const packageManifest = require(resolve(process.cwd(), "package.json")) as {
  author: string;
  devDependencies: Record<string, string>;
};

test("Deb maker targets the packaged Linux executable", () => {
  const debMaker = forgeConfig.makers.find((maker) => maker.name === "@electron-forge/maker-deb");

  assert.ok(debMaker);
  assert.equal(debMaker.config?.options?.bin, forgeConfig.packagerConfig.executableName);
});

test("Electron package declares its required author metadata", () => {
  assert.equal(packageManifest.author, "Agw");
});

test("Squirrel maker installs its Windows installer backend directly", () => {
  assert.equal(packageManifest.devDependencies["electron-winstaller"], "5.4.4");
  assert.doesNotThrow(() => require.resolve("electron-winstaller", { paths: [process.cwd()] }));
});

test("Client flavor adds a Windows portable ZIP", () => {
  const clientZipMaker = loadForgeConfig("client").makers.find(
    (maker) => maker.name === "@electron-forge/maker-zip",
  );
  const fullZipMaker = forgeConfig.makers.find(
    (maker) => maker.name === "@electron-forge/maker-zip",
  );

  assert.deepEqual(clientZipMaker?.platforms, ["win32"]);
  assert.equal(fullZipMaker, undefined);
  assert.equal(packageManifest.devDependencies["@electron-forge/maker-zip"], "7.11.2");
  assert.doesNotThrow(() =>
    require.resolve("@electron-forge/maker-zip", { paths: [process.cwd()] }),
  );
});
