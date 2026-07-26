import { spawn } from "node:child_process";
import { access, readFile, readdir, rm } from "node:fs/promises";
import { arch as hostArch, platform as hostPlatform } from "node:os";
import { resolve } from "node:path";

const desktopDirectory = resolve(import.meta.dirname, "..");
const packageManifest = JSON.parse(
  await readFile(resolve(desktopDirectory, "package.json"), "utf8"),
);
const nodeMajorVersion = Number.parseInt(process.versions.node.split(".")[0], 10);
if (nodeMajorVersion !== 24) {
  throw new Error(`Desktop releases require Node.js 24, received ${process.versions.node}.`);
}

function parseArguments(arguments_) {
  const options = {
    flavor: process.env.AGW_PACKAGE_FLAVOR || "full",
    arch: process.env.AGW_TARGET_ARCH || hostArch(),
    version: process.env.AGW_RELEASE_VERSION || packageManifest.version,
  };

  for (let index = 0; index < arguments_.length; index += 1) {
    const argument = arguments_[index];
    if (argument === "--") continue;
    const separator = argument.indexOf("=");
    const name = separator === -1 ? argument : argument.slice(0, separator);
    const value = separator === -1 ? arguments_[index + 1] : argument.slice(separator + 1);
    if (!["--flavor", "--arch", "--version"].includes(name) || !value) {
      throw new Error(
        "Usage: pnpm release:desktop -- --flavor full|client --arch x64|arm64 --version X.Y.Z[-{preview|alpha|beta}.N]",
      );
    }
    options[name.slice(2)] = value;
    if (separator === -1) index += 1;
  }

  return options;
}

function targetFor(platform, architecture) {
  if (!["darwin", "linux", "win32"].includes(platform)) {
    throw new Error(`Unsupported Desktop release platform: ${platform}.`);
  }
  if (!["x64", "arm64"].includes(architecture)) {
    throw new Error(`Unsupported Desktop release architecture: ${architecture}.`);
  }
  if (platform !== "darwin" && architecture !== "x64") {
    throw new Error("Windows and Linux Desktop releases currently support x64 only.");
  }

  const platformName = platform === "darwin" ? "macos" : platform === "win32" ? "windows" : "linux";
  const ridPlatform = platform === "darwin" ? "osx" : platform === "win32" ? "win" : "linux";
  return { platformName, rid: `${ridPlatform}-${architecture}` };
}

function run(command, args, environment) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, {
      cwd: desktopDirectory,
      env: environment,
      stdio: "inherit",
    });
    child.once("error", reject);
    child.once("exit", (code) => {
      if (code === 0) resolvePromise();
      else reject(new Error(`${command} exited with code ${code}.`));
    });
  });
}

async function exists(path) {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

const options = parseArguments(process.argv.slice(2));
if (!["full", "client"].includes(options.flavor)) {
  throw new Error(`Desktop release flavor must be full or client, received ${options.flavor}.`);
}
if (
  !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(preview|alpha|beta)\.(0|[1-9]\d*))?$/u.test(
    options.version,
  )
) {
  throw new Error(
    `Desktop release version must use X.Y.Z or X.Y.Z-{preview|alpha|beta}.N format, received ${options.version}.`,
  );
}

const target = targetFor(hostPlatform(), options.arch);
const isWindows = hostPlatform() === "win32";
const pnpmCommand = isWindows ? process.env.ComSpec || "cmd.exe" : "pnpm";
const pnpmArguments = isWindows ? ["/d", "/s", "/c", "pnpm.cmd"] : [];
const environment = {
  ...process.env,
  AGW_PACKAGE_FLAVOR: options.flavor,
  AGW_RELEASE_VERSION: options.version,
  AGW_TARGET_ARCH: options.arch,
  AGW_TARGET_PLATFORM: hostPlatform(),
  AGW_TARGET_RID: target.rid,
};

await rm(resolve(desktopDirectory, "out", "make"), { recursive: true, force: true });
await rm(resolve(desktopDirectory, "release-artifacts"), { recursive: true, force: true });
await run(pnpmCommand, [...pnpmArguments, "make", `--arch=${options.arch}`], environment);
await run(pnpmCommand, [...pnpmArguments, "release:collect"], environment);

const serverExecutable = resolve(
  desktopDirectory,
  "resources",
  "server",
  hostPlatform() === "win32" ? "agw-server.exe" : "agw-server",
);
const serverIsBundled = await exists(serverExecutable);
const packagedApplicationDirectory = resolve(
  desktopDirectory,
  "out",
  `Agw Desktop-${hostPlatform()}-${options.arch}`,
);
const packagedResourcesDirectory =
  hostPlatform() === "darwin"
    ? resolve(packagedApplicationDirectory, "Agw Desktop.app", "Contents", "Resources")
    : resolve(packagedApplicationDirectory, "resources");
const packagedServerExecutable = resolve(
  packagedResourcesDirectory,
  "server",
  hostPlatform() === "win32" ? "agw-server.exe" : "agw-server",
);
const packagedServerIsBundled = await exists(packagedServerExecutable);
if (options.flavor === "full" && (!serverIsBundled || !packagedServerIsBundled)) {
  throw new Error(
    `Full Desktop release is missing its Server resources (${serverExecutable}, ${packagedServerExecutable}).`,
  );
}
if (options.flavor === "client" && (serverIsBundled || packagedServerIsBundled)) {
  throw new Error(
    `Client Desktop release unexpectedly contains Server resources (${serverExecutable}, ${packagedServerExecutable}).`,
  );
}

const releaseDirectory = resolve(desktopDirectory, "release-artifacts");
const artifacts = await readdir(releaseDirectory);
if (artifacts.length !== 1) {
  throw new Error(`Expected one collected Desktop installer, found ${artifacts.length}.`);
}

const expectedPrefix = `Agw-${options.version}-${options.flavor}-${target.platformName}-${options.arch}`;
if (!artifacts[0].startsWith(expectedPrefix)) {
  throw new Error(`Unexpected Desktop installer name: ${artifacts[0]}.`);
}
console.log(resolve(releaseDirectory, artifacts[0]));
