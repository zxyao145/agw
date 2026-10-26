import { createHash } from "node:crypto";
import { createReadStream } from "node:fs";
import { readdir, readlink } from "node:fs/promises";
import { join, relative, sep } from "node:path";

/**
 * extension 包的摘要：按相对路径排序，路径、条目类型与内容一起计算 SHA-256；符号链接计算链接内容。批准绑定这个摘要，
 * 包内任何文件变化都使批准失效。
 * The digest of an extension package: entries sorted by relative path, with each path, entry type and content in one
 * SHA-256; symbolic links contribute their link text. Approvals bind this digest, so any file change in the package
 * invalidates them.
 */
export async function packageDigest(directory: string): Promise<string> {
  const entries = (await readdir(directory, { recursive: true, withFileTypes: true }))
    .map((entry) => {
      const path = join(entry.parentPath, entry.name);
      return { entry, path, name: relative(directory, path).split(sep).join("/") };
    })
    .sort((left, right) => (left.name < right.name ? -1 : left.name > right.name ? 1 : 0));
  const hash = createHash("sha256");
  for (const { entry, path, name } of entries) {
    if (entry.isSymbolicLink()) {
      hash.update(`link\u0000${name}\u0000${await readlink(path)}\u0000`);
    } else if (entry.isDirectory()) {
      hash.update(`directory\u0000${name}\u0000`);
    } else if (entry.isFile()) {
      hash.update(`file\u0000${name}\u0000`);
      for await (const chunk of createReadStream(path)) hash.update(chunk as Buffer);
      hash.update("\u0000");
    } else {
      hash.update(`other\u0000${name}\u0000`);
    }
  }
  return hash.digest("hex");
}
