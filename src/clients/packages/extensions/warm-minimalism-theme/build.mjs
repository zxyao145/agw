// 把 manifest.json 与 themes/ 复制到 dist/。Copies manifest.json and themes/ into dist/.
import { cp, mkdir, rm } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const packageDirectory = dirname(fileURLToPath(import.meta.url));
const distDirectory = join(packageDirectory, "dist");

await rm(distDirectory, { recursive: true, force: true });
await mkdir(distDirectory, { recursive: true });
await cp(join(packageDirectory, "manifest.json"), join(distDirectory, "manifest.json"));
await cp(join(packageDirectory, "themes"), join(distDirectory, "themes"), { recursive: true });
