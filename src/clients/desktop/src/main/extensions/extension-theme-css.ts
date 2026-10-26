import { createHash } from "node:crypto";
import { dirname } from "node:path";
import postcss, { type AtRule, type Root } from "postcss";
import postcssImport from "postcss-import";

import {
  atRuleName,
  decodeUtf8,
  MAX_THEME_CSS_FILE_BYTES,
  MAX_THEME_CSS_TEXT_BYTES,
  MAX_THEME_IMPORT_DEPTH,
  MAX_THEME_IMPORT_EXPANSIONS,
  nodeLocation,
  parseCss,
  parseCssTree,
  resolvePackageReference,
  rewriteStylesheet,
  sizeLimitError,
  ThemeCompileContext,
  ThemeCompileError,
  type ThemeDependency,
  type ThemeSourceLocation,
  verifyThemeCssText,
} from "./extension-theme-resources";

/** 编译规则的版本；规则变化时递增，使缓存失效。The compile rules version; bump it when rules change so caches invalidate. */
export const THEME_CSS_COMPILER_VERSION = 1;

/** 处理后的主题样式。A processed theme style. */
export type CompiledThemeCss = {
  cssText: string;
  /** `cssText` UTF-8 内容的 SHA-256。The SHA-256 of the `cssText` UTF-8 content. */
  styleId: string;
  byteLength: number;
  /** 读取过的全部文件。Every file that was read. */
  dependencies: ThemeDependency[];
};

/** 一个 `@import` 及其目标。One `@import` and its target. */
type ImportEdge = {
  /** postcss-import 读到的地址。The address postcss-import reads. */
  id: string;
  target: string;
  targetPackagePath: string;
  location: ThemeSourceLocation;
};

type SourceFile = {
  path: string;
  packagePath: string;
  text: string;
  imports: ImportEdge[];
  hasNamespace: boolean;
};

/**
 * 解析 `@import` 的地址：只能是包内 `.css` 文件，不含 escape、片段与查询。
 * Parses an `@import` address: only package `.css` files, without escapes, fragments or queries.
 */
function parseImport(rule: AtRule, file: SourceFile, context: ThemeCompileContext): ImportEdge {
  const location = nodeLocation(rule, file.packagePath);
  if (rule.nodes) {
    throw new ThemeCompileError("THEME_IMPORT_INVALID", "@import cannot have a block.", location);
  }
  if (rule.name.toLowerCase() !== "import" || rule.params.includes("\\")) {
    throw new ThemeCompileError(
      "THEME_IMPORT_INVALID",
      "@import rules cannot contain CSS escapes.",
      location,
    );
  }
  const prelude = parseCssTree(
    rule.params,
    { context: "atrulePrelude", atrule: "import" },
    `"@import ${rule.params}"`,
    location,
  );
  const first =
    "children" in prelude && prelude.children
      ? prelude.children
          .toArray()
          .find((node) => node.type !== "WhiteSpace" && node.type !== "Comment")
      : undefined;
  if (first?.type !== "String" && first?.type !== "Url") {
    throw new ThemeCompileError(
      "THEME_IMPORT_INVALID",
      `"@import ${rule.params}" must start with a string or url().`,
      location,
    );
  }
  const id = first.value;
  if (/^data:/iu.test(id.trim())) {
    throw new ThemeCompileError(
      "THEME_IMPORT_INVALID",
      "@import cannot load data URLs; import package CSS files.",
      location,
    );
  }
  const reference = resolvePackageReference(id, file.packagePath, location);
  if (reference.fragment !== "" || !reference.packagePath.toLowerCase().endsWith(".css")) {
    throw new ThemeCompileError(
      "THEME_IMPORT_INVALID",
      `"${id}" is not a package .css file.`,
      location,
    );
  }
  return {
    id,
    target: context.pathOf(reference.packagePath),
    targetPackagePath: reference.packagePath,
    location,
  };
}

/**
 * 读取并解析一个 CSS 来源文件，收集顶层 `@import`。`@import` 必须连续位于 `@charset` 与 `@layer` 语句之后、其他规则之前。
 * postcss-import 把文件开头的 `@layer` 语句移到展开结果的最前面，被导入的文件因此不能以 `@layer` 语句开头，否则 layer 顺序改变。
 * Reads and parses one CSS source file and collects its top-level `@import` rules, which must follow `@charset` and
 * `@layer` statements consecutively and precede every other rule. postcss-import moves leading `@layer` statements
 * to the start of the expanded result, so imported files cannot start with them without changing the layer order.
 */
async function loadSource(
  packagePath: string,
  isEntry: boolean,
  context: ThemeCompileContext,
  location: ThemeSourceLocation,
): Promise<SourceFile> {
  const data = await context.readFile(packagePath, MAX_THEME_CSS_FILE_BYTES, location);
  const path = context.pathOf(packagePath);
  const text = decodeUtf8(data, { file: packagePath });
  const root = parseCss(text, path, packagePath);
  const file: SourceFile = { path, packagePath, text, imports: [], hasNamespace: false };
  let seenRule = false;
  let layerAfterImport = false;
  for (const [index, node] of root.nodes.entries()) {
    if (node.type === "comment") continue;
    if (node.type === "atrule") {
      const name = atRuleName(node);
      if (name === "charset") {
        if (index !== 0 || node.params.replace(/["']/gu, "").toLowerCase() !== "utf-8") {
          throw new ThemeCompileError(
            "THEME_PARSE_FAILED",
            '@charset must be the first rule and declare "UTF-8".',
            nodeLocation(node, packagePath),
          );
        }
        continue;
      }
      if (name === "import") {
        if (seenRule || layerAfterImport) {
          throw new ThemeCompileError(
            "THEME_IMPORT_INVALID",
            seenRule
              ? "@import must precede every rule other than @charset and @layer statements."
              : "Move @layer statements that sit between @import rules before the first @import.",
            nodeLocation(node, packagePath),
          );
        }
        file.imports.push(parseImport(node, file, context));
        continue;
      }
      if (name === "layer" && !node.nodes && !seenRule) {
        if (!isEntry) {
          throw new ThemeCompileError(
            "THEME_IMPORT_INVALID",
            "Imported files cannot start with @layer statements, because expanding the import would move them before earlier content. Declare the layer order in the theme entry or use @layer blocks.",
            nodeLocation(node, packagePath),
          );
        }
        if (file.imports.length > 0) layerAfterImport = true;
        continue;
      }
      if (name === "namespace") file.hasNamespace = true;
    }
    seenRule = true;
    layerAfterImport = false;
  }
  root.walkAtRules((rule) => {
    if (rule.parent !== root && atRuleName(rule) === "import") {
      throw new ThemeCompileError(
        "THEME_IMPORT_INVALID",
        "@import can only appear at the top of a stylesheet.",
        nodeLocation(rule, packagePath),
      );
    }
    if (rule.parent !== root && atRuleName(rule) === "namespace") {
      throw new ThemeCompileError(
        "THEME_PARSE_FAILED",
        "@namespace can only appear at the top of a stylesheet.",
        nodeLocation(rule, packagePath),
      );
    }
  });
  return file;
}

/**
 * 从入口建立导入依赖图：检查循环导入、深度、展开次数与展开后的体积估计。返回按引用路径索引的全部来源文件。
 * Builds the import graph from the entry: checks import cycles, depth, expansion count and the expanded size estimate.
 * Returns every source file by referenced path.
 */
async function buildImportGraph(
  entryPath: string,
  context: ThemeCompileContext,
): Promise<{ entry: SourceFile; sources: Map<string, SourceFile> }> {
  const sources = new Map<string, SourceFile>();
  const entry = await loadSource(entryPath, true, context, { file: entryPath });
  sources.set(entry.path, entry);
  const load = async (packagePath: string, location: ThemeSourceLocation) => {
    const path = context.pathOf(packagePath);
    let source = sources.get(path);
    if (!source) {
      source = await loadSource(packagePath, false, context, location);
      sources.set(path, source);
    }
    return source;
  };
  let expansions = 0;
  let expandedBytes = Buffer.byteLength(entry.text);
  const visit = async (file: SourceFile, chain: string[]): Promise<void> => {
    for (const edge of file.imports) {
      if (chain.includes(edge.target)) {
        const names = [...chain, edge.target].map((path) => context.packagePathOf(path));
        throw new ThemeCompileError(
          "THEME_IMPORT_INVALID",
          `Circular import: ${names.join(" → ")}.`,
          edge.location,
        );
      }
      // 链中第 n 个文件发出的导入位于第 n 层。An import made by the n-th file of the chain is at depth n.
      if (chain.length > MAX_THEME_IMPORT_DEPTH) {
        throw sizeLimitError(
          `Importing "${edge.targetPackagePath}" reaches import depth ${chain.length}; the limit is ${MAX_THEME_IMPORT_DEPTH}.`,
          edge.location,
        );
      }
      expansions += 1;
      if (expansions > MAX_THEME_IMPORT_EXPANSIONS) {
        throw sizeLimitError(
          `Importing "${edge.targetPackagePath}" is import expansion ${expansions}; the limit is ${MAX_THEME_IMPORT_EXPANSIONS}.`,
          edge.location,
        );
      }
      const target = await load(edge.targetPackagePath, edge.location);
      expandedBytes += Buffer.byteLength(target.text);
      if (expandedBytes > MAX_THEME_CSS_TEXT_BYTES) {
        throw sizeLimitError(
          `Importing "${edge.targetPackagePath}" expands the theme CSS to more than ${MAX_THEME_CSS_TEXT_BYTES} bytes (16 MiB).`,
          edge.location,
        );
      }
      await visit(target, [...chain, edge.target]);
    }
  };
  await visit(entry, [entry.path]);

  for (const source of sources.values()) {
    if (!source.hasNamespace) continue;
    if (source !== entry || entry.imports.length > 0) {
      throw new ThemeCompileError(
        "THEME_IMPORT_INVALID",
        source === entry
          ? "A theme entry with @namespace cannot use @import; put the namespaced rules in a separate entry without imports."
          : "@namespace can only appear in a theme entry without @import.",
        { file: source.packagePath },
      );
    }
  }
  return { entry, sources };
}

/**
 * 用 postcss-import 展开已校验的导入：解析与读取只使用依赖图中的结果；重复导入保持来源顺序与条件。
 * Expands the validated imports with postcss-import: resolution and loading use only the import graph; repeated
 * imports keep their source order and conditions.
 */
async function expandImports(
  entry: SourceFile,
  sources: ReadonlyMap<string, SourceFile>,
  context: ThemeCompileContext,
): Promise<Root> {
  const targets = new Map<string, string>();
  for (const source of sources.values()) {
    for (const edge of source.imports)
      targets.set(`${dirname(source.path)}\u0000${edge.id}`, edge.target);
  }
  // @types/postcss-import 缺少 17.x 的 `warnOnEmpty`；空文件是合法的导入。
  // @types/postcss-import lacks the 17.x `warnOnEmpty`; empty files are valid imports.
  const options: Parameters<typeof postcssImport>[0] & { warnOnEmpty: boolean } = {
    skipDuplicates: false,
    warnOnEmpty: false,
    resolve: (id, basedir) => {
      const target = targets.get(`${basedir}\u0000${id}`);
      if (target === undefined) {
        throw new ThemeCompileError(
          "THEME_IMPORT_INVALID",
          `"${id}" was not resolved while the imports were checked.`,
          { file: entry.packagePath },
        );
      }
      return target;
    },
    load: (filename) => {
      const source = sources.get(filename);
      if (!source) throw new Error(`${filename} was not read while the imports were checked.`);
      return source.text;
    },
  };
  const result = await postcss([postcssImport(options)]).process(entry.text, {
    from: entry.path,
  });
  const [warning] = result.warnings();
  if (warning) {
    const file = warning.node?.source?.input.file;
    throw new ThemeCompileError(
      "THEME_IMPORT_INVALID",
      warning.text,
      file
        ? { file: context.packagePathOf(file), line: warning.line, column: warning.column }
        : { file: entry.packagePath },
    );
  }
  return result.root;
}

/**
 * 编译 CSS 主题：展开包内导入，内嵌图片与字体，检查完整结果，计算 `styleId`。失败时抛出 `ThemeCompileError`。
 * Compiles a CSS theme: expands package imports, inlines images and fonts, checks the complete result and computes
 * the `styleId`. Failures throw `ThemeCompileError`.
 */
export async function compileCssTheme(
  rootRealPath: string,
  entryPath: string,
): Promise<CompiledThemeCss> {
  const context = new ThemeCompileContext(rootRealPath);
  const { entry, sources } = await buildImportGraph(entryPath, context);
  const root = await expandImports(entry, sources, context);
  context.growOutput(Buffer.byteLength(root.toString()), { file: entryPath });
  await rewriteStylesheet(root, context, (node) => {
    const input = node.source?.input.file;
    const file = input ? context.packagePathOf(input) : entryPath;
    return { base: file, file };
  });
  const cssText = root.toString();
  const byteLength = Buffer.byteLength(cssText);
  if (byteLength > MAX_THEME_CSS_TEXT_BYTES) {
    throw sizeLimitError(
      `The theme CSS is ${byteLength} bytes; the limit is ${MAX_THEME_CSS_TEXT_BYTES} bytes (16 MiB).`,
      { file: entryPath },
    );
  }
  verifyThemeCssText(cssText);
  return {
    cssText,
    styleId: createHash("sha256").update(cssText, "utf8").digest("hex"),
    byteLength,
    dependencies: [...context.dependencies.values()],
  };
}
