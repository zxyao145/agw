// Markdown 中项目内图片的读取，Preview 与 Edit 共用。Reading of project images referenced by Markdown, shared by Preview and Edit.
import type { FileSystemApi } from "@agw/extension-sdk";

import { imageMimeType } from "./markdown-links";

/**
 * 用 `fs.readFile` 读取图片文件并生成 Blob URL；`signal` 已中止时拒绝，不生成 URL。图片不再显示时由调用方调用
 * `URL.revokeObjectURL`。
 * Reads an image file with `fs.readFile` and creates a Blob URL; rejects without creating one once `signal` is
 * aborted. The caller calls `URL.revokeObjectURL` when the image is no longer shown.
 */
export async function createImageObjectUrl(
  fs: FileSystemApi,
  uri: string,
  signal: AbortSignal,
): Promise<string> {
  const data = await fs.readFile(uri, { signal });
  signal.throwIfAborted();
  // 结构化克隆得到的数据由 ArrayBuffer 承载。Structured-cloned data is backed by an ArrayBuffer.
  const bytes = data as Uint8Array<ArrayBuffer>;
  return URL.createObjectURL(new Blob([bytes], { type: imageMimeType(uri) ?? "" }));
}
