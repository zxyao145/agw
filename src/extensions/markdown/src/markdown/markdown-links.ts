// Markdown 中链接与图片地址的解析与分类。Resolution and classification of link and image addresses in Markdown.

const CLOUD_PROTOCOL = "agw-cloud:";
const SCHEME_PATTERN = /^[a-z][a-z\d+.-]*:/i;
const EXTERNAL_LINK_SCHEMES = new Set(["http:", "https:"]);
// 宿主解码路径段后，编码的 `/`、`\` 与字面 `\` 会成为目录分隔符。
// After the host decodes path segments, encoded `/` and `\` and a literal `\` become directory separators.
const SEPARATOR_IN_SEGMENT = /%2f|%5c|\\/i;
const IMAGE_MIME_TYPES = new Map([
  ["png", "image/png"],
  ["jpg", "image/jpeg"],
  ["jpeg", "image/jpeg"],
  ["gif", "image/gif"],
  ["svg", "image/svg+xml"],
  ["webp", "image/webp"],
  ["avif", "image/avif"],
]);

export type LinkTarget =
  | { kind: "anchor"; id: string }
  | { kind: "file"; uri: string }
  | { kind: "external"; url: string }
  | { kind: "ignored" };

export type ImageSource =
  | { kind: "data"; src: string }
  | { kind: "file"; uri: string }
  | { kind: "external" };

function schemeOf(reference: string): string | null {
  return SCHEME_PATTERN.exec(reference)?.[0].toLowerCase() ?? null;
}

/**
 * 解析当前文件的云端 URI，返回它与根目录路径 `/<projectId>/<directoryKey>/`。
 * Parses the current file's cloud URI and returns it with the root path `/<projectId>/<directoryKey>/`.
 */
function parseFileUri(fileUri: string): { url: URL; rootPath: string } {
  const url = new URL(fileUri);
  const [, projectId, directoryKey] = url.pathname.split("/");
  if (url.protocol !== CLOUD_PROTOCOL || !url.host || !projectId || !directoryKey) {
    throw new Error(`Not a cloud file URI: ${fileUri}`);
  }
  return { url, rootPath: `/${projectId}/${directoryKey}/` };
}

/**
 * 按当前文件所在目录解析相对地址，去掉查询与片段。地址带 scheme、以 `//` 开头或超出根目录时返回 null。
 * Resolves a relative reference against the current file's directory, dropping the query and fragment. Returns
 * null when the reference has a scheme, starts with `//`, or leaves the root directory.
 */
export function resolveRelativeUri(fileUri: string, reference: string): string | null {
  const { url: base, rootPath } = parseFileUri(fileUri);
  if (schemeOf(reference) !== null || reference.startsWith("//")) return null;
  const resolved = new URL(reference, base);
  if (
    resolved.protocol !== base.protocol ||
    resolved.host !== base.host ||
    !resolved.pathname.startsWith(rootPath) ||
    SEPARATOR_IN_SEGMENT.test(resolved.pathname.slice(rootPath.length))
  ) {
    return null;
  }
  return `${resolved.protocol}//${resolved.host}${resolved.pathname}`;
}

/** 分类链接的点击行为。Classifies what clicking a link does. */
export function classifyLink(fileUri: string, href: string): LinkTarget {
  if (href === "") return { kind: "ignored" };
  if (href.startsWith("#")) return { kind: "anchor", id: decodeURIComponent(href.slice(1)) };
  const scheme = schemeOf(href);
  if (scheme !== null) {
    return EXTERNAL_LINK_SCHEMES.has(scheme)
      ? { kind: "external", url: href }
      : { kind: "ignored" };
  }
  const uri = resolveRelativeUri(fileUri, href);
  return uri === null ? { kind: "ignored" } : { kind: "file", uri };
}

/** 分类图片来源。Classifies an image source. */
export function classifyImage(fileUri: string, src: string): ImageSource {
  const scheme = schemeOf(src);
  if (scheme === "data:") return { kind: "data", src };
  if (scheme !== null || src === "") return { kind: "external" };
  const uri = resolveRelativeUri(fileUri, src);
  return uri === null ? { kind: "external" } : { kind: "file", uri };
}

/** 按扩展名返回图片的 MIME 类型；未知时为 null。Returns the image MIME type by extension, or null when unknown. */
export function imageMimeType(uri: string): string | null {
  const name = new URL(uri).pathname.split("/").at(-1) ?? "";
  const dot = name.lastIndexOf(".");
  if (dot < 0) return null;
  return IMAGE_MIME_TYPES.get(name.slice(dot + 1).toLowerCase()) ?? null;
}
