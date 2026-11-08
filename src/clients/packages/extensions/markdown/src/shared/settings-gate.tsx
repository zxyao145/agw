import type { ExtensionApi } from "@agw/extension-sdk";
import type { Root } from "react-dom/client";

import { SettingsError } from "./document-error";
import { notifyFailure } from "./errors";
import { readTextEditorSettings, type TextEditorSettings } from "./text-editor-settings";

/** 查看器打开后的内容。What a viewer has after opening. */
export type OpenedView = {
  /** 显示（或刷新）宿主给出的文件。Shows (or refreshes) the file the host gives. */
  show(params: unknown): void | Promise<void>;
  /** 隐藏时清除当前文件的外部 Context。Clears the current file's external context when hidden. */
  hide?(): void | Promise<void>;
  /** 卸载界面并释放文档会话与监听器。Unmounts the UI and releases the document session and listeners. */
  dispose(): void | Promise<void>;
};

type Loaded = {
  api: ExtensionApi;
  unloaded: boolean;
  hidden: boolean;
  opening: boolean;
  visibilityRevision: number;
  disposeTask: Promise<void> | null;
  opened: OpenedView | null;
  ready: Promise<void>;
};

/**
 * 三个查看器共用的生命周期：`onLoad` 先读取 Markdown extension 的设置，成功后调用 `open` 创建文档会话并渲染；读取失败（文件不合法
 * 或其他读取错误）时在 `root` 显示错误面板，不调用 `open`。读取结束时视图已经卸载则什么都不创建。`onShow` 等 `onLoad`
 * 完成后再显示文件，设置读取失败或视图已卸载时忽略。`onUnload` 释放 `open` 返回的内容；没有打开过时只卸载 `root`。
 * The lifecycle shared by the three viewers: `onLoad` reads the Markdown extension settings first and, on success, calls
 * `open` to create the document session and render; when the read fails (invalid file or any other read error) the
 * error panel is shown in `root` and `open` is not called. Nothing is created when the view was unloaded by the time
 * the read ends. `onShow` waits for `onLoad` before showing the file and is ignored when the settings read failed or
 * the view was unloaded. `onUnload` releases what `open` returned, or only unmounts `root` when nothing was opened.
 */
export function createSettingsGate(
  root: Root,
  open: (
    api: ExtensionApi,
    settings: TextEditorSettings,
    params: unknown,
  ) => OpenedView | Promise<OpenedView>,
) {
  let loaded: Loaded | null = null;

  function dispose(current: Loaded): Promise<void> {
    if (current.disposeTask) return current.disposeTask;
    current.disposeTask = (async () => {
      if (current.opened) await current.opened.dispose();
      else root.unmount();
    })();
    return current.disposeTask;
  }

  async function load(current: Loaded, params: unknown): Promise<void> {
    let settings: TextEditorSettings;
    try {
      settings = await readTextEditorSettings(current.api);
    } catch (error) {
      if (current.unloaded) return;
      root.render(
        <SettingsError
          error={error instanceof Error ? error : new Error(String(error))}
          hint="Open Markdown settings to reset them."
        />,
      );
      return;
    }
    if (current.unloaded) return;
    current.opening = true;
    let opened: OpenedView;
    try {
      opened = await open(current.api, settings, params);
    } finally {
      current.opening = false;
    }
    current.opened = opened;
    if (current.unloaded) {
      await dispose(current);
      return;
    }
    if (current.hidden && opened.hide) notifyFailure(current.api, Promise.resolve(opened.hide()));
  }

  return {
    onLoad(api: ExtensionApi, params: unknown): Promise<void> {
      const current: Loaded = {
        api,
        unloaded: false,
        hidden: false,
        opening: false,
        visibilityRevision: 0,
        disposeTask: null,
        opened: null,
        ready: Promise.resolve(),
      };
      loaded = current;
      current.ready = load(current, params);
      return current.ready;
    },
    onShow(params: unknown): void {
      const current = loaded;
      if (!current) throw new Error("The view received a file before onLoad.");
      current.hidden = false;
      const visibilityRevision = ++current.visibilityRevision;
      notifyFailure(
        current.api,
        current.ready.then(async () => {
          if (
            current.opened &&
            !current.unloaded &&
            !current.hidden &&
            current.visibilityRevision === visibilityRevision
          ) {
            await current.opened.show(params);
          }
        }),
      );
    },
    onHide(): void {
      const current = loaded;
      if (!current || current.unloaded) return;
      current.hidden = true;
      current.visibilityRevision += 1;
      if (current.opened?.hide) notifyFailure(current.api, Promise.resolve(current.opened.hide()));
    },
    async onUnload(): Promise<void> {
      const current = loaded;
      loaded = null;
      if (!current) {
        root.unmount();
        return;
      }
      current.unloaded = true;
      current.visibilityRevision += 1;
      if (current.opening) {
        try {
          await current.ready;
        } catch (error) {
          await dispose(current);
          throw error;
        }
      }
      await dispose(current);
    },
  };
}
