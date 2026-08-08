import { spawn, spawnSync } from "node:child_process";
import { copyFile, mkdir, rm } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { setTimeout as delay } from "node:timers/promises";

import electronPath from "electron";

const appName = "Agw Desktop";
const oauthProtocol = "agw-desktop";
const launchServicesRegister =
  "/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister";
const pnpmCommand = process.platform === "win32" ? "pnpm.cmd" : "pnpm";
const rendererUrl = process.env.AGW_RENDERER_URL ?? "http://localhost:3000";
const children = new Set();
let stopping = false;

function run(args, options = {}) {
  return spawn(pnpmCommand, args, { stdio: "inherit", ...options });
}

function runRequired(command, args) {
  const result = spawnSync(command, args, { stdio: "inherit" });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${command} exited with code ${result.status}`);
}

async function prepareElectronLaunch() {
  if (process.platform !== "darwin") {
    return { command: pnpmCommand, args: ["exec", "electron", "."] };
  }

  const electronApp = dirname(dirname(dirname(electronPath)));
  const applicationsRoot = join(homedir(), "Applications");
  const brandedApp = join(applicationsRoot, `${appName} Development.app`);
  await mkdir(applicationsRoot, { recursive: true });
  await rm(brandedApp, { recursive: true, force: true });

  try {
    runRequired("/bin/cp", ["-cR", electronApp, brandedApp]);

    const resources = join(brandedApp, "Contents", "Resources");
    await copyFile(resolve("assets", "agw-logo.icns"), join(resources, "agw-logo.icns"));

    const infoPlist = join(brandedApp, "Contents", "Info.plist");
    for (const [key, value] of [
      ["CFBundleDisplayName", appName],
      ["CFBundleName", appName],
      ["CFBundleIdentifier", "com.agw.desktop.dev"],
      ["CFBundleIconFile", "agw-logo.icns"],
    ]) {
      runRequired("/usr/bin/plutil", ["-replace", key, "-string", value, infoPlist]);
    }
    runRequired("/usr/bin/plutil", [
      "-insert",
      "CFBundleURLTypes",
      "-json",
      JSON.stringify([
        {
          CFBundleURLName: `${appName} OAuth`,
          CFBundleURLSchemes: [oauthProtocol],
        },
      ]),
      infoPlist,
    ]);
    runRequired("/usr/bin/codesign", ["--force", "--deep", "--sign", "-", brandedApp]);
    runRequired(launchServicesRegister, ["-f", brandedApp]);

    return {
      command: join(brandedApp, "Contents", "MacOS", "Electron"),
      args: ["."],
      applicationPath: brandedApp,
    };
  } catch (error) {
    await rm(brandedApp, { recursive: true, force: true });
    throw error;
  }
}

function waitForExit(child) {
  return new Promise((resolvePromise, reject) => {
    child.once("error", reject);
    child.once("exit", (code, signal) => {
      if (code === 0 || stopping) resolvePromise();
      else reject(new Error(`${pnpmCommand} exited with ${signal ?? `code ${code}`}`));
    });
  });
}

async function waitForRenderer() {
  const deadline = Date.now() + 60_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(rendererUrl);
      if (response.ok) return;
    } catch {
      // The development server is still starting.
    }
    await delay(250);
  }
  throw new Error(`Desktop renderer did not become ready at ${rendererUrl}.`);
}

function stopChildren() {
  stopping = true;
  for (const child of children) child.kill();
}

process.once("SIGINT", stopChildren);
process.once("SIGTERM", stopChildren);

await waitForExit(run(["build:main"]));

const renderer = run(["dev:renderer"]);
children.add(renderer);
let electronLaunch;

try {
  await waitForRenderer();
  electronLaunch = await prepareElectronLaunch();
  const electron = spawn(electronLaunch.command, electronLaunch.args, {
    stdio: "inherit",
    env: { ...process.env, AGW_RENDERER_URL: rendererUrl },
  });
  children.add(electron);
  await waitForExit(electron);
} finally {
  stopChildren();
  if (electronLaunch?.applicationPath) {
    spawnSync(launchServicesRegister, ["-u", electronLaunch.applicationPath], {
      stdio: "ignore",
    });
    await rm(electronLaunch.applicationPath, { recursive: true, force: true });
  }
}
