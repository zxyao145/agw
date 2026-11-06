import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";

interface ForgeConfig {
  packagerConfig: {
    executableName?: string;
    icon?: string;
    extraResource?: string[];
  };
  makers: Array<{
    name: string;
    platforms?: string[];
    config?: {
      icon?: string;
      setupIcon?: string;
      loadingGif?: string;
      iconUrl?: string;
      options?: {
        bin?: string;
        icon?: string;
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

for (const flavor of ["full", "client"] as const) {
  test(`DmgMaker_${flavor}_UsesAgwVolumeIcon`, () => {
    const maker = loadForgeConfig(flavor).makers.find(
      (entry) => entry.name === "@electron-forge/maker-dmg",
    );
    const icon = resolve(process.cwd(), "assets", "agw-logo.icns");

    assert.equal(maker?.config?.icon, icon);
    assert.ok(existsSync(icon));
  });

  test(`SquirrelMaker_${flavor}_UsesAgwInstallerBranding`, () => {
    const maker = loadForgeConfig(flavor).makers.find(
      (entry) => entry.name === "@electron-forge/maker-squirrel",
    );
    const icon = resolve(process.cwd(), "assets", "agw-logo.ico");
    const loadingGif = resolve(process.cwd(), "assets", "agw-installer.gif");

    assert.equal(maker?.config?.setupIcon, icon);
    assert.equal(maker?.config?.loadingGif, loadingGif);
    assert.equal(
      maker?.config?.iconUrl,
      "https://raw.githubusercontent.com/zxyao145/agw/main/src/clients/desktop/assets/agw-logo.ico",
    );
    assert.ok(existsSync(icon));
    assert.ok(existsSync(loadingGif));
  });

  test(`DebMaker_${flavor}_InstallsAgwLauncherIcon`, () => {
    const maker = loadForgeConfig(flavor).makers.find(
      (entry) => entry.name === "@electron-forge/maker-deb",
    );
    const icon = resolve(process.cwd(), "assets", "agw-logo.png");

    assert.equal(maker?.config?.options?.icon, icon);
    assert.ok(existsSync(icon));
  });

  test(`Packager_${flavor}_IncludesAgwApplicationIcons`, () => {
    const config = loadForgeConfig(flavor).packagerConfig;

    assert.equal(config.icon, resolve(process.cwd(), "assets", "agw-logo"));
    assert.ok(config.extraResource?.includes(resolve(process.cwd(), "assets")));
    for (const extension of ["ico", "icns", "png"]) {
      assert.ok(existsSync(`${config.icon}.${extension}`));
    }
  });
}
