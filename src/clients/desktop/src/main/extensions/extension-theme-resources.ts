import { open, realpath } from "node:fs/promises";
import { join, posix, relative, sep } from "node:path";
import {
  type Document as XmlDocument,
  DOMParser,
  type Element as XmlElement,
  type Node as XmlNode,
  XMLSerializer,
} from "@xmldom/xmldom";
// css-tree 的 `lib` 入口打包为 CommonJS 后无法读取语法数据，使用 `dist` 构建（见 extension-theme-file.ts）。
// css-tree's `lib` entry cannot read its syntax data once bundled as CommonJS, so the `dist` build is used (see
// extension-theme-file.ts).
import {
  type CssLocation,
  type CssNode,
  ident,
  parse,
  type ParseOptions,
  string as cssString,
  tokenize,
  tokenTypes,
  url as cssUrl,
} from "css-tree/dist/csstree.esm";
import parseDataUrl from "data-urls";
import { lookup } from "mime-types";
import postcss, {
  type AtRule,
  type ChildNode,
  CssSyntaxError,
  type Declaration,
  type Root,
} from "postcss";

import { isValidPackagePath } from "../../shared/extension-manifest";
import { isPathInside } from "./extension-catalog";

/** 单个 CSS 文件的上限。The limit for one CSS file. */
export const MAX_THEME_CSS_FILE_BYTES = 1024 * 1024;
/** 单个图片或字体资源解码后的上限。The limit for one decoded image or font resource. */
export const MAX_THEME_ASSET_BYTES = 4 * 1024 * 1024;
/** 不同输入文件与已有 data URL 解码数据的合计上限。The limit for distinct input files plus decoded data URLs. */
export const MAX_THEME_INPUT_BYTES = 8 * 1024 * 1024;
/** 每个主题读取的不同文件数量上限。The limit for distinct files read per theme. */
export const MAX_THEME_FILES = 256;
/** 导入深度上限。The import depth limit. */
export const MAX_THEME_IMPORT_DEPTH = 32;
/** 导入展开累计次数上限，重复导入同样计入。The limit for import expansions, repeated imports included. */
export const MAX_THEME_IMPORT_EXPANSIONS = 256;
/** 完整 `cssText` 的 UTF-8 上限。The UTF-8 limit for the complete `cssText`. */
export const MAX_THEME_CSS_TEXT_BYTES = 16 * 1024 * 1024;

export type ThemeCompileErrorCode =
  | "THEME_PARSE_FAILED"
  | "THEME_FILE_NOT_FOUND"
  | "PACKAGE_PATH_OUTSIDE_ROOT"
  | "THEME_IMPORT_INVALID"
  | "THEME_RESOURCE_INVALID"
  | "THEME_SIZE_LIMIT_EXCEEDED";

/** 包内文件与可获得的行号、列号。A package file with the line and column when known. */
export type ThemeSourceLocation = { file: string; line?: number; column?: number };

function formatLocation(location: ThemeSourceLocation): string {
  return location.line === undefined
    ? location.file
    : `${location.file}:${location.line}:${location.column ?? 1}`;
}

/** 主题 CSS 的校验错误，消息带有包内文件位置。A theme CSS validation error whose message carries the package file location. */
export class ThemeCompileError extends Error {
  public constructor(
    public readonly code: ThemeCompileErrorCode,
    message: string,
    location: ThemeSourceLocation | null = null,
  ) {
    super(location ? `${formatLocation(location)}: ${message}` : message);
    this.name = "ThemeCompileError";
  }
}

function describeLimit(bytes: number): string {
  return `${bytes} bytes (${bytes / 1024 / 1024} MiB)`;
}

export function sizeLimitError(
  message: string,
  location: ThemeSourceLocation | null,
): ThemeCompileError {
  return new ThemeCompileError("THEME_SIZE_LIMIT_EXCEEDED", message, location);
}

/** 参与缓存有效性判断的文件元数据。File metadata that decides whether a cached compile is still valid. */
export type ThemeDependency = {
  /** 引用路径：根目录实际路径下按包内路径拼接的绝对路径。The referenced path: the package path under the root real path. */
  path: string;
  realPath: string;
  dev: bigint;
  ino: bigint;
  size: bigint;
  mtimeNs: bigint;
  ctimeNs: bigint;
};

type ResourceKind = "image" | "font";

const IMAGE_TYPES: ReadonlyMap<string, string> = new Map([
  ["png", "image/png"],
  ["jpg", "image/jpeg"],
  ["jpeg", "image/jpeg"],
  ["gif", "image/gif"],
  ["webp", "image/webp"],
  ["avif", "image/avif"],
  ["svg", "image/svg+xml"],
  ["ico", "image/vnd.microsoft.icon"],
]);
const FONT_TYPES: ReadonlyMap<string, string> = new Map([
  ["woff", "font/woff"],
  ["woff2", "font/woff2"],
  ["ttf", "font/ttf"],
  ["otf", "font/otf"],
]);
const IMAGE_MIME_TYPES = new Set(IMAGE_TYPES.values());
const FONT_MIME_TYPES = new Set(FONT_TYPES.values());

const SVG_NAMESPACE = "http://www.w3.org/2000/svg";
const XLINK_NAMESPACE = "http://www.w3.org/1999/xlink";
const XHTML_NAMESPACE = "http://www.w3.org/1999/xhtml";
const XMLNS_NAMESPACE = "http://www.w3.org/2000/xmlns/";

// 字符串参数就是资源地址的函数。Functions whose string arguments are resource addresses.
const ADDRESS_FUNCTIONS = new Set(["url", "src"]);
const IMAGE_SET_FUNCTIONS = new Set(["image-set", "-webkit-image-set"]);
// 字符串或候选项可能是资源地址、因此不能含动态表达式的函数。Functions whose strings or candidates may be resource
// addresses, so they cannot hold dynamic expressions.
const STRING_RESOURCE_FUNCTIONS = new Set([...ADDRESS_FUNCTIONS, ...IMAGE_SET_FUNCTIONS, "image"]);
const DYNAMIC_FUNCTIONS = new Set(["var", "env", "attr"]);
// image-set 候选项中可以递归检查的图片函数。Image functions in image-set candidates that can be checked recursively.
const IMAGE_FUNCTIONS = new Set([
  ...STRING_RESOURCE_FUNCTIONS,
  "linear-gradient",
  "radial-gradient",
  "conic-gradient",
  "repeating-linear-gradient",
  "repeating-radial-gradient",
  "repeating-conic-gradient",
  "-webkit-linear-gradient",
  "-webkit-radial-gradient",
  "-webkit-repeating-linear-gradient",
  "-webkit-repeating-radial-gradient",
  "cross-fade",
  "-webkit-cross-fade",
  "element",
  "paint",
]);

/** 解码 CSS escape 后的小写名称。The lowercase name with CSS escapes decoded. */
export function cssName(raw: string): string {
  return ident.decode(raw).toLowerCase();
}

export function atRuleName(rule: AtRule): string {
  return cssName(rule.name);
}

const utf8 = new TextDecoder("utf-8", { fatal: true });

export function decodeUtf8(data: Uint8Array, location: ThemeSourceLocation): string {
  try {
    return utf8.decode(data);
  } catch (error) {
    if (!(error instanceof TypeError)) throw error;
    throw new ThemeCompileError("THEME_PARSE_FAILED", "The file is not valid UTF-8.", location);
  }
}

/** postcss 解析 CSS；结构解析错误报告文件与行列。Parses CSS with postcss; structure errors report the file, line and column. */
export function parseCss(text: string, from: string, file: string): Root {
  try {
    return postcss.parse(text, { from });
  } catch (error) {
    if (!(error instanceof CssSyntaxError)) throw error;
    throw new ThemeCompileError("THEME_PARSE_FAILED", error.reason, {
      file,
      line: error.line,
      column: error.column,
    });
  }
}

/**
 * css-tree 解析：恢复错误与顶层错误都报告为解析失败，不产生 Raw 回退。
 * Parses with css-tree; recovery errors and top-level errors both report a parse failure instead of a Raw fallback.
 */
export function parseCssTree(
  source: string,
  options: ParseOptions,
  subject: string,
  location: ThemeSourceLocation,
): CssNode {
  const errors: string[] = [];
  let ast: CssNode | null = null;
  try {
    ast = parse(source, { ...options, onParseError: (error) => errors.push(error.message) });
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    errors.push(error.message);
  }
  if (ast === null || errors.length > 0) {
    throw new ThemeCompileError(
      "THEME_PARSE_FAILED",
      `${subject} cannot be parsed: ${errors[0]}`,
      location,
    );
  }
  return ast;
}

export function nodeLocation(node: ChildNode, file: string): ThemeSourceLocation {
  const start = node.source?.start;
  return start ? { file, line: start.line, column: start.column } : { file };
}

/** 包内引用解析的结果。A resolved package reference. */
export type PackageReference = { packagePath: string; fragment: string };

const SCHEME = /^[a-z][a-z0-9+.-]*:/iu;

/** 带协议或以 `//` 开头的地址。An address with a scheme or starting with `//`. */
function isExternalReference(reference: string): boolean {
  return SCHEME.test(reference) || reference.startsWith("//");
}

/**
 * 解析包内引用：相对路径按来源文件所在目录，以 `/` 开头的路径从根目录；外部地址、查询参数与离开根目录的路径都不合法。
 * Resolves a package reference: relative paths against the source file's directory and `/` paths against the root;
 * external addresses, queries and paths leaving the root are invalid.
 */
export function resolvePackageReference(
  reference: string,
  fromPackagePath: string,
  location: ThemeSourceLocation,
): PackageReference {
  if (reference.trim() === "") {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      "An empty URL cannot be resolved.",
      location,
    );
  }
  if (isExternalReference(reference)) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${reference}" refers outside the extension package; use a package file or a data URL.`,
      location,
    );
  }
  if (reference.includes("\\")) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${reference}" contains a backslash; package paths use "/".`,
      location,
    );
  }
  const hash = reference.indexOf("#");
  const address = hash === -1 ? reference : reference.slice(0, hash);
  const fragment = hash === -1 ? "" : reference.slice(hash);
  if (address.includes("?")) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${reference}" has a query; package references cannot have one.`,
      location,
    );
  }
  let decoded: string;
  try {
    decoded = decodeURIComponent(address);
  } catch (error) {
    if (!(error instanceof URIError)) throw error;
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${reference}" has invalid percent-encoding.`,
      location,
    );
  }
  const joined = decoded.startsWith("/")
    ? posix.normalize(decoded.slice(1))
    : posix.normalize(posix.join(posix.dirname(fromPackagePath), decoded));
  if (joined === ".." || joined.startsWith("../")) {
    throw new ThemeCompileError(
      "PACKAGE_PATH_OUTSIDE_ROOT",
      `"${reference}" resolves outside the extension directory.`,
      location,
    );
  }
  if (!isValidPackagePath(joined)) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${reference}" is not a valid package path.`,
      location,
    );
  }
  return { packagePath: joined, fragment };
}

/** 一次编译的状态：依赖、预算与已内嵌的资源。The state of one compile: dependencies, budgets and inlined resources. */
export class ThemeCompileContext {
  public readonly dependencies = new Map<string, ThemeDependency>();
  /** 当前正在检查的 SVG 文件，用于发现循环引用。SVG files being checked, for detecting reference cycles. */
  public readonly svgStack: string[] = [];
  /** 完整 `cssText` 的 UTF-8 体积估计。The UTF-8 size estimate of the complete `cssText`. */
  public outputBytes = 0;
  private inputBytes = 0;
  private readonly contents = new Map<string, Buffer>();
  private readonly assets = new Map<string, string>();
  private readonly countedDataUrls = new Set<string>();

  public constructor(public readonly rootRealPath: string) {}

  /** 包内路径对应的引用路径。The referenced path of a package path. */
  public pathOf(packagePath: string): string {
    return join(this.rootRealPath, ...packagePath.split("/"));
  }

  /** 引用路径对应的包内路径。The package path of a referenced path. */
  public packagePathOf(path: string): string {
    return relative(this.rootRealPath, path).split(sep).join("/");
  }

  /**
   * 经过实际路径检查后读取包内文件，并计入文件数量与输入体积。
   * Reads a package file after its real path checks, counting it toward the file count and the input size.
   */
  public async readFile(
    packagePath: string,
    maxBytes: number,
    location: ThemeSourceLocation,
  ): Promise<Buffer> {
    const path = this.pathOf(packagePath);
    const cached = this.contents.get(path);
    if (cached) return cached;
    let realPath: string;
    try {
      realPath = await realpath(path);
    } catch (error) {
      const code = (error as NodeJS.ErrnoException).code;
      if (code !== "ENOENT" && code !== "ENOTDIR" && code !== "ELOOP") throw error;
      throw new ThemeCompileError(
        "THEME_FILE_NOT_FOUND",
        `"${packagePath}" does not exist.`,
        location,
      );
    }
    if (!isPathInside(this.rootRealPath, realPath)) {
      throw new ThemeCompileError(
        "PACKAGE_PATH_OUTSIDE_ROOT",
        `"${packagePath}" resolves outside the extension directory.`,
        location,
      );
    }
    if (this.dependencies.size >= MAX_THEME_FILES) {
      throw sizeLimitError(
        `"${packagePath}" would be file ${this.dependencies.size + 1}; a theme reads at most ${MAX_THEME_FILES} files.`,
        location,
      );
    }
    const handle = await open(realPath, "r");
    try {
      const info = await handle.stat({ bigint: true });
      if (!info.isFile()) {
        throw new ThemeCompileError(
          "THEME_FILE_NOT_FOUND",
          `"${packagePath}" is not a file.`,
          location,
        );
      }
      const size = Number(info.size);
      if (size > maxBytes) {
        throw sizeLimitError(
          `"${packagePath}" is ${size} bytes; the limit is ${describeLimit(maxBytes)}.`,
          location,
        );
      }
      this.addInput(size, `"${packagePath}"`, location);
      const data = await handle.readFile();
      if (data.length !== size) {
        throw new ThemeCompileError(
          "THEME_PARSE_FAILED",
          `"${packagePath}" changed while it was read.`,
          location,
        );
      }
      this.dependencies.set(path, {
        path,
        realPath,
        dev: info.dev,
        ino: info.ino,
        size: info.size,
        mtimeNs: info.mtimeNs,
        ctimeNs: info.ctimeNs,
      });
      this.contents.set(path, data);
      return data;
    } finally {
      await handle.close();
    }
  }

  /** 已有 data URL 的解码数据计入输入体积，每个位置只计一次。Decoded data URLs count toward the input size once per position. */
  public countDataUrl(key: string, bytes: number, location: ThemeSourceLocation): void {
    if (this.countedDataUrls.has(key)) return;
    this.countedDataUrls.add(key);
    this.addInput(bytes, "A data URL", location);
  }

  /** 输出体积估计增长，超过上限时在构造结果之前终止。Grows the output estimate and stops before building an oversized result. */
  public growOutput(delta: number, location: ThemeSourceLocation | null): void {
    this.outputBytes += delta;
    if (this.outputBytes > MAX_THEME_CSS_TEXT_BYTES) {
      throw sizeLimitError(
        `The theme CSS would be ${this.outputBytes} bytes; the limit is ${describeLimit(MAX_THEME_CSS_TEXT_BYTES)}.`,
        location,
      );
    }
  }

  /** 包内图片或字体转换为 data URL，同一文件只读取一次。Converts a package image or font to a data URL, reading each file once. */
  public async inlineAsset(
    packagePath: string,
    kind: ResourceKind,
    location: ThemeSourceLocation,
  ): Promise<string> {
    const key = `${kind}:${packagePath}`;
    const cached = this.assets.get(key);
    if (cached !== undefined) return cached;
    const mimeType = packageMimeType(packagePath, kind, location);
    let data: Uint8Array = await this.readFile(packagePath, MAX_THEME_ASSET_BYTES, location);
    if (mimeType === "image/svg+xml") {
      const source = decodeUtf8(data, { file: packagePath });
      const checked = await checkSvg(source, packagePath, this, { file: packagePath });
      data = Buffer.from(checked, "utf8");
    }
    const dataUrl = `data:${mimeType};base64,${Buffer.from(data).toString("base64")}`;
    this.assets.set(key, dataUrl);
    return dataUrl;
  }

  private addInput(bytes: number, label: string, location: ThemeSourceLocation): void {
    this.inputBytes += bytes;
    if (this.inputBytes > MAX_THEME_INPUT_BYTES) {
      throw sizeLimitError(
        `${label} brings the theme's input files and data URLs to ${this.inputBytes} bytes; the limit is ${describeLimit(MAX_THEME_INPUT_BYTES)}.`,
        location,
      );
    }
  }
}

/** 包内文件的 MIME：扩展名与 MIME 数据库的结果都必须在允许范围内。A package file's MIME: the extension and the database result must both be allowed. */
function packageMimeType(
  packagePath: string,
  kind: ResourceKind,
  location: ThemeSourceLocation,
): string {
  const extension = posix.extname(packagePath).slice(1).toLowerCase();
  const allowed = kind === "font" ? FONT_TYPES : IMAGE_TYPES;
  const other = kind === "font" ? IMAGE_TYPES : FONT_TYPES;
  const mimeType = lookup(packagePath);
  if (mimeType !== false && allowed.get(extension) === mimeType) return mimeType;
  if (mimeType !== false && other.get(extension) === mimeType) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      kind === "font"
        ? `"${packagePath}" is an image; @font-face sources must be WOFF, WOFF2, TTF or OTF fonts.`
        : `"${packagePath}" is a font; only @font-face sources can use fonts.`,
      location,
    );
  }
  throw new ThemeCompileError(
    "THEME_RESOURCE_INVALID",
    kind === "font"
      ? `"${packagePath}" is not an allowed font type (WOFF, WOFF2, TTF, OTF).`
      : `"${packagePath}" is not an allowed image type (PNG, JPEG, GIF, WebP, AVIF, SVG, ICO).`,
    location,
  );
}

/**
 * 校验已有的 data URL：MIME 必须适合使用位置，解码后的体积计入预算；SVG 同样检查内容。
 * Validates an existing data URL: its MIME must suit the position, its decoded size counts toward the budgets, and
 * SVG content is checked as well.
 */
async function checkDataUrl(
  value: string,
  kind: ResourceKind,
  context: ThemeCompileContext,
  key: string,
  location: ThemeSourceLocation,
): Promise<void> {
  const parsed = parseDataUrl(value);
  if (!parsed) {
    throw new ThemeCompileError("THEME_RESOURCE_INVALID", "A data URL cannot be parsed.", location);
  }
  const essence = parsed.mimeType.essence;
  const allowed = kind === "font" ? FONT_MIME_TYPES : IMAGE_MIME_TYPES;
  if (!allowed.has(essence)) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `A data URL of type "${essence}" cannot be used ${kind === "font" ? "as an @font-face source" : "as an image"}.`,
      location,
    );
  }
  if (parsed.body.length > MAX_THEME_ASSET_BYTES) {
    throw sizeLimitError(
      `A data URL decodes to ${parsed.body.length} bytes; the limit is ${describeLimit(MAX_THEME_ASSET_BYTES)}.`,
      location,
    );
  }
  context.countDataUrl(key, parsed.body.length, location);
  if (essence === "image/svg+xml") {
    const charset = parsed.mimeType.parameters.get("charset");
    if (charset !== undefined && charset.toLowerCase() !== "utf-8") {
      throw new ThemeCompileError(
        "THEME_RESOURCE_INVALID",
        "SVG data URLs must be UTF-8.",
        location,
      );
    }
    await checkSvg(decodeUtf8(parsed.body, location), null, context, location);
  }
}

/** 资源引用的检查范围：来源文件与使用位置。Where a resource reference is checked: its source file and position. */
type ResourceScope = {
  context: ThemeCompileContext;
  /** 解析相对路径的来源文件；data URL 中的内容没有来源文件。The source file for relative paths; content inside data URLs has none. */
  base: string | null;
  kind: ResourceKind;
  location: ThemeSourceLocation;
};

/**
 * 检查一个资源地址，返回替换后的地址：包内文件转换为 data URL，data URL 与片段引用保持不变。
 * Checks one resource address and returns its replacement: package files become data URLs, while data URLs and
 * fragment references stay unchanged.
 */
async function resolveResource(value: string, scope: ResourceScope, key: string): Promise<string> {
  const trimmed = value.trim();
  if (trimmed.startsWith("#")) return value;
  if (/^data:/iu.test(trimmed)) {
    await checkDataUrl(trimmed, scope.kind, scope.context, key, scope.location);
    return value;
  }
  if (scope.base === null && !isExternalReference(trimmed)) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${trimmed}" is a relative reference inside a data URL; use a data URL or a fragment.`,
      scope.location,
    );
  }
  const reference = resolvePackageReference(trimmed, scope.base ?? "", scope.location);
  const dataUrl = await scope.context.inlineAsset(
    reference.packagePath,
    scope.kind,
    scope.location,
  );
  return `${dataUrl}${reference.fragment}`;
}

type Replacement = { start: number; end: number; text: string };

function locOf(node: CssNode): CssLocation {
  if (!node.loc) throw new Error("css-tree did not record a position.");
  return node.loc;
}

function significantChildren(node: CssNode): CssNode[] {
  return "children" in node && node.children
    ? node.children
        .toArray()
        .filter((child) => child.type !== "WhiteSpace" && child.type !== "Comment")
    : [];
}

function unverifiable(description: string, location: ThemeSourceLocation): ThemeCompileError {
  return new ThemeCompileError(
    "THEME_RESOURCE_INVALID",
    `${description} cannot be checked statically.`,
    location,
  );
}

/**
 * 遍历 css-tree 的值节点，检查资源地址并收集替换：`url()`、`src()`、`image-set()`、`image()`；Raw 节点不能检查。
 * Walks css-tree value nodes, checking resource addresses and collecting replacements: `url()`, `src()`,
 * `image-set()` and `image()`; Raw nodes cannot be checked.
 */
async function scanValueNode(
  node: CssNode,
  scope: ResourceScope,
  replacements: Replacement[],
  keyPrefix: string,
): Promise<void> {
  const replace = async (target: CssNode, value: string, encode: (value: string) => string) => {
    const loc = locOf(target);
    const resolved = await resolveResource(value, scope, `${keyPrefix}:${loc.start.offset}`);
    if (resolved !== value)
      replacements.push({ start: loc.start.offset, end: loc.end.offset, text: encode(resolved) });
  };
  switch (node.type) {
    case "Raw":
      throw unverifiable(`"${node.value.trim()}"`, scope.location);
    case "Declaration":
      await scanValueNode(node.value, scope, replacements, keyPrefix);
      return;
    case "Url":
      await replace(node, node.value, cssUrl.encode);
      return;
    case "Function": {
      const name = cssName(node.name);
      if (ADDRESS_FUNCTIONS.has(name)) {
        const [address, ...rest] = significantChildren(node);
        if (address?.type !== "String" || rest.length > 0)
          throw unverifiable(`${name}()`, scope.location);
        await replace(address, address.value, (value) => cssString.encode(value));
        return;
      }
      if (IMAGE_SET_FUNCTIONS.has(name)) {
        await scanImageSet(node, scope, replacements, keyPrefix);
        return;
      }
      if (name === "image") {
        for (const child of significantChildren(node)) {
          if (child.type === "String")
            await replace(child, child.value, (value) => cssString.encode(value));
          else if (child.type === "Function" && DYNAMIC_FUNCTIONS.has(cssName(child.name)))
            throw unverifiable(`${cssName(child.name)}() in image()`, scope.location);
          else await scanValueNode(child, scope, replacements, keyPrefix);
        }
        return;
      }
      break;
    }
  }
  if ("children" in node && node.children) {
    for (const child of node.children.toArray())
      await scanValueNode(child, scope, replacements, keyPrefix);
  }
}

/**
 * image-set 的每个候选项：图片为字面量 URL、字符串地址或可以递归检查的图片函数，之后只能有分辨率与 `type()`。
 * Each image-set candidate: the image is a literal URL, a string address or an image function that can be checked
 * recursively, followed only by a resolution and `type()`.
 */
async function scanImageSet(
  node: Extract<CssNode, { type: "Function" }>,
  scope: ResourceScope,
  replacements: Replacement[],
  keyPrefix: string,
): Promise<void> {
  const name = cssName(node.name);
  const candidates: CssNode[][] = [[]];
  for (const child of significantChildren(node)) {
    if (child.type === "Operator" && child.value === ",") candidates.push([]);
    else candidates[candidates.length - 1].push(child);
  }
  for (const [image, ...descriptors] of candidates) {
    if (!image) throw unverifiable(`An empty ${name}() candidate`, scope.location);
    if (image.type === "String") {
      const loc = locOf(image);
      const resolved = await resolveResource(
        image.value,
        scope,
        `${keyPrefix}:${loc.start.offset}`,
      );
      if (resolved !== image.value)
        replacements.push({
          start: loc.start.offset,
          end: loc.end.offset,
          text: cssString.encode(resolved),
        });
    } else if (image.type === "Url") {
      await scanValueNode(image, scope, replacements, keyPrefix);
    } else if (image.type === "Function" && IMAGE_FUNCTIONS.has(cssName(image.name))) {
      await scanValueNode(image, scope, replacements, keyPrefix);
    } else {
      const label = image.type === "Function" ? `${cssName(image.name)}()` : `"${image.type}"`;
      throw unverifiable(`${label} as a ${name}() image`, scope.location);
    }
    for (const descriptor of descriptors) {
      if (descriptor.type === "Dimension") continue;
      if (descriptor.type === "Function" && cssName(descriptor.name) === "type") continue;
      const label =
        descriptor.type === "Function" ? `${cssName(descriptor.name)}()` : `"${descriptor.type}"`;
      throw unverifiable(`${label} in a ${name}() candidate`, scope.location);
    }
  }
}

function applyReplacements(source: string, replacements: Replacement[]): string {
  let result = source;
  for (const replacement of [...replacements].sort((left, right) => right.start - left.start)) {
    result = result.slice(0, replacement.start) + replacement.text + result.slice(replacement.end);
  }
  return result;
}

/**
 * 检查一个声明值并内嵌包内资源，返回新的值；值必须能完整解析。
 * Checks one declaration value and inlines package resources, returning the new value; the value must parse fully.
 */
async function rewriteValue(
  value: string,
  property: string,
  scope: ResourceScope,
  keyPrefix: string,
): Promise<string> {
  const ast = parseCssTree(
    value,
    { context: "value", positions: true, parseCustomProperty: true },
    `The value of "${property}"`,
    scope.location,
  );
  const replacements: Replacement[] = [];
  await scanValueNode(ast, scope, replacements, keyPrefix);
  return applyReplacements(value, replacements);
}

/**
 * 用 tokenizer 检查不按 CSS 值语法解析的文字（at-rule prelude、SVG 属性）：url token 与地址函数作为资源检查并替换，
 * 其他图片函数不能使用。
 * Checks text that is not parsed as a CSS value (at-rule preludes, SVG attributes) with the tokenizer: url tokens and
 * address functions are checked and replaced as resources, and other image functions cannot be used.
 */
async function rewriteTokens(
  source: string,
  scope: ResourceScope,
  keyPrefix: string,
  label: string,
): Promise<string> {
  const tokens: { type: number; start: number; end: number }[] = [];
  tokenize(source, (type, start, end) => tokens.push({ type, start, end }));
  const replacements: Replacement[] = [];
  for (let index = 0; index < tokens.length; index += 1) {
    const token = tokens[index];
    const raw = source.slice(token.start, token.end);
    if (token.type === tokenTypes.BadUrl || token.type === tokenTypes.BadString) {
      throw unverifiable(`"${raw}" in ${label}`, scope.location);
    }
    if (token.type === tokenTypes.Url) {
      const value = cssUrl.decode(`url(${raw.slice(raw.indexOf("(") + 1)}`);
      const resolved = await resolveResource(value, scope, `${keyPrefix}:${token.start}`);
      if (resolved !== value)
        replacements.push({ start: token.start, end: token.end, text: cssUrl.encode(resolved) });
      continue;
    }
    if (token.type !== tokenTypes.Function) continue;
    const name = cssName(raw.slice(0, -1));
    if (ADDRESS_FUNCTIONS.has(name)) {
      const inner = tokens
        .slice(index + 1)
        .filter((item) => item.type !== tokenTypes.WhiteSpace && item.type !== tokenTypes.Comment);
      const [address, close] = inner;
      if (address?.type !== tokenTypes.String || close?.type !== tokenTypes.RightParenthesis) {
        throw unverifiable(`${name}() in ${label}`, scope.location);
      }
      const value = cssString.decode(source.slice(address.start, address.end));
      const resolved = await resolveResource(value, scope, `${keyPrefix}:${address.start}`);
      if (resolved !== value)
        replacements.push({
          start: address.start,
          end: address.end,
          text: cssString.encode(resolved),
        });
    } else if (STRING_RESOURCE_FUNCTIONS.has(name)) {
      throw new ThemeCompileError(
        "THEME_RESOURCE_INVALID",
        `${name}() cannot be used in ${label}.`,
        scope.location,
      );
    }
  }
  return applyReplacements(source, replacements);
}

/**
 * 检查 postcss 样式表中的全部声明与 at-rule，并内嵌包内资源。`baseOf` 给出每个节点解析相对路径的来源文件。
 * Checks every declaration and at-rule of a postcss stylesheet and inlines package resources. `baseOf` gives the
 * source file each node resolves relative paths against.
 */
export async function rewriteStylesheet(
  root: Root,
  context: ThemeCompileContext,
  baseOf: (node: ChildNode) => { base: string | null; file: string },
): Promise<void> {
  const nodes: (AtRule | Declaration)[] = [];
  root.walk((node) => {
    if (node.type === "atrule" || node.type === "decl") nodes.push(node);
  });
  for (const node of nodes) {
    const { base, file } = baseOf(node);
    const location = nodeLocation(node, file);
    const keyPrefix = `${file}:${location.line ?? 0}:${location.column ?? 0}`;
    if (node.type === "atrule") {
      const name = atRuleName(node);
      if (name === "import") {
        throw new ThemeCompileError(
          "THEME_IMPORT_INVALID",
          "This @import was not expanded; imports must be package CSS files at the top of a stylesheet.",
          location,
        );
      }
      if (name === "namespace") {
        checkNamespacePrelude(node.params, location);
        continue;
      }
      const scope: ResourceScope = { context, base, kind: "image", location };
      const params = await rewriteTokens(node.params, scope, keyPrefix, `@${name} rules`);
      if (params !== node.params) {
        context.growOutput(Buffer.byteLength(params) - Buffer.byteLength(node.params), location);
        node.params = params;
      }
      continue;
    }
    const property = cssName(node.prop);
    const parent = node.parent;
    const kind: ResourceKind =
      property === "src" &&
      parent?.type === "atrule" &&
      atRuleName(parent as AtRule) === "font-face"
        ? "font"
        : "image";
    const scope: ResourceScope = { context, base, kind, location };
    const value = await rewriteValue(node.value, node.prop, scope, keyPrefix);
    if (value !== node.value) {
      context.growOutput(Buffer.byteLength(value) - Buffer.byteLength(node.value), location);
      node.value = value;
    }
  }
}

/** `@namespace` 的 URI 是 namespace 标识符，不加载资源。The `@namespace` URI is a namespace name and loads nothing. */
function checkNamespacePrelude(params: string, location: ThemeSourceLocation): void {
  tokenize(params, (type, start, end) => {
    if (type === tokenTypes.Function) {
      const name = cssName(params.slice(start, end - 1));
      if (name !== "url") {
        throw new ThemeCompileError(
          "THEME_RESOURCE_INVALID",
          `${name}() cannot be used in @namespace rules.`,
          location,
        );
      }
    }
    if (type === tokenTypes.BadUrl || type === tokenTypes.BadString) {
      throw unverifiable(`"${params.slice(start, end)}" in @namespace rules`, location);
    }
  });
}

/* ---------- SVG ---------- */

const ELEMENT_NODE = 1;
const PROCESSING_INSTRUCTION_NODE = 7;

/**
 * 检查 SVG 图片：不能声明 DOCTYPE，不能有脚本、事件属性、processing instruction 与 XHTML 内容；属性与内嵌 CSS 中的资源
 * 地址按 SVG 文件所在目录解析并内嵌。返回替换资源后的 SVG 文字。
 * Checks an SVG image: no DOCTYPE, scripts, event attributes, processing instructions or XHTML content; resource
 * addresses in attributes and embedded CSS resolve against the SVG file's directory and are inlined. Returns the SVG
 * text with resources replaced.
 */
export async function checkSvg(
  source: string,
  packagePath: string | null,
  context: ThemeCompileContext,
  location: ThemeSourceLocation,
): Promise<string> {
  if (packagePath !== null && context.svgStack.includes(packagePath)) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `"${packagePath}" refers to itself through ${[...context.svgStack, packagePath].join(" → ")}.`,
      location,
    );
  }
  let document: XmlDocument;
  try {
    document = new DOMParser({
      onError: (level, message) => {
        throw new Error(`${level}: ${message}`);
      },
    }).parseFromString(source, "image/svg+xml");
  } catch (error) {
    throw new ThemeCompileError(
      "THEME_PARSE_FAILED",
      `The SVG cannot be parsed: ${error instanceof Error ? error.message : String(error)}`,
      location,
    );
  }
  if (document.doctype) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      "SVG images cannot declare a DOCTYPE or entities.",
      location,
    );
  }
  const root = document.documentElement;
  if (!root || root.namespaceURI !== SVG_NAMESPACE || root.localName !== "svg") {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      "The image is not an SVG document.",
      location,
    );
  }
  if (packagePath !== null) context.svgStack.push(packagePath);
  try {
    const scope: ResourceScope = { context, base: packagePath, kind: "image", location };
    const changed = await checkSvgNode(document, scope);
    return changed ? new XMLSerializer().serializeToString(document) : source;
  } finally {
    if (packagePath !== null) context.svgStack.pop();
  }
}

async function checkSvgNode(node: XmlNode, scope: ResourceScope): Promise<boolean> {
  let changed = false;
  for (const child of Array.from(node.childNodes)) {
    if (child.nodeType === PROCESSING_INSTRUCTION_NODE) {
      throw new ThemeCompileError(
        "THEME_RESOURCE_INVALID",
        "SVG images cannot contain processing instructions.",
        scope.location,
      );
    }
    if (child.nodeType !== ELEMENT_NODE) continue;
    if (await checkSvgElement(child as XmlElement, scope)) changed = true;
  }
  return changed;
}

async function checkSvgElement(element: XmlElement, scope: ResourceScope): Promise<boolean> {
  const name = element.localName;
  if (element.namespaceURI === XHTML_NAMESPACE) {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `SVG images cannot contain XHTML elements (<${name}>).`,
      scope.location,
    );
  }
  if (name === "script" || name === "foreignObject" || name === "handler") {
    throw new ThemeCompileError(
      "THEME_RESOURCE_INVALID",
      `SVG images cannot contain <${name}> elements.`,
      scope.location,
    );
  }
  let changed = false;
  const keyPrefix = `${scope.location.file}:svg:${name}`;
  for (const attribute of Array.from(element.attributes)) {
    if (
      attribute.namespaceURI === XMLNS_NAMESPACE ||
      attribute.name === "xmlns" ||
      attribute.prefix === "xmlns"
    )
      continue;
    const local = attribute.localName ?? attribute.name;
    let value = attribute.value;
    if (attribute.namespaceURI === null && /^on/iu.test(local)) {
      throw new ThemeCompileError(
        "THEME_RESOURCE_INVALID",
        `SVG images cannot contain event attributes ("${attribute.name}").`,
        scope.location,
      );
    }
    if (
      local === "href" &&
      (attribute.namespaceURI === null || attribute.namespaceURI === XLINK_NAMESPACE)
    ) {
      value = await rewriteSvgHref(element, attribute.value, scope, keyPrefix);
    } else if (attribute.namespaceURI === null && local === "style") {
      value = await rewriteInlineStyle(attribute.value, scope, `${keyPrefix}:style`);
    } else {
      value = await rewriteTokens(
        attribute.value,
        scope,
        `${keyPrefix}:${attribute.name}`,
        `the SVG "${attribute.name}" attribute`,
      );
    }
    if (value !== attribute.value) {
      element.setAttributeNS(attribute.namespaceURI, attribute.name, value);
      changed = true;
    }
  }
  if (element.namespaceURI === SVG_NAMESPACE && name === "style") {
    const css = element.textContent ?? "";
    const checked = await rewriteEmbeddedCss(css, scope);
    if (checked !== css) {
      const owner = element.ownerDocument;
      if (!owner) throw new Error("An SVG element has no owner document.");
      for (const child of Array.from(element.childNodes)) element.removeChild(child);
      element.appendChild(owner.createTextNode(checked));
      changed = true;
    }
    return changed;
  }
  if (await checkSvgNode(element, scope)) changed = true;
  return changed;
}

/**
 * `href`：`<image>`、`<feImage>` 可以引用包内图片或 data URL，其他元素只能引用当前文档的片段。
 * `href`: `<image>` and `<feImage>` may refer to package images or data URLs; other elements refer only to fragments
 * of the document.
 */
async function rewriteSvgHref(
  element: XmlElement,
  value: string,
  scope: ResourceScope,
  keyPrefix: string,
): Promise<string> {
  if (value.trim().startsWith("#")) return value;
  if (
    element.namespaceURI === SVG_NAMESPACE &&
    (element.localName === "image" || element.localName === "feImage")
  ) {
    return resolveResource(value, scope, `${keyPrefix}:href`);
  }
  throw new ThemeCompileError(
    "THEME_RESOURCE_INVALID",
    `<${element.localName}> can only refer to fragments of the same SVG ("${value}").`,
    scope.location,
  );
}

async function rewriteInlineStyle(
  value: string,
  scope: ResourceScope,
  keyPrefix: string,
): Promise<string> {
  const ast = parseCssTree(
    value,
    { context: "declarationList", positions: true, parseCustomProperty: true },
    "An SVG style attribute",
    scope.location,
  );
  const replacements: Replacement[] = [];
  await scanValueNode(ast, scope, replacements, keyPrefix);
  return applyReplacements(value, replacements);
}

async function rewriteEmbeddedCss(css: string, scope: ResourceScope): Promise<string> {
  const root = parseCss(css, scope.location.file, scope.location.file);
  await rewriteStylesheet(root, scope.context, () => ({
    base: scope.base,
    file: scope.location.file,
  }));
  return root.toString();
}

/* ---------- 最终文本 Final text ---------- */

function isAllowedFinalAddress(value: string): boolean {
  const trimmed = value.trim();
  if (trimmed.startsWith("#")) return true;
  if (!/^data:/iu.test(trimmed)) return false;
  // 内容已在来源检查中校验，这里只解析逗号之前的 MIME。The content was validated by the source checks, so only the MIME
  // before the comma is parsed here.
  const comma = trimmed.indexOf(",");
  if (comma === -1) return false;
  const parsed = parseDataUrl(trimmed.slice(0, comma + 1));
  return (
    parsed !== null &&
    (IMAGE_MIME_TYPES.has(parsed.mimeType.essence) || FONT_MIME_TYPES.has(parsed.mimeType.essence))
  );
}

/**
 * 用 CSS tokenizer 检查完整的 `cssText`：不能有 `@import`；所有具有资源加载含义的地址只能是允许类型的 data URL 或片段引用；
 * 地址函数与 image-set 中不能有动态表达式。与结构解析无关，覆盖选择器与任意位置。
 * Checks the complete `cssText` with the CSS tokenizer: no `@import`; every address that loads a resource is a data
 * URL of an allowed type or a fragment reference; address functions and image-set hold no dynamic expressions.
 * Independent of structure parsing, so selectors and every other position are covered.
 */
export function verifyThemeCssText(text: string): void {
  const stack: string[] = [];
  let atRule: string | null = null;
  let failure: string | null = null;
  const fail = (message: string) => {
    failure ??= message;
  };
  tokenize(text, (type, start, end) => {
    if (failure !== null) return;
    const raw = text.slice(start, end);
    const top = stack.length > 0 ? stack[stack.length - 1] : null;
    switch (type) {
      case tokenTypes.WhiteSpace:
      case tokenTypes.Comment:
        return;
      case tokenTypes.AtKeyword:
        atRule = cssName(raw.slice(1));
        if (atRule === "import") fail("The theme CSS contains an @import rule.");
        return;
      case tokenTypes.Semicolon:
      case tokenTypes.LeftCurlyBracket:
      case tokenTypes.RightCurlyBracket:
        if (top === null) atRule = null;
        break;
      case tokenTypes.BadUrl:
      case tokenTypes.BadString:
        fail(`The theme CSS contains "${raw}", which cannot be checked.`);
        return;
      case tokenTypes.Url: {
        const value = cssUrl.decode(`url(${raw.slice(raw.indexOf("(") + 1)}`);
        if (atRule === "namespace" && top === null) return;
        if (!isAllowedFinalAddress(value)) fail(`The theme CSS refers to "${value}".`);
        if (top !== null && ADDRESS_FUNCTIONS.has(top))
          fail(`url() inside ${top}() cannot be checked.`);
        return;
      }
      case tokenTypes.Function: {
        const name = cssName(raw.slice(0, -1));
        if (top !== null && ADDRESS_FUNCTIONS.has(top))
          fail(`${name}() inside ${top}() cannot be checked.`);
        if (top !== null && STRING_RESOURCE_FUNCTIONS.has(top) && DYNAMIC_FUNCTIONS.has(name))
          fail(`${name}() inside ${top}() cannot be checked.`);
        stack.push(name);
        return;
      }
      case tokenTypes.LeftParenthesis:
        stack.push("(");
        return;
      case tokenTypes.LeftSquareBracket:
        stack.push("[");
        return;
      case tokenTypes.RightParenthesis:
      case tokenTypes.RightSquareBracket:
        stack.pop();
        return;
      case tokenTypes.String:
        if (top !== null && STRING_RESOURCE_FUNCTIONS.has(top)) {
          const value = cssString.decode(raw);
          if (!isAllowedFinalAddress(value)) fail(`The theme CSS refers to "${value}".`);
        }
        return;
    }
    if (top !== null && ADDRESS_FUNCTIONS.has(top))
      fail(`"${raw}" inside ${top}() cannot be checked.`);
  });
  if (failure !== null) throw new ThemeCompileError("THEME_RESOURCE_INVALID", failure);
}
