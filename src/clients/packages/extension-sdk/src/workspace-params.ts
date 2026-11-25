import type { OpenMainViewOptions } from "./api";
import { createExtensionError } from "./rpc";

export type JsonValue =
  | null
  | string
  | boolean
  | number
  | JsonValue[]
  | { [key: string]: JsonValue };
export const MAX_EXTENSION_TAB_KEY_LENGTH = 256;
export const MAX_EXTENSION_TAB_TITLE_LENGTH = 100;
export const MAX_EXTENSION_PARAMS_BYTES = 65_536;

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== "object" || value === null) return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

/** 在 MessagePort 两端检查 JSON 参数。Checks JSON parameters on both sides of the MessagePort. */
export function isWorkspaceJsonValue(
  value: unknown,
  ancestors = new Set<object>(),
): value is JsonValue {
  if (value === null || typeof value === "boolean" || typeof value === "string") return true;
  if (typeof value === "number") return Number.isFinite(value);
  if (typeof value !== "object" || ancestors.has(value)) return false;
  if (Object.getOwnPropertySymbols(value).length) return false;
  const array = Array.isArray(value);
  if (!array && !isPlainObject(value)) return false;
  ancestors.add(value);
  const valid = (array ? Array.from(value) : Object.values(value)).every((child) =>
    isWorkspaceJsonValue(child, ancestors),
  );
  ancestors.delete(value);
  return valid;
}

export function workspaceOpenOptions(value: unknown, kind: "view" | "file"): OpenMainViewOptions {
  const invalid = (reason: string) => createExtensionError("ERR_INVALID_ARGUMENT", reason);
  if (!isPlainObject(value)) throw invalid("options must be a plain object.");
  const allowed = kind === "view" ? ["preview", "key", "title"] : ["preview"];
  const unexpected = Reflect.ownKeys(value).some(
    (key) => typeof key !== "string" || !allowed.includes(key),
  );
  if (unexpected) throw invalid("Unknown workspace option.");
  const options: OpenMainViewOptions = {};
  if (value.preview !== undefined) {
    if (typeof value.preview !== "boolean") throw invalid("preview must be a boolean.");
    options.preview = value.preview;
  }
  for (const [field, limit] of [
    ["key", MAX_EXTENSION_TAB_KEY_LENGTH],
    ["title", MAX_EXTENSION_TAB_TITLE_LENGTH],
  ] as const) {
    const text = value[field];
    if (text === undefined) continue;
    const valid = typeof text === "string" && text.length >= 1 && text.length <= limit;
    if (!valid) throw invalid(`${field} must have 1 to ${limit} characters.`);
    options[field] = text;
  }
  return options;
}
