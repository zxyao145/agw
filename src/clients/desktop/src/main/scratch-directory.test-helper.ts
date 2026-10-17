import { mkdir, mkdtemp, rm } from "node:fs/promises";
import { join, resolve } from "node:path";

/** 在 `desktop/.scratch/unit-tests/` 下创建临时目录。Creates a temporary directory under `desktop/.scratch/unit-tests/`. */
export async function createScratchDirectory(prefix: string): Promise<{
  path: string;
  dispose(): Promise<void>;
}> {
  const root = resolve(".scratch", "unit-tests");
  await mkdir(root, { recursive: true });
  const path = await mkdtemp(join(root, `${prefix}-`));
  return {
    path,
    dispose: () => rm(path, { recursive: true, force: true }),
  };
}
