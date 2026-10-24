import type { ExtensionApi } from "@agw/extension-sdk";
import { z } from "zod";

import { errorCode } from "./errors";

const MIN_FONT_SIZE = 12;
const MAX_FONT_SIZE = 28;
const SETTINGS_FILE_NAME = "settings.json";
// Code 的字号默认取文档字号的 0.875 倍。By default the code size is 0.875 times the document size.
const CODE_TO_DOCUMENT_RATIO = 0.875;

/** 可选的字号（px），12 到 28 的整数。The selectable sizes in px: the integers 12 to 28. */
export const TEXT_EDITOR_FONT_SIZES: readonly number[] = Array.from(
  { length: MAX_FONT_SIZE - MIN_FONT_SIZE + 1 },
  (_, index) => MIN_FONT_SIZE + index,
);

const fontSizeSchema = z.number().int().min(MIN_FONT_SIZE).max(MAX_FONT_SIZE).nullable();

const settingsSchema = z.strictObject({
  schemaVersion: z.literal(1),
  documentFontSize: fontSizeSchema,
  codeFontSize: fontSizeSchema,
});

/** `null` 表示跟随默认值。`null` follows the default. */
export type TextEditorSettings = z.infer<typeof settingsSchema>;

export const DEFAULT_TEXT_EDITOR_SETTINGS: TextEditorSettings = {
  schemaVersion: 1,
  documentFontSize: null,
  codeFontSize: null,
};

/** 设置文件无法解析或校验失败。The settings file cannot be parsed or fails validation. */
export class TextEditorSettingsError extends Error {
  public override readonly name = "TextEditorSettingsError";
}

function settingsUri(api: ExtensionApi): string {
  const storageUri = api.context.get().extension.storageUri;
  return `${storageUri.replace(/\/+$/, "")}/${SETTINGS_FILE_NAME}`;
}

function validate(value: unknown): TextEditorSettings {
  const result = settingsSchema.safeParse(value);
  if (!result.success) {
    throw new TextEditorSettingsError(
      `${SETTINGS_FILE_NAME} is invalid: ${z.prettifyError(result.error)}`,
    );
  }
  return result.data;
}

/**
 * 读取设置文件；文件不存在时返回默认值，内容不合法时抛出 TextEditorSettingsError，其他读取错误原样抛出。
 * Reads the settings file: defaults when it does not exist, TextEditorSettingsError when the content is invalid,
 * any other read error is rethrown.
 */
export async function readTextEditorSettings(api: ExtensionApi): Promise<TextEditorSettings> {
  let text: string;
  try {
    text = await api.fs.readTextFile(settingsUri(api));
  } catch (error) {
    if (errorCode(error) === "ENOENT") return { ...DEFAULT_TEXT_EDITOR_SETTINGS };
    throw error;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    throw new TextEditorSettingsError(
      `${SETTINGS_FILE_NAME} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`,
    );
  }
  return validate(parsed);
}

/**
 * 校验后写入设置文件；校验失败时不发出写入请求。
 * Validates and then writes the settings file; no write request is made when validation fails.
 */
export async function writeTextEditorSettings(
  api: ExtensionApi,
  settings: TextEditorSettings,
): Promise<void> {
  const valid = validate(settings);
  await api.fs.writeFile(
    settingsUri(api),
    new TextEncoder().encode(JSON.stringify(valid, null, 2)),
  );
}

function pixels(tokens: Record<string, string>, name: string): number {
  const value = tokens[name];
  if (value === undefined) throw new Error(`The host theme has no ${name}.`);
  const match = /^(\d+(?:\.\d+)?)px$/.exec(value.trim());
  if (match === null) throw new Error(`The host theme ${name} is not a px value: "${value}".`);
  return Number(match[1]);
}

/**
 * 实际使用的字号（px）：文档字号为设置值，未设置时取基础字号；代码字号为设置值，未设置时取文档字号 × 0.875 并取整到
 * 0.5px。主题变量缺少基础字号或不是 px 值时抛出。
 * The effective sizes in px: the document size is the setting, or the base size when unset; the code size is the
 * setting, or the document size × 0.875 rounded to 0.5px when unset. Throws when the theme has no base size or it is
 * not a px value.
 */
export function resolveTextEditorFontSizes(
  settings: TextEditorSettings,
  tokens: Record<string, string>,
): { document: number; code: number } {
  const base = pixels(tokens, "--agw-font-size-base");
  const document = settings.documentFontSize ?? base;
  const code = settings.codeFontSize ?? Math.round(document * CODE_TO_DOCUMENT_RATIO * 2) / 2;
  return { document, code };
}
