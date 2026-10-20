import { VFile } from "vfile";
import { matter } from "vfile-matter";

export class FrontMatterError extends Error {
  constructor(message: string, options: ErrorOptions) {
    super(`Front matter isn't valid YAML: ${message}`, options);
    this.name = "FrontMatterError";
  }
}

export type FrontMatterSplit = {
  /** 文件开头 front matter 所在的前缀（包括两行 `---`），没有时为空字符串。The prefix holding the front matter
   *  (both `---` lines included); empty when there is none. */
  prefix: string;
  /** 两行 `---` 之间的文字；没有 front matter 时为 null。The text between the two `---` lines; null without front
   *  matter. */
  text: string | null;
  body: string;
};

/**
 * vfile-matter 以 `{ strip: true }` 处理原文，原文与去掉 front matter 之后文字的长度差就是 front matter 所在的前缀。
 * YAML 无效时抛出 FrontMatterError。
 * vfile-matter processes the source with `{ strip: true }`; the length difference between the source and the
 * stripped text is the front matter's prefix. Invalid YAML throws FrontMatterError.
 */
export function splitFrontMatter(source: string): FrontMatterSplit {
  const file = new VFile(source);
  try {
    matter(file, { strip: true });
  } catch (error) {
    if (!(error instanceof Error) || error.name !== "YAMLParseError") throw error;
    throw new FrontMatterError(error.message, { cause: error });
  }
  const body = String(file);
  if (body.length === source.length) return { prefix: "", text: null, body };
  const prefix = source.slice(0, source.length - body.length);
  // 前缀为 `---\n`、可选的「文字与换行」、`---` 与可选的换行。The prefix is `---\n`, optional text plus a newline,
  // `---` and an optional newline.
  const inner = prefix.slice(4, prefix.endsWith("\n") ? -4 : -3);
  return { prefix, text: inner.endsWith("\n") ? inner.slice(0, -1) : inner, body };
}
