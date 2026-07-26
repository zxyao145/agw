import { spawn } from "node:child_process";
import { access, cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { arch, platform } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const desktopDirectory = resolve(scriptDirectory, "..");
const repositoryRoot = resolve(desktopDirectory, "..", "..", "..");
const resourcesDirectory = resolve(desktopDirectory, "resources");
const rendererDirectory = resolve(resourcesDirectory, "renderer");
const rendererOutput = resolve(desktopDirectory, "renderer", "out");
const flavor = process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full";
const packageManifest = JSON.parse(
  await readFile(resolve(desktopDirectory, "package.json"), "utf8"),
);
const releaseVersion = process.env.AGW_RELEASE_VERSION || packageManifest.version;
if (!/^\d+\.\d+\.\d+$/u.test(releaseVersion)) {
  throw new Error(`AGW_RELEASE_VERSION must use X.Y.Z format, received ${releaseVersion}.`);
}

function run(command, args, options = {}) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, { stdio: "inherit", ...options });
    child.once("error", reject);
    child.once("exit", (code) => {
      if (code === 0) resolvePromise();
      else reject(new Error(`${command} exited with code ${code}`));
    });
  });
}

function targetRid() {
  if (process.env.AGW_TARGET_RID) return process.env.AGW_TARGET_RID;
  const targetArch = process.env.AGW_TARGET_ARCH || arch();
  const ridArch = targetArch === "arm64" ? "arm64" : "x64";
  if (platform() === "win32") return `win-${ridArch}`;
  if (platform() === "darwin") return `osx-${ridArch}`;
  return `linux-${ridArch}`;
}

await mkdir(resourcesDirectory, { recursive: true });
await access(resolve(rendererOutput, "index.html")).catch(() => {
  throw new Error(
    "Desktop renderer is missing. Run `pnpm build:renderer` from src/clients/desktop.",
  );
});
await rm(rendererDirectory, { recursive: true, force: true });
await cp(rendererOutput, rendererDirectory, { recursive: true });
await writeFile(
  resolve(resourcesDirectory, "package-flavor.json"),
  `${JSON.stringify({ packageFlavor: flavor }, null, 2)}\n`,
  "utf8",
);

const serverOutput = resolve(resourcesDirectory, "server");
await rm(serverOutput, { recursive: true, force: true });
if (flavor === "full") {
  await run(
    "dotnet",
    [
      "publish",
      resolve(repositoryRoot, "src", "server", "Agw.Host", "Agw.Host.csproj"),
      "-c",
      "Release",
      "-r",
      targetRid(),
      "--self-contained",
      "true",
      `-p:Version=${releaseVersion}`,
      "-o",
      serverOutput,
    ],
    { cwd: repositoryRoot },
  );
  await cp(rendererDirectory, resolve(serverOutput, "wwwroot"), { recursive: true });
}
