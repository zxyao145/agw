import { spawn } from "node:child_process";
import { cp, mkdir, rm, writeFile } from "node:fs/promises";
import { arch, platform } from "node:os";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const desktopDirectory = resolve(scriptDirectory, "..");
const repositoryRoot = resolve(desktopDirectory, "..", "..", "..");
const webDirectory = resolve(repositoryRoot, "src", "clients", "web");
const resourcesDirectory = resolve(desktopDirectory, "resources");
const flavor = process.env.AGW_PACKAGE_FLAVOR === "client" ? "client" : "full";
const rendererOnly = process.argv.includes("--renderer-only");

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
await run("pnpm", ["build"], {
  cwd: webDirectory,
  env: { ...process.env, NEXT_OUTPUT_MODE: "export" },
});
await rm(resolve(resourcesDirectory, "renderer"), { recursive: true, force: true });
await cp(resolve(webDirectory, "out"), resolve(resourcesDirectory, "renderer"), {
  recursive: true,
});
await writeFile(
  resolve(resourcesDirectory, "package-flavor.json"),
  `${JSON.stringify({ packageFlavor: flavor }, null, 2)}\n`,
  "utf8",
);

const serverOutput = resolve(resourcesDirectory, "server");
if (!rendererOnly) {
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
        "-o",
        serverOutput,
      ],
      { cwd: repositoryRoot },
    );
    await cp(resolve(webDirectory, "out"), resolve(serverOutput, "wwwroot"), {
      recursive: true,
    });
  }
}
