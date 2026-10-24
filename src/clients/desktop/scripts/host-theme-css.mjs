// 宿主 CSS 的主题变量工具：编译 renderer 的 globals.css，检查推送给 extension 的变量名称，提取根元素自定义属性规则作为
// extension 的默认变量样式表（/__agw/theme-defaults.css）。
// Theme variable tools for the host CSS: compiles the renderer's globals.css, checks the variable names pushed to
// extensions, and extracts the root custom property rules as the extensions' default variable stylesheet
// (/__agw/theme-defaults.css).
import { readdir, readFile } from "node:fs/promises";
import { join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { parse as parseCssTree, walk } from "css-tree";
import { build } from "esbuild";
import postcss from "postcss";

const desktopDirectory = resolve(fileURLToPath(new URL("..", import.meta.url)));
const rendererDirectory = join(desktopDirectory, "renderer");
const staticDirectory = join(rendererDirectory, "out", "_next", "static");

/** 推送给 extension 的变量名称。The variable names pushed to extensions. */
export async function loadThemeTokenNames() {
  const moduleFile = join(desktopDirectory, ".scratch", "check-theme-tokens", "theme-tokens.mjs");
  await build({
    entryPoints: [join(desktopDirectory, "src", "shared", "theme-tokens.ts")],
    outfile: moduleFile,
    bundle: true,
    format: "esm",
    platform: "node",
    target: "node24",
    logLevel: "warning",
  });
  return (await import(`${pathToFileURL(moduleFile).href}?t=${Date.now()}`)).THEME_TOKEN_NAMES;
}

/** Next 构建产出的宿主 CSS。The host CSS produced by the Next build. */
export async function readBuiltHostCss() {
  const files = (await readdir(staticDirectory, { recursive: true, withFileTypes: true }))
    .filter((entry) => entry.isFile() && entry.name.endsWith(".css"))
    .map((entry) => join(entry.parentPath, entry.name));
  if (files.length === 0) throw new Error(`No built CSS was found under ${staticDirectory}.`);
  return Promise.all(
    files.map(async (file) => ({
      file,
      root: postcss.parse(await readFile(file, "utf8"), { from: file }),
    })),
  );
}

/**
 * 用 renderer 的 PostCSS 配置编译 globals.css，与 Next 处理宿主样式的方式相同；Tailwind 从 Desktop 目录扫描来源，与
 * `next build renderer` 的工作目录一致。开发与发布构建都可以运行。
 * Compiles globals.css with the renderer's PostCSS config, as Next processes the host styles; Tailwind scans sources
 * from the Desktop directory, the working directory of `next build renderer`. Runs in development and release builds.
 */
export async function compileHostCss() {
  const { default: config } = await import(
    pathToFileURL(join(rendererDirectory, "postcss.config.mjs")).href
  );
  const plugins = await Promise.all(
    Object.entries(config.plugins).map(async ([name, options]) => {
      const { default: plugin } = await import(name);
      return plugin({ base: desktopDirectory, ...options });
    }),
  );
  const file = join(rendererDirectory, "src", "app", "globals.css");
  const result = await postcss(plugins).process(await readFile(file, "utf8"), { from: file });
  return [{ file, root: result.root }];
}

/** 样式表中声明的全部自定义属性。Every custom property declared by the stylesheets. */
export function definedCustomProperties(stylesheets) {
  const defined = new Set();
  for (const { root } of stylesheets) {
    root.walkDecls((declaration) => {
      if (declaration.prop.startsWith("--")) defined.add(declaration.prop);
    });
  }
  return defined;
}

/** 名称检查：推送给 extension 的变量都必须有定义。The name check: every variable pushed to extensions must be defined. */
export function checkThemeTokenNames(stylesheets, names, label) {
  const defined = definedCustomProperties(stylesheets);
  const missing = names.filter((name) => !defined.has(name));
  if (missing.length > 0) {
    throw new Error(`${label} does not define these theme variables: ${missing.join(", ")}`);
  }
}

// 根元素选择器中可以出现的简单选择器。Simple selectors allowed in a root element selector.
const ROOT_ANCHORS = new Set(["root", "host"]);

/**
 * 只匹配根元素的复合选择器：含有 `:root`、`:host`、`html` 或 Mode 的 `.dark`，其余部分只有属性选择器、`.dark` 与
 * `:not(...)` 这样的条件，没有组合符。
 * A compound selector matching only the root element: it holds `:root`, `:host`, `html` or the Mode `.dark`, the rest
 * being conditions such as attribute selectors, `.dark` and `:not(...)`, with no combinators.
 */
function isRootSelector(selector) {
  const ast = parseCssTree(selector, { context: "selector" });
  let anchored = false;
  const check = (nodes) =>
    nodes.every((node) => {
      if (node.type === "PseudoClassSelector" && ROOT_ANCHORS.has(node.name) && !node.children) {
        anchored = true;
        return true;
      }
      if (node.type === "TypeSelector" && node.name === "html") {
        anchored = true;
        return true;
      }
      if (node.type === "ClassSelector" && node.name === "dark") {
        anchored = true;
        return true;
      }
      if (node.type === "AttributeSelector") return true;
      if (node.type === "PseudoClassSelector" && node.name === "not" && node.children) {
        return node.children
          .toArray()
          .every(
            (list) =>
              list.type === "SelectorList" &&
              list.children.toArray().every((item) => check(item.children.toArray())),
          );
      }
      return false;
    });
  return check(ast.children.toArray()) && anchored;
}

/** 声明值中引用的变量与是否有回退值。The variables a value references and whether each has a fallback. */
function variableReferences(value) {
  const references = [];
  walk(parseCssTree(value, { context: "value" }), (node) => {
    if (node.type !== "Function" || node.name.toLowerCase() !== "var") return;
    const [name, ...rest] = node.children.toArray();
    if (name?.type === "Identifier")
      references.push({ name: name.name, fallback: rest.length > 0 });
  });
  return references;
}

/**
 * 提取根元素自定义属性规则：保留选择器、来源顺序、Mode、`@media`、`@supports` 与 cascade layer 上下文，以及 layer 顺序语句与
 * 相关的 `@property`；校验提取结果包含推送给 extension 的变量与全部变量依赖。
 * Extracts the root custom property rules, keeping selectors, source order, modes, `@media`, `@supports` and cascade
 * layer contexts, plus layer order statements and the related `@property` rules; verifies that the result defines
 * the variables pushed to extensions and every variable dependency.
 */
export function extractThemeDefaults(stylesheets, names) {
  const output = postcss.root();
  const extracted = new Set();
  const copyChildren = (source, target) => {
    for (const node of source.nodes ?? []) {
      if (node.type === "atrule") {
        const name = node.name.toLowerCase();
        if (name === "layer" && !node.nodes) {
          target.append(node.clone());
          continue;
        }
        if (name !== "layer" && name !== "media" && name !== "supports") continue;
        const wrapper = node.clone({ nodes: [] });
        copyChildren(node, wrapper);
        if (wrapper.nodes.length > 0) target.append(wrapper);
        continue;
      }
      if (node.type !== "rule") continue;
      const selectors = node.selectors.filter(isRootSelector);
      const declarations = node.nodes.filter(
        (child) => child.type === "decl" && child.prop.startsWith("--"),
      );
      if (selectors.length === 0 || declarations.length === 0) continue;
      const rule = postcss.rule({ selectors });
      for (const declaration of declarations) {
        rule.append(declaration.clone());
        extracted.add(declaration.prop);
      }
      target.append(rule);
    }
  };
  for (const { root } of stylesheets) copyChildren(root, output);
  const properties = [];
  for (const { root } of stylesheets) {
    root.walkAtRules("property", (rule) => {
      if (extracted.has(rule.params.trim())) properties.push(rule.clone());
    });
  }
  if (properties.length > 0) output.prepend(...properties);

  const missing = names.filter((name) => !extracted.has(name));
  if (missing.length > 0) {
    throw new Error(`The extracted theme defaults do not define: ${missing.join(", ")}`);
  }
  const unresolved = new Set();
  output.walkDecls((declaration) => {
    for (const reference of variableReferences(declaration.value)) {
      if (!reference.fallback && !extracted.has(reference.name)) unresolved.add(reference.name);
    }
  });
  if (unresolved.size > 0) {
    throw new Error(
      `The extracted theme defaults refer to variables they do not define: ${[...unresolved].join(", ")}`,
    );
  }
  return output.toString();
}
