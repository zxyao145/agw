import type { TextDocumentAdapter } from "@agw/extension-sdk";
import { CaptureUpdateAction, restore, serializeAsJSON } from "@excalidraw/excalidraw";
import type { AppState, ExcalidrawImperativeAPI } from "@excalidraw/excalidraw/types";
import { flushSync } from "react-dom";

import { parseSceneFile } from "./scene-file";
import { matchesSnapshot, takeSnapshot, type SceneSnapshot } from "./unsaved-changes";

/** 保存时写入的内容与用于对比的场景快照。The content to write on save and the scene snapshot used for comparison. */
export interface SceneDocumentSnapshot {
  readonly scene: SceneSnapshot;
  readonly text: string;
}

export interface SceneDocument {
  readonly adapter: TextDocumentAdapter<SceneDocumentSnapshot>;
  /** 接入 Excalidraw 的 `onChange`；场景变化时 `version()` 加 1，并返回 true。Wire to Excalidraw's `onChange`; bumps `version()` and returns true when the scene changed. */
  onChange(
    elements: ReturnType<ExcalidrawImperativeAPI["getSceneElementsIncludingDeleted"]>,
    appState: AppState,
    files: ReturnType<ExcalidrawImperativeAPI["getFiles"]>,
  ): boolean;
}

/**
 * 写入文件的 appState 字段（背景颜色、网格设置）的序列化结果，由 Excalidraw 自己挑选字段。
 * The serialized appState fields written to the file (background color, grid settings), as Excalidraw selects them.
 */
function savedAppState(appState: Partial<AppState>): string {
  return serializeAsJSON([], appState, {}, "local");
}

type Elements = ReturnType<ExcalidrawImperativeAPI["getSceneElementsIncludingDeleted"]>;
type Files = ReturnType<ExcalidrawImperativeAPI["getFiles"]>;

/**
 * 只保留未删除图片元素引用的文件，与写入文件的内容一致。Excalidraw 的 `addFiles` 只增不减，
 * 重新读取后上一个场景的文件仍留在内存中，不参与对比。
 * Keeps the files referenced by non-deleted image elements, which is what gets written to the file. Excalidraw's
 * `addFiles` only adds, so after a reload the previous scene's files stay in memory and must not take part in comparisons.
 */
function usedFiles(elements: Elements, files: Files): Files {
  const used: Files = {};
  for (const element of elements) {
    if (element.isDeleted || !("fileId" in element) || !element.fileId) continue;
    const file = files[element.fileId];
    if (file) used[element.fileId] = file;
  }
  return used;
}

export function createSceneDocument(excalidraw: ExcalidrawImperativeAPI): SceneDocument {
  let version = 0;
  let loading = false;
  // 上一次 onChange 看到的场景；第一次 onChange 只建立起点。The scene the last onChange saw; the first onChange only sets the starting point.
  let seen: SceneSnapshot | null = null;

  const currentSnapshot = (): SceneSnapshot => {
    const elements = excalidraw.getSceneElementsIncludingDeleted();
    return takeSnapshot(
      elements,
      usedFiles(elements, excalidraw.getFiles()),
      savedAppState(excalidraw.getAppState()),
    );
  };

  const adapter: TextDocumentAdapter<SceneDocumentSnapshot> = {
    load({ text }) {
      // 空文件（例如在 Files 中新建的文件）作为空白画布打开。An empty file (such as one just created in Files) opens as a blank canvas.
      const data = text.trim() === "" ? null : parseSceneFile(text);
      // 与 Excalidraw 自己打开文件时的选项相同。The options Excalidraw itself uses when opening a file.
      const scene = restore(data, null, null, { repairBindings: true, refreshDimensions: false });
      // 同步应用，使紧随其后的 snapshot 读到新场景的 appState。Applied synchronously so the snapshot taken right after reads the new scene's appState.
      // 读取本身不增加版本：加载期间的 onChange 只更新起点。Loading does not bump the version: onChange during the load only updates the starting point.
      loading = true;
      try {
        flushSync(() => {
          excalidraw.updateScene({
            elements: scene.elements,
            // 主题跟随宿主。The theme follows the host.
            appState: { ...scene.appState, theme: excalidraw.getAppState().theme },
            captureUpdate: CaptureUpdateAction.NEVER,
          });
          excalidraw.addFiles(Object.values(scene.files));
          excalidraw.scrollToContent(scene.elements);
        });
        seen = currentSnapshot();
      } finally {
        loading = false;
      }
      excalidraw.history.clear();
    },
    snapshot: () => ({
      scene: currentSnapshot(),
      text: serializeAsJSON(
        excalidraw.getSceneElementsIncludingDeleted(),
        excalidraw.getAppState(),
        excalidraw.getFiles(),
        "local",
      ),
    }),
    serialize: (snapshot) => snapshot.text,
    matches: (snapshot) => {
      const elements = excalidraw.getSceneElementsIncludingDeleted();
      return matchesSnapshot(
        snapshot.scene,
        elements,
        usedFiles(elements, excalidraw.getFiles()),
        savedAppState(excalidraw.getAppState()),
      );
    },
    version: () => version,
  };

  return {
    adapter,
    onChange(elements, appState, files) {
      const used = usedFiles(elements, files);
      const current = takeSnapshot(elements, used, savedAppState(appState));
      const previous = seen;
      seen = current;
      if (loading || previous === null) return false;
      if (matchesSnapshot(previous, elements, used, current.savedAppState)) return false;
      version += 1;
      return true;
    },
  };
}
