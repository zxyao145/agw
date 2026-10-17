import { mkdir, rename, rm, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

/** 先写临时文件再改名，读取方不会看到写了一半的文件。Writes a temporary file, then renames it, so readers never see a partial file. */
export async function writeJsonFile(
  file: string,
  value: unknown,
  beforeCommit?: () => Promise<void>,
): Promise<void> {
  await mkdir(dirname(file), { recursive: true });
  const temporaryFile = `${file}.${process.pid}.tmp`;
  await writeFile(temporaryFile, `${JSON.stringify(value, null, 2)}\n`, {
    encoding: "utf8",
    mode: 0o600,
  });
  try {
    await beforeCommit?.();
    await rename(temporaryFile, file);
  } finally {
    await rm(temporaryFile, { force: true });
  }
}
