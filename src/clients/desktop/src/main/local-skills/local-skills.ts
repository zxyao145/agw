import { createHash } from "node:crypto";
import { constants } from "node:fs";
import { lstat, open, readdir, realpath, stat, type FileHandle } from "node:fs/promises";
import { isAbsolute, join, relative, sep } from "node:path";
import { VFile } from "vfile";
import { matter } from "vfile-matter";
import yazl from "yazl";

import type { LocalSkill, LocalSkillPackage, LocalSkillScan } from "../../shared/contracts";

const IGNORED_ENTRY_NAMES = new Set([".git", ".DS_Store", "Thumbs.db", "desktop.ini"]);
const compareStrings = (left: string, right: string) => (left < right ? -1 : left > right ? 1 : 0);

type SkillFile = {
  relativePath: string;
  path: string;
  device: bigint;
  inode: bigint;
};

type SkillProblem = {
  problem: string;
};

type SkillMetadata = {
  name: string;
  description: string;
};

type HashedFile = {
  relativePath: string;
  sha256: string;
};

function isPathInside(root: string, target: string): boolean {
  const path = relative(root, target);
  return path === "" || (!path.startsWith(`..${sep}`) && path !== ".." && !isAbsolute(path));
}

function sourceDigest(files: readonly HashedFile[]): string {
  const entries = files
    .map((file) => [file.relativePath, file.sha256] as const)
    .sort(([left], [right]) => compareStrings(left, right));
  const digest = createHash("sha256")
    .update(Buffer.from(JSON.stringify(entries), "utf8"))
    .digest("hex");
  return `sha256:${digest}`;
}

function hashBytes(bytes: Buffer): string {
  return createHash("sha256").update(bytes).digest("hex");
}

async function readSkillFile<T>(
  root: string,
  file: SkillFile,
  read: (handle: FileHandle) => Promise<T>,
): Promise<T> {
  const target = await realpath(file.path);
  if (!isPathInside(root, target)) {
    throw new Error(`Symbolic link "${file.relativePath}" points outside the skill directory.`);
  }

  const handle = await open(
    target,
    constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
  );
  try {
    const entry = await handle.stat({ bigint: true });
    if (!entry.isFile() || entry.dev !== file.device || entry.ino !== file.inode) {
      throw new Error(`File "${file.relativePath}" changed while reading the skill directory.`);
    }
    return await read(handle);
  } finally {
    await handle.close();
  }
}

function hashFile(root: string, file: SkillFile): Promise<{ sha256: string; size: number }> {
  return readSkillFile(root, file, async (handle) => {
    const hash = createHash("sha256");
    let size = 0;
    for await (const chunk of handle.createReadStream({ autoClose: false })) {
      hash.update(chunk);
      size += chunk.length;
    }
    return { sha256: hash.digest("hex"), size };
  });
}

async function walkSkillFiles(root: string): Promise<{ files: SkillFile[] } | SkillProblem> {
  const files: SkillFile[] = [];

  async function walk(directory: string): Promise<SkillProblem | null> {
    const entries = (await readdir(directory)).sort(compareStrings);
    for (const name of entries) {
      if (IGNORED_ENTRY_NAMES.has(name)) continue;
      const path = join(directory, name);
      const relativePath = relative(root, path).split(sep).join("/");
      const entry = await lstat(path, { bigint: true });

      if (entry.isSymbolicLink()) {
        let target: string;
        try {
          target = await realpath(path);
        } catch (error) {
          if ((error as NodeJS.ErrnoException).code === "ENOENT")
            return { problem: "Broken symbolic link." };
          throw error;
        }
        if (!isPathInside(root, target)) {
          return { problem: `Symbolic link "${relativePath}" points outside the skill directory.` };
        }
        const targetStat = await stat(target, { bigint: true });
        if (targetStat.isDirectory()) {
          return { problem: `Symbolic link "${relativePath}" points to a directory.` };
        }
        if (!targetStat.isFile()) {
          return { problem: `"${relativePath}" isn't a regular file or directory.` };
        }
        files.push({ relativePath, path: target, device: targetStat.dev, inode: targetStat.ino });
        continue;
      }

      if (entry.isDirectory()) {
        const problem = await walk(path);
        if (problem) return problem;
        continue;
      }
      if (entry.isFile()) {
        files.push({ relativePath, path, device: entry.dev, inode: entry.ino });
        continue;
      }
      return { problem: `"${relativePath}" isn't a regular file or directory.` };
    }
    return null;
  }

  const problem = await walk(root);
  return problem ?? { files };
}

function parseSkillMetadata(source: string): SkillMetadata | SkillProblem {
  const normalizedSource = source.startsWith("\uFEFF") ? source.slice(1) : source;
  const file = new VFile(normalizedSource);
  try {
    matter(file, { strip: true });
  } catch (error) {
    if (!(error instanceof Error) || error.name !== "YAMLParseError") throw error;
    return { problem: `Front matter isn't valid YAML: ${error.message}` };
  }
  if (String(file).length === normalizedSource.length)
    return { problem: "SKILL.md has no front matter." };

  const metadata = file.data.matter;
  if (metadata === null || typeof metadata !== "object" || Array.isArray(metadata)) {
    return { problem: "Front matter needs a name." };
  }
  const name = (metadata as Record<string, unknown>).name;
  if (typeof name !== "string" || name.trim().length === 0)
    return { problem: "Front matter needs a name." };
  const description = (metadata as Record<string, unknown>).description;
  if (typeof description !== "string" || description.trim().length === 0) {
    return { problem: "Front matter needs a description." };
  }
  return { name: name.trim(), description: description.trim() };
}

function emptySkill(directoryName: string, problem: string | null = null): LocalSkill {
  return {
    directoryName,
    name: null,
    description: null,
    sourceDigest: null,
    fileCount: 0,
    totalBytes: 0,
    problem,
  };
}

async function inspectSkill(directoryName: string, directoryPath: string): Promise<LocalSkill> {
  const root = await realpath(directoryPath);
  const walked = await walkSkillFiles(root);
  if ("problem" in walked) return emptySkill(directoryName, walked.problem);

  const skillMarkdown = walked.files.find((file) => file.relativePath === "SKILL.md");
  if (!skillMarkdown) return emptySkill(directoryName, "SKILL.md is missing.");
  const markdown = await readSkillFile(root, skillMarkdown, (handle) => handle.readFile("utf8"));
  const metadata = parseSkillMetadata(markdown);
  if ("problem" in metadata) return emptySkill(directoryName, metadata.problem);

  const hashes: HashedFile[] = [];
  let totalBytes = 0;
  for (const file of walked.files) {
    const result = await hashFile(root, file);
    hashes.push({ relativePath: file.relativePath, sha256: result.sha256 });
    totalBytes += result.size;
  }

  return {
    directoryName,
    name: metadata.name,
    description: metadata.description,
    sourceDigest: sourceDigest(hashes),
    fileCount: walked.files.length,
    totalBytes,
    problem: null,
  };
}

function brokenSkill(directoryName: string): LocalSkill {
  return emptySkill(directoryName, "Broken symbolic link.");
}

export async function scanLocalSkills(homeDirectory: string): Promise<LocalSkillScan> {
  const directory = join(homeDirectory, ".agents", "skills");
  let directoryStat;
  try {
    directoryStat = await stat(directory);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === "ENOENT")
      return { directory, exists: false, skills: [] };
    throw error;
  }
  if (!directoryStat.isDirectory()) throw new Error(`${directory} isn't a directory.`);

  const skills: LocalSkill[] = [];
  for (const directoryName of (await readdir(directory)).sort(compareStrings)) {
    if (directoryName.startsWith(".")) continue;
    const path = join(directory, directoryName);
    const entry = await lstat(path);
    if (!entry.isDirectory() && !entry.isSymbolicLink()) continue;

    if (entry.isSymbolicLink()) {
      let targetStat;
      try {
        targetStat = await stat(path);
      } catch (error) {
        if ((error as NodeJS.ErrnoException).code === "ENOENT") {
          skills.push(brokenSkill(directoryName));
          continue;
        }
        throw error;
      }
      if (!targetStat.isDirectory()) continue;
    }
    skills.push(await inspectSkill(directoryName, path));
  }

  const names = new Map<string, LocalSkill[]>();
  for (const skill of skills) {
    if (skill.name === null) continue;
    const duplicates = names.get(skill.name) ?? [];
    duplicates.push(skill);
    names.set(skill.name, duplicates);
  }
  for (const [name, duplicates] of names) {
    if (duplicates.length < 2) continue;
    for (const skill of duplicates) {
      skill.problem = `Another directory uses the name "${name}".`;
      skill.sourceDigest = null;
    }
  }

  return { directory, exists: true, skills };
}

function validateDirectoryName(directoryName: string): void {
  if (
    typeof directoryName !== "string" ||
    directoryName.length === 0 ||
    directoryName.startsWith(".") ||
    directoryName.includes("/") ||
    directoryName.includes("\\") ||
    directoryName.includes("\0")
  ) {
    throw new Error("Invalid local skill directory name.");
  }
}

async function zipBuffer(zipFile: yazl.ZipFile): Promise<Buffer> {
  const chunks: Buffer[] = [];
  const output = zipFile.outputStream;
  const completed = new Promise<Buffer>((resolve, reject) => {
    output.on("data", (chunk: Buffer) => chunks.push(chunk));
    output.once("error", reject);
    output.once("end", () => resolve(Buffer.concat(chunks)));
  });
  zipFile.end();
  return await completed;
}

export async function packLocalSkill(
  homeDirectory: string,
  directoryName: string,
): Promise<LocalSkillPackage> {
  validateDirectoryName(directoryName);
  const directoryPath = join(homeDirectory, ".agents", "skills", directoryName);
  const root = await realpath(directoryPath);
  const walked = await walkSkillFiles(root);
  if ("problem" in walked) throw new Error(walked.problem);

  const skillMarkdown = walked.files.find((file) => file.relativePath === "SKILL.md");
  if (!skillMarkdown) throw new Error("SKILL.md is missing.");
  const markdownBytes = await readSkillFile(root, skillMarkdown, (handle) => handle.readFile());
  const metadata = parseSkillMetadata(markdownBytes.toString("utf8"));
  if ("problem" in metadata) throw new Error(metadata.problem);

  const files: Array<{ relativePath: string; bytes: Buffer }> = [];
  for (const file of walked.files) {
    files.push({
      relativePath: file.relativePath,
      bytes:
        file === skillMarkdown
          ? markdownBytes
          : await readSkillFile(root, file, (handle) => handle.readFile()),
    });
  }

  const zipFile = new yazl.ZipFile();
  for (const file of files) zipFile.addBuffer(file.bytes, file.relativePath);
  const bytes = await zipBuffer(zipFile);
  const sourceDigestValue = sourceDigest(
    files.map((file) => ({ relativePath: file.relativePath, sha256: hashBytes(file.bytes) })),
  );

  return {
    directoryName,
    name: metadata.name,
    description: metadata.description,
    sourceDigest: sourceDigestValue,
    fileName: `${metadata.name}.zip`,
    bytes: new Uint8Array(bytes),
  };
}
