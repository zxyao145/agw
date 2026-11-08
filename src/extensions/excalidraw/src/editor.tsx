import type {
  DocumentSession,
  ExtensionApi,
  ExtensionError,
  FileViewerFile,
  ThemeInfo,
} from "@agw/extension-sdk";
import { Button, Excalidraw, isElementLink, MainMenu } from "@excalidraw/excalidraw";
import type {
  ExcalidrawImperativeAPI,
  ExcalidrawProps,
  UIOptions,
} from "@excalidraw/excalidraw/types";
import { useCallback, useEffect, useReducer, useRef, useState } from "react";

import { errorMessage, notifyFailure } from "./error-message";
import { blockSaveAsShortcut } from "./save-as-shortcut";
import { createSceneDocument, type SceneDocument } from "./scene-document";

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
  /** 每次 `onShow` 都是新对象，用来触发文档会话的 `show`。A new object on every `onShow`; triggers the document session's `show`. */
  file: FileViewerFile;
  theme: ThemeInfo["kind"];
}

export function Editor({ api, file, theme }: EditorProps) {
  const excalidrawRef = useRef<ExcalidrawImperativeAPI | null>(null);
  const sceneRef = useRef<SceneDocument | null>(null);
  const sessionRef = useRef<DocumentSession | null>(null);
  const fileRef = useRef(file);
  const [excalidraw, setExcalidraw] = useState<ExcalidrawImperativeAPI | null>(null);
  const [session, setSession] = useState<DocumentSession | null>(null);
  const [, rerender] = useReducer((count: number) => count + 1, 0);

  // 同一个文件再次到达（视图从隐藏恢复）或换了文件，都交给文档会话处理。
  // The same file arriving again (the view is shown after being hidden) or a new file is handled by the document session.
  useEffect(() => {
    fileRef.current = file;
    if (sessionRef.current) notifyFailure(api, sessionRef.current.show(file));
  }, [api, file]);

  // Mod+Shift+S 会打开 Excalidraw 的另存为，在捕获阶段先拦下。Mod+Shift+S would open Excalidraw's save-as, so it is
  // stopped first in the capture phase.
  useEffect(() => blockSaveAsShortcut(window, api.context.get().app.platform), [api]);

  // Excalidraw 就绪后才能打开文档会话，会话读取文件后通过适配器写入场景；effect 清理时释放会话。
  // The document session opens once Excalidraw is ready; after reading the file it writes the scene through the adapter. The effect cleanup disposes it.
  useEffect(() => {
    if (!excalidraw) return;
    const scene = createSceneDocument(excalidraw);
    const opened = api.documents.openText({ file: fileRef.current, adapter: scene.adapter });
    const subscription = opened.onDidChange(rerender);
    sceneRef.current = scene;
    sessionRef.current = opened;
    setSession(opened);
    return () => {
      subscription.dispose();
      opened.dispose();
      sceneRef.current = null;
      sessionRef.current = null;
    };
  }, [api, excalidraw]);

  const onChange = useCallback<NonNullable<ExcalidrawProps["onChange"]>>(
    (elements, appState, files) => {
      if (sceneRef.current?.onChange(elements, appState, files)) sessionRef.current?.edited();
    },
    [],
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

  // Excalidraw 在渲染阶段调用这个回调，所以只保存 API，不在这里做其他事。
  // Excalidraw calls this during render, so it only stores the API and does nothing else.
  const setExcalidrawApi = useCallback((instance: ExcalidrawImperativeAPI) => {
    excalidrawRef.current = instance;
    setExcalidraw(instance);
  }, []);

  const state = session?.state ?? "loading";

  const renderTopRightUI = useCallback(
    () => (
      <Button
        className="save-button"
        onSelect={() => {
          if (session) notifyFailure(api, session.save());
        }}
        disabled={!session || state === "loading" || state === "saving" || state === "failed"}
      >
        {state === "saving" ? "Saving…" : "Save"}
      </Button>
    ),
    [api, session, state],
  );

  return (
    <div className="editor">
      <Excalidraw
        excalidrawAPI={setExcalidrawApi}
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
      {state === "loading" && (
        <p className="status overlay">Loading {session?.file.name ?? file.name}…</p>
      )}
      {state === "failed" && session && (
        <div className="status status-error overlay" role="alert">
          <p className="status-title">Cannot open {session.file.name}</p>
          <p>{session.error?.message}</p>
        </div>
      )}
    </div>
  );
}
