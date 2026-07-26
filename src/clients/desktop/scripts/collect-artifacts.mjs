import { cp, mkdir, readdir, rm } from "node:fs/promises";
import { arch, platform } from "node:os";
import { extname, resolve } from "node:path";
import packageManifest from "../package.json" with { type: "json" };

const desktopDirectory = resolve(import.meta.dirname, "..");
const makeDirectory = resolve(desktopDirectory, "out", "make");
const releaseDirectory = resolve(desktopDirectory, "release-artifacts");
const flavor = process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full";
const targetArch = process.env.AGW_TARGET_ARCH || arch();
const targetPlatform = process.env.AGW_TARGET_PLATFORM || platform();
const releaseVersion = process.env.AGW_RELEASE_VERSION || packageManifest.version;
if (
  !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(preview|alpha|beta)\.(0|[1-9]\d*))?$/u.test(
    releaseVersion,
  )
) {
  throw new Error(
    `AGW_RELEASE_VERSION must use X.Y.Z or X.Y.Z-{preview|alpha|beta}.N format, received ${releaseVersion}.`,
  );
}
const platformName =
  targetPlatform === "darwin" ? "macos" : targetPlatform === "win32" ? "windows" : "linux";
const supportedExtensions = new Set([".dmg", ".exe", ".deb"]);

async function findInstallers(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const installers = [];
  for (const entry of entries) {
    const path = resolve(directory, entry.name);
    if (entry.isDirectory()) installers.push(...(await findInstallers(path)));
    else if (supportedExtensions.has(extname(entry.name).toLowerCase())) installers.push(path);
  }
  return installers;
}

const installers = await findInstallers(makeDirectory);
if (installers.length !== 1) {
  throw new Error(`Expected one installer in ${makeDirectory}, found ${installers.length}.`);
}

await rm(releaseDirectory, { recursive: true, force: true });
await mkdir(releaseDirectory, { recursive: true });
const extension = extname(installers[0]).toLowerCase();
const suffix = extension === ".exe" ? "-Setup" : "";
const output = resolve(
  releaseDirectory,
  `Agw-${releaseVersion}-${flavor}-${platformName}-${targetArch}${suffix}${extension}`,
);
await cp(installers[0], output);
console.log(output);
