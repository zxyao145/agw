import type { ImportedDataState } from "@excalidraw/excalidraw/data/types";

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/**
 * 解析 `.excalidraw` 文件内容并检查结构（与 Excalidraw 打开文件时的检查相同）；元素内容由 `restore` 修正。
 * Parses `.excalidraw` content and checks its shape as Excalidraw does when opening a file; `restore` sanitizes the elements.
 */
export function parseSceneFile(text: string): ImportedDataState {
  let data: unknown;
  try {
    data = JSON.parse(text);
  } catch (error) {
    throw new Error(`The file is not valid JSON: ${(error as Error).message}`, { cause: error });
  }
  if (!isObject(data) || data.type !== "excalidraw") {
    throw new Error('The file is not an Excalidraw scene (expected "type": "excalidraw").');
  }
  if (data.elements != null && !Array.isArray(data.elements)) {
    throw new Error('The scene "elements" field is not an array.');
  }
  if (data.appState != null && !isObject(data.appState)) {
    throw new Error('The scene "appState" field is not an object.');
  }
  if (data.files != null && !isObject(data.files)) {
    throw new Error('The scene "files" field is not an object.');
  }
  return data as ImportedDataState;
}
