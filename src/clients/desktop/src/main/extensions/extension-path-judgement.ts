import { lstat, readlink, realpath } from "node:fs/promises";
import { basename, dirname, join, resolve } from "node:path";

const MAX_SYMBOLIC_LINK_HOPS = 40;

function isMissing(error: unknown): boolean {
  const code = (error as NodeJS.ErrnoException).code;
  return code === "ENOENT" || code === "ENOTDIR";
}

/**
 * 判断用的路径：目标存在时取 realpath（跟随符号链接）；不存在时向上找到最近的已存在祖先，
 * 取它的 realpath 再拼接剩余路径段；悬空的符号链接按链接内容继续解析。
 * Path used for checks: the realpath when the target exists (following links); otherwise the nearest
 * existing ancestor's realpath plus the remaining segments, resolving dangling links by their contents.
 */
export async function judgementPath(path: string): Promise<string> {
  let current = resolve(path);
  const rest: string[] = [];
  for (let hops = 0; ;) {
    try {
      return join(await realpath(current), ...rest.reverse());
    } catch (error) {
      if (!isMissing(error)) throw error;
    }
    let isLink = false;
    try {
      isLink = (await lstat(current)).isSymbolicLink();
    } catch (error) {
      if (!isMissing(error)) throw error;
    }
    if (isLink) {
      hops += 1;
      if (hops > MAX_SYMBOLIC_LINK_HOPS) {
        throw Object.assign(new Error(`ELOOP: too many symbolic links, ${path}`), {
          code: "ELOOP",
          path,
        });
      }
      current = resolve(dirname(current), await readlink(current));
      continue;
    }
    const parent = dirname(current);
    if (parent === current) return join(current, ...rest.reverse());
    rest.push(basename(current));
    current = parent;
  }
}

/** 作用于目录项本身（删除目标、改名的源）：父目录的判断路径加最后一段。Acting on the entry itself: the parent's judgement path plus the last segment. */
export async function entryJudgementPath(path: string): Promise<string> {
  const absolute = resolve(path);
  return join(await judgementPath(dirname(absolute)), basename(absolute));
}
