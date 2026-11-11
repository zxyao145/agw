import { createHash } from "node:crypto";
import { spawn } from "node:child_process";
import { readdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";
import { build as buildDesktop } from "vite";

const packageRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(packageRoot, "../../..");
const outputRoot = resolve(packageRoot, "dist");
const serverOutput = resolve(outputRoot, "server");

await rm(outputRoot, { recursive: true, force: true });
await buildDesktop({ configFile: resolve(packageRoot, "vite.config.ts") });
await run("dotnet", [
  "publish",
  resolve(packageRoot, "server/Agw.Extensions.TaskPilot.csproj"),
  "--configuration",
  "Release",
  "--disable-build-servers",
  "--output",
  serverOutput,
]);

const manifest = JSON.parse(await readFile(resolve(packageRoot, "manifest.json"), "utf8"));
manifest.files = await createFileMap(outputRoot);
await writeFile(resolve(outputRoot, "manifest.json"), `${JSON.stringify(manifest, null, 2)}\n`);

async function createFileMap(directory) {
  const result = {};
  for (const path of await listFiles(directory)) {
    const bytes = await readFile(path);
    const relativePath = relative(outputRoot, path).split(sep).join("/");
    result[relativePath] = createHash("sha256").update(bytes).digest("hex");
  }
  return result;
}

async function listFiles(directory) {
  const files = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = join(directory, entry.name);
    if (entry.isDirectory()) files.push(...(await listFiles(entryPath)));
    else if (entry.isFile()) files.push(entryPath);
  }
  return files;
}

function run(command, args) {
  return new Promise((resolvePromise, rejectPromise) => {
    const child = spawn(command, args, { cwd: repositoryRoot, stdio: "inherit" });
    child.once("error", rejectPromise);
    child.once("exit", (code) => {
      if (code === 0) resolvePromise();
      else rejectPromise(new Error(`${command} exited with code ${code ?? "unknown"}.`));
    });
  });
}
