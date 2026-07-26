const path = require("node:path");
const packageManifest = require("./package.json");

const flavor = process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full";
const arch = process.env.AGW_TARGET_ARCH || process.arch;
const releaseVersion = process.env.AGW_RELEASE_VERSION || packageManifest.version;

module.exports = {
  packagerConfig: {
    name: "Agw Desktop",
    executableName: "agw-desktop",
    appBundleId: "com.agw.desktop",
    appVersion: releaseVersion,
    buildVersion: releaseVersion,
    asar: true,
    icon: path.resolve(__dirname, "assets", "agw-logo"),
    extraResource: [
      path.resolve(__dirname, "resources", "renderer"),
      path.resolve(__dirname, "resources", "package-flavor.json"),
      path.resolve(__dirname, "assets"),
      ...(flavor === "full" ? [path.resolve(__dirname, "resources", "server")] : []),
    ],
    ignore: [/^\/src($|\/)/, /^\/scripts($|\/)/, /^\/renderer($|\/)/, /\.test\.(ts|js)$/],
  },
  rebuildConfig: {},
  makers: [
    {
      name: "@electron-forge/maker-squirrel",
      platforms: ["win32"],
      config: {
        name: "agw_desktop",
        version: releaseVersion,
        setupIcon: path.resolve(__dirname, "assets", "agw-logo.ico"),
        setupExe: `Agw-${releaseVersion}-${flavor}-windows-${arch}-Setup.exe`,
      },
    },
    {
      name: "@electron-forge/maker-dmg",
      platforms: ["darwin"],
      config: {
        name: `Agw-${flavor}-macos-${arch}`,
        format: "ULFO",
      },
    },
    {
      name: "@electron-forge/maker-deb",
      platforms: ["linux"],
      config: {
        options: {
          bin: "agw-desktop",
          name: "agw-desktop",
          version: releaseVersion,
          productName: "Agw Desktop",
          genericName: "Agent Gateway",
          maintainer: "Agw",
          homepage: "https://github.com/zxyao145/agw",
          icon: path.resolve(__dirname, "assets", "agw-logo.png"),
          categories: ["Development", "Utility"],
        },
      },
    },
  ],
};
