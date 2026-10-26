// 图片文件的类型、大小与解码。The type, size and decoding of image files.

/** manifest 中每个 pattern 的扩展名对应的 MIME 类型。The MIME type of every manifest pattern's extension. */
const IMAGE_MIME_TYPES = new Map([
  ["png", "image/png"],
  ["apng", "image/apng"],
  ["jpg", "image/jpeg"],
  ["jpeg", "image/jpeg"],
  ["jfif", "image/jpeg"],
  ["gif", "image/gif"],
  ["webp", "image/webp"],
  ["avif", "image/avif"],
  ["bmp", "image/bmp"],
  ["ico", "image/x-icon"],
  ["svg", "image/svg+xml"],
]);

/**
 * 按扩展名（不区分大小写）返回 MIME 类型；manifest 只把这些扩展名交给查看器，其他扩展名抛出错误。
 * Returns the MIME type by extension, ignoring case; the manifest hands only these extensions to the viewer, so others
 * throw.
 */
export function imageMimeType(fileName: string): string {
  const dot = fileName.lastIndexOf(".");
  const type = dot < 0 ? undefined : IMAGE_MIME_TYPES.get(fileName.slice(dot + 1).toLowerCase());
  if (type === undefined) throw new Error(`${fileName} is not a supported image file.`);
  return type;
}

/** 与 Files 文件树相同的大小格式。The size format of the Files tree. */
export function formatFileSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function sameBytes(left: Uint8Array, right: Uint8Array): boolean {
  if (left.length !== right.length) return false;
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return false;
  }
  return true;
}

/** 解码完成的图片；不再显示时由调用方释放 `url`。A decoded image; the caller revokes `url` once it is not shown. */
export type DecodedImage = {
  url: string;
  width: number;
  height: number;
  bytes: Uint8Array;
};

/**
 * 用字节创建 Blob URL 并解码；失败时释放 URL 并抛出解码错误。
 * Creates a Blob URL from the bytes and decodes it; on failure it revokes the URL and rethrows the decoding error.
 */
export async function decodeImage(bytes: Uint8Array, type: string): Promise<DecodedImage> {
  // 结构化克隆得到的数据由 ArrayBuffer 承载。Structured-cloned data is backed by an ArrayBuffer.
  const url = URL.createObjectURL(new Blob([bytes as Uint8Array<ArrayBuffer>], { type }));
  const element = new Image();
  element.src = url;
  try {
    await element.decode();
  } catch (error) {
    URL.revokeObjectURL(url);
    throw error;
  }
  return { url, width: element.naturalWidth, height: element.naturalHeight, bytes };
}
