import type { ExtensionApi, ExtensionError, FileViewerFile, ThemeInfo } from "@agw/extension-sdk";
import {
  Button,
  Excalidraw,
  isElementLink,
  MainMenu,
  restore,
  serializeAsJSON,
} from "@excalidraw/excalidraw";
import type {
  AppState,
  ExcalidrawImperativeAPI,
  ExcalidrawProps,
  UIOptions,
} from "@excalidraw/excalidraw/types";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";

import { errorMessage } from "./error-message";
import { readSaveShortcut } from "./save-shortcut";
import { matchesSnapshot, takeSnapshot, type SceneSnapshot } from "./unsaved-changes";

export type RestoredScene = ReturnType<typeof restore>;

/**
 * 写入文件的 appState 字段（背景颜色、网格设置）的序列化结果，由 Excalidraw 自己挑选字段。
 * The serialized appState fields written to the file (background color, grid settings), as Excalidraw selects them.
 */
function savedAppState(appState: Partial<AppState>): string {
  return serializeAsJSON([], appState, {}, "local");
}

const EXTERNAL_LINK = /^https?:/i;

// 关闭沙箱中无法工作的入口：打开或另存为其他文件、导出、保存为图片；主题跟随宿主，不提供切换。
// Turns off what cannot work in the sandbox: opening or saving other files, export and save as image. The theme
// follows the host, so the toggle is hidden.
const UI_OPTIONS: Partial<UIOptions> = {
  canvasActions: {
    loadScene: false,
    saveToActiveFile: false,
    export: false,
    saveAsImage: false,
    toggleTheme: false,
  },
};

export interface EditorProps {
  api: ExtensionApi;
  file: FileViewerFile;
  scene: RestoredScene;
  theme: ThemeInfo["kind"];
  reportUnsaved: (unsaved: boolean) => void;
}

export function Editor({ api, file, scene, theme, reportUnsaved }: EditorProps) {
  const excalidrawRef = useRef<ExcalidrawImperativeAPI | null>(null);
  const [loaded] = useState(() =>
    takeSnapshot(scene.elements, scene.files, savedAppState(scene.appState)),
  );
  const savedRef = useRef<SceneSnapshot>(loaded);
  const saveQueue = useRef(Promise.resolve());
  const disposedRef = useRef(false);
  const [saving, setSaving] = useState(false);
  const initialData = useMemo(() => ({ ...scene, scrollToContent: true }), [scene]);

  useEffect(() => {
    disposedRef.current = false;
    return () => {
      disposedRef.current = true;
    };
  }, []);

  const onChange = useCallback<NonNullable<ExcalidrawProps["onChange"]>>(
    (elements, appState, files) =>
      reportUnsaved(!matchesSnapshot(savedRef.current, elements, files, savedAppState(appState))),
    [reportUnsaved],
  );

  // 页面不能打开新窗口：元素链接在画布内跳转，http(s) 链接由宿主确认后用浏览器打开，其他链接不响应。
  // The page cannot open windows: element links jump within the canvas, http(s) links open in the browser after the
  // host confirms, and other links do nothing.
  const onLinkOpen = useCallback<NonNullable<ExcalidrawProps["onLinkOpen"]>>(
    (element, event) => {
      event.preventDefault();
      const link = element.link;
      if (!link) return;
      if (isElementLink(link)) {
        excalidrawRef.current?.scrollToContent(link, { fitToContent: true, animate: true });
      } else if (EXTERNAL_LINK.test(link)) {
        api.ui.openExternal(link).catch((error: unknown) => {
          if ((error as Partial<ExtensionError>).code === "ERR_CANCELED") return;
          api.ui.notify({ type: "error", message: errorMessage(error) });
        });
      }
    },
    [api],
  );

  const saveOnce = useCallback(async () => {
    const excalidraw = excalidrawRef.current;
    if (!excalidraw) throw new Error("The Excalidraw API is not ready.");
    setSaving(true);
    let saved: SceneSnapshot;
    try {
      const elements = excalidraw.getSceneElementsIncludingDeleted();
      const files = excalidraw.getFiles();
      const appState = excalidraw.getAppState();
      saved = takeSnapshot(elements, files, savedAppState(appState));
      const content = serializeAsJSON(elements, appState, files, "local");
      await api.fs.writeFile(file.uri, content, { overwrite: true });
    } catch (error) {
      api.ui.notify({
        type: "error",
        message: `Could not save ${file.name}: ${errorMessage(error)}`,
      });
      return;
    } finally {
      setSaving(false);
    }
    // 卸载后报告属于新文件，不再更新。After unmount the reporter belongs to the next file.
    if (disposedRef.current) return;
    savedRef.current = saved;
    // 写入期间可能继续编辑，与保存的快照比较。Editing may continue during the write, so compare with the saved snapshot.
    reportUnsaved(
      !matchesSnapshot(
        saved,
        excalidraw.getSceneElementsIncludingDeleted(),
        excalidraw.getFiles(),
        savedAppState(excalidraw.getAppState()),
      ),
    );
  }, [api, file, reportUnsaved]);

  // 按顺序保存，先发起的写入不会覆盖后发起的。Saves run in order, so an earlier write never overwrites a later one.
  const save = useCallback(() => {
    saveQueue.current = saveQueue.current.then(saveOnce);
  }, [saveOnce]);

  useEffect(() => {
    const platform = api.context.get().app.platform;
    const onKeyDown = (event: KeyboardEvent) => {
      const shortcut = readSaveShortcut(event, platform);
      if (!shortcut) return;
      // 捕获阶段先于 Excalidraw 处理；另存为在沙箱中关闭。Runs before Excalidraw in the capture phase; save-as is off in the sandbox.
      event.preventDefault();
      event.stopPropagation();
      if (shortcut === "save") save();
    };
    window.addEventListener("keydown", onKeyDown, { capture: true });
    return () => window.removeEventListener("keydown", onKeyDown, { capture: true });
  }, [api, save]);

  const setExcalidrawApi = useCallback((excalidraw: ExcalidrawImperativeAPI) => {
    excalidrawRef.current = excalidraw;
  }, []);

  const renderTopRightUI = useCallback(
    () => (
      <Button className="save-button" onSelect={save} disabled={saving}>
        {saving ? "Saving…" : "Save"}
      </Button>
    ),
    [save, saving],
  );

  return (
    <div className="editor">
      <Excalidraw
        excalidrawAPI={setExcalidrawApi}
        initialData={initialData}
        onChange={onChange}
        onLinkOpen={onLinkOpen}
        theme={theme}
        UIOptions={UI_OPTIONS}
        renderTopRightUI={renderTopRightUI}
        aiEnabled={false}
        // 页面中只有编辑器，快捷键绑定到 document。The page holds only the editor, so shortcuts bind to the document.
        handleKeyboardGlobally
      >
        {/* 去掉 Excalidraw 的外部链接、打开、保存与导出菜单项。Leaves out Excalidraw's external links, open, save and export items. */}
        <MainMenu>
          <MainMenu.DefaultItems.SearchMenu />
          <MainMenu.DefaultItems.Help />
          <MainMenu.DefaultItems.ClearCanvas />
          <MainMenu.Separator />
          <MainMenu.DefaultItems.ChangeCanvasBackground />
        </MainMenu>
      </Excalidraw>
    </div>
  );
}
