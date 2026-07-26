import assert from "node:assert/strict";
import { resolve } from "node:path";
import test from "node:test";

interface ForgeConfig {
  packagerConfig: {
    executableName?: string;
  };
  makers: Array<{
    name: string;
    config?: {
      options?: {
        bin?: string;
      };
    };
  }>;
}

const forgeConfig = require(resolve(process.cwd(), "forge.config.cjs")) as ForgeConfig;
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
