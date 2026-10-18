/**
 * 文件查看器的文件名模式：对比文件基本名，不区分大小写；`*` 匹配任意个字符，`?` 匹配一个字符，其余按字面匹配。
 * File viewer name patterns: compared with the base name, case-insensitively; `*` matches any run of characters,
 * `?` one character, everything else literally.
 */
export function matchesFileNamePattern(pattern: string, fileName: string): boolean {
  const source = Array.from(pattern, (character) =>
    character === "*"
      ? ".*"
      : character === "?"
        ? "."
        : character.replace(/[.*+?^${}()|[\]\\/-]/gu, "\\$&"),
  ).join("");
  return new RegExp(`^${source}$`, "iu").test(fileName);
}

/** 路径中的基本名。The base name of a path. */
export function fileBaseName(path: string): string {
  return path.split(/[\\/]/u).filter(Boolean).pop() ?? "";
}
