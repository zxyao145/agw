// 语言识别（设计文档第 1 节）。这个模块不导入 Monaco，Node 测试可以直接调用。
// Language detection (design section 1). This module does not import Monaco, so Node tests call it directly.

/** 没有匹配时使用的语言。The language used when nothing matches. */
export const PLAIN_TEXT = "plaintext";

/** `monaco.languages.getLanguages()` 中用到的字段。The fields used from `monaco.languages.getLanguages()`. */
export interface LanguageExtensionPoint {
  id: string;
  extensions?: string[];
  filenames?: string[];
}

/**
 * 按文件名识别语言：先找 `filenames` 中与文件名相同的一项（不区分大小写），再找与文件名结尾匹配的最长扩展名，都不匹配
 * 时为 `plaintext`。运行时传入 `monaco.languages.getLanguages()`。
 * Detects the language of a file name: an equal entry in `filenames` (ignoring case) first, then the longest
 * extension the name ends with, otherwise `plaintext`. At runtime the list is `monaco.languages.getLanguages()`.
 */
export function detectLanguage(
  fileName: string,
  languages: readonly LanguageExtensionPoint[],
): string {
  const name = fileName.toLowerCase();
  const byName = languages.find((language) =>
    language.filenames?.some((candidate) => candidate.toLowerCase() === name),
  );
  if (byName) return byName.id;

  let best: { id: string; length: number } | null = null;
  for (const language of languages) {
    for (const extension of language.extensions ?? []) {
      if (name.endsWith(extension.toLowerCase()) && extension.length > (best?.length ?? 0)) {
        best = { id: language.id, length: extension.length };
      }
    }
  }
  return best?.id ?? PLAIN_TEXT;
}
