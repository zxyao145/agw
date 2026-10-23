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

export const MAX_THEME_VALUE_LENGTH = 200;

const fontFamilyProperty = lexer.getProperty("font-family");
if (!fontFamilyProperty?.syntax) throw new Error("css-tree has no font-family syntax.");
// 属性语法不含 CSS-wide 关键字（`inherit` 等），它们作为自定义属性的值时作用于变量本身。
// The property syntax leaves out CSS-wide keywords (`inherit` and so on), which would apply to the variable itself.
const FONT_FAMILY_SYNTAX = fontFamilyProperty.syntax;

const KIND_LABELS: Record<ThemeTokenKind, string> = {
  color: "color",
  length: "length",
  "font-family": "font-family value",
};

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
  const result =
    kind === "font-family" ? lexer.match(FONT_FAMILY_SYNTAX, ast) : lexer.matchType(kind, ast);
  return result.matched !== null;
}

const themeValueSchema = z.string().min(1).max(MAX_THEME_VALUE_LENGTH);

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
      // 长度不合法的值已经由 themeValueSchema 报告。Values of an invalid length were reported by themeValueSchema.
      themeValueSchema.safeParse(value).success &&
      !isValidThemeValue(kind, value)
    ) {
      context.addIssue({
        code: "custom",
        message: `"${value}" is not a valid CSS ${KIND_LABELS[kind]}.`,
        path: [name],
        params: { code: "INVALID_THEME_VALUE" },
      });
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
 * Reads and validates the theme files of `contributes.themes`. `files` holds the theme files that passed the package
 * path checks (package path → real path); themes missing from it already reported a path error.
 */
export async function readExtensionThemes(
  contributions: readonly ExtensionThemeContribution[],
  files: ReadonlyMap<string, string>,
): Promise<{ themes: ExtensionTheme[]; errors: ExtensionValidationError[] }> {
  const themes: ExtensionTheme[] = [];
  const errors: ExtensionValidationError[] = [];
  for (const [index, contribution] of contributions.entries()) {
    const file = files.get(contribution.path);
    if (file === undefined) continue;
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
      id: contribution.id,
      title: contribution.title,
      common: parsed.data.common ?? {},
      light: parsed.data.light ?? {},
      dark: parsed.data.dark ?? {},
    });
  }
  return { themes, errors };
}
