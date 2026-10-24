import { readFile } from "node:fs/promises";
// css-tree 的 `lib` 入口用 `createRequire(import.meta.url)` 读取语法数据，打包为 CommonJS 后无法运行；`dist` 构建内含这些数据。
// css-tree's `lib` entry reads its syntax data through `createRequire(import.meta.url)`, which fails once bundled as
// CommonJS; the `dist` build embeds that data.
import { type CssNode, lexer, parse } from "css-tree/dist/csstree.esm";
import { z } from "zod";

import type { ExtensionThemeContribution } from "@agw/extension-sdk";
import type { ExtensionTheme, ExtensionValidationError } from "../../shared/contracts";
import { toValidationErrors } from "../../shared/extension-manifest";
import { THEME_TOKEN_KINDS, type ThemeTokenKind } from "../../shared/theme-tokens";
import { ThemeCompileError } from "./extension-theme-resources";
import type { ThemeStyleCompiler } from "./extension-theme-style-cache";

export const MAX_THEME_VALUE_LENGTH = 200;
/** 渐变背景由多层渐变组成，长度上限单独设置。Gradient backdrops stack several gradients, so they have their own limit. */
export const MAX_THEME_IMAGE_VALUE_LENGTH = 4000;

/** 属性的语法。Gets a property's syntax. */
function propertySyntax(name: string) {
  const property = lexer.getProperty(name);
  if (!property?.syntax) throw new Error(`css-tree has no ${name} syntax.`);
  return property.syntax;
}

// 属性语法不含 CSS-wide 关键字（`inherit` 等），它们作为自定义属性的值时作用于变量本身。
// The property syntax leaves out CSS-wide keywords (`inherit` and so on), which would apply to the variable itself.
const FONT_FAMILY_SYNTAX = propertySyntax("font-family");
const BACKGROUND_IMAGE_SYNTAX = propertySyntax("background-image");

// 背景只接受渐变：图片文件与其他图片函数会让主题加载外部资源，主题文件只是数据。
// Backdrops take gradients only: image files and other image functions would make a theme load resources, and a theme
// file is data only.
const GRADIENT_FUNCTIONS = new Set([
  "linear-gradient",
  "radial-gradient",
  "conic-gradient",
  "repeating-linear-gradient",
  "repeating-radial-gradient",
  "repeating-conic-gradient",
]);

const KIND_LABELS: Record<ThemeTokenKind, string> = {
  color: "color",
  length: "length",
  "font-family": "font-family value",
  image: "gradient image",
};

/** 渐变背景的一层或层之间的逗号。A layer of a gradient backdrop, or the comma between layers. */
function isGradientLayer(node: CssNode): boolean {
  return (
    (node.type === "Function" && GRADIENT_FUNCTIONS.has(node.name.toLowerCase())) ||
    (node.type === "Identifier" && node.name.toLowerCase() === "none") ||
    (node.type === "Operator" && node.value === ",")
  );
}

/**
 * 值是否是该类型的合法 CSS 值；CSS-wide 关键字与 `var()` 不合法。
 * Whether the value is valid CSS of the kind; CSS-wide keywords and `var()` are not.
 */
export function isValidThemeValue(kind: ThemeTokenKind, value: string): boolean {
  let ast: CssNode;
  try {
    ast = parse(value, { context: "value" });
  } catch (error) {
    if (!(error instanceof SyntaxError)) throw error;
    return false;
  }
  if (kind === "image") {
    return (
      ast.type === "Value" &&
      ast.children.toArray().every(isGradientLayer) &&
      lexer.match(BACKGROUND_IMAGE_SYNTAX, ast).matched !== null
    );
  }
  const result =
    kind === "font-family" ? lexer.match(FONT_FAMILY_SYNTAX, ast) : lexer.matchType(kind, ast);
  return result.matched !== null;
}

const themeValueSchema = z.string().min(1);

const themeTokensSchema = z.record(z.string(), themeValueSchema).superRefine((tokens, context) => {
  for (const [name, value] of Object.entries(tokens)) {
    const kind = THEME_TOKEN_KINDS.get(name);
    if (kind === undefined) {
      context.addIssue({
        code: "custom",
        message: `Unknown theme variable "${name}".`,
        path: [name],
        params: { code: "UNKNOWN_THEME_VARIABLE" },
      });
    } else if (
      // 不是非空字符串的值已经由 themeValueSchema 报告。Values that are not non-empty strings were reported by themeValueSchema.
      themeValueSchema.safeParse(value).success
    ) {
      const maxLength = kind === "image" ? MAX_THEME_IMAGE_VALUE_LENGTH : MAX_THEME_VALUE_LENGTH;
      if (value.length > maxLength) {
        context.addIssue({
          code: "custom",
          message: `Too big: expected string to have <=${maxLength} characters`,
          path: [name],
        });
      } else if (!isValidThemeValue(kind, value)) {
        context.addIssue({
          code: "custom",
          message: `"${value}" is not a valid CSS ${KIND_LABELS[kind]}.`,
          path: [name],
          params: { code: "INVALID_THEME_VALUE" },
        });
      }
    }
  }
});

export const themeFileSchema = z
  .strictObject({
    common: themeTokensSchema.optional(),
    light: themeTokensSchema.optional(),
    dark: themeTokensSchema.optional(),
  })
  .superRefine((file, context) => {
    const sides = [file.common, file.light, file.dark];
    if (sides.every((tokens) => tokens === undefined || Object.keys(tokens).length === 0)) {
      context.addIssue({
        code: "custom",
        message: "A theme must set at least one variable in common, light or dark.",
        path: [],
        params: { code: "THEME_EMPTY" },
      });
    }
  });

/**
 * 读取并校验 `contributes.themes` 的主题文件。`files` 为通过包内路径检查的主题文件：包路径 → 真实路径；不在其中的主题已经报告了路径错误。
 * JSON 入口按变量清单校验；CSS 入口由 `compiler` 展开包内依赖、内嵌资源并检查，结果只保留描述。
 * Reads and validates the theme files of `contributes.themes`. `files` holds the theme files that passed the package
 * path checks (package path → real path); themes missing from it already reported a path error. JSON entries are
 * validated against the variable list; `compiler` expands, inlines and checks CSS entries, and only their descriptors
 * are kept.
 */
export async function readExtensionThemes(
  contributions: readonly ExtensionThemeContribution[],
  files: ReadonlyMap<string, string>,
  rootDirectory: string,
  compiler: ThemeStyleCompiler,
): Promise<{ themes: ExtensionTheme[]; errors: ExtensionValidationError[] }> {
  const themes: ExtensionTheme[] = [];
  const errors: ExtensionValidationError[] = [];
  for (const [index, contribution] of contributions.entries()) {
    const file = files.get(contribution.path);
    if (file === undefined) continue;
    if (contribution.path.endsWith(".css")) {
      try {
        const style = await compiler.compile(rootDirectory, contribution.path);
        themes.push({
          format: "css",
          id: contribution.id,
          title: contribution.title,
          styleId: style.styleId,
          byteLength: style.byteLength,
        });
      } catch (error) {
        if (!(error instanceof ThemeCompileError)) throw error;
        errors.push({
          code: error.code,
          message: error.message,
          path: `contributes.themes[${index}].path`,
        });
      }
      continue;
    }
    let raw: unknown;
    try {
      raw = JSON.parse(await readFile(file, "utf8"));
    } catch (error) {
      if (!(error instanceof SyntaxError)) throw error;
      errors.push({
        code: "THEME_PARSE_FAILED",
        message: `"${contribution.path}" is not valid JSON: ${error.message}`,
        path: `contributes.themes[${index}].path`,
      });
      continue;
    }
    const parsed = themeFileSchema.safeParse(raw);
    if (!parsed.success) {
      errors.push(...toValidationErrors(parsed.error.issues, ["contributes", "themes", index]));
      continue;
    }
    themes.push({
      format: "json",
      id: contribution.id,
      title: contribution.title,
      common: parsed.data.common ?? {},
      light: parsed.data.light ?? {},
      dark: parsed.data.dark ?? {},
    });
  }
  return { themes, errors };
}
