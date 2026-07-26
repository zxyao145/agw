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

test("Deb maker targets the packaged Linux executable", () => {
  const debMaker = forgeConfig.makers.find((maker) => maker.name === "@electron-forge/maker-deb");

  assert.ok(debMaker);
  assert.equal(debMaker.config?.options?.bin, forgeConfig.packagerConfig.executableName);
});
