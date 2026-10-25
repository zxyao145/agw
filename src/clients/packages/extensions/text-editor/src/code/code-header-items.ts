import type { DocumentSession, ExtensionApi, FileHeaderItem } from "@agw/extension-sdk";
import { useEffect, useLayoutEffect, useRef } from "react";

import { useSessionValue } from "../shared/editor-chrome";
import { notifyFailure } from "../shared/errors";

/** Code 条目的状态。The state the Code items show. */
export type CodeHeaderState = {
  /** 编辑器正在显示文档（没有在读取中或失败）。The editor shows the document (not loading or failed). */
  shown: boolean;
  canFormat: boolean;
  wordWrap: boolean;
};

/** Code 条目被选择时执行的操作。What the Code items do when chosen. */
export type CodeHeaderActions = {
  format(): void;
  toggleWordWrap(): void;
};

/**
 * Code 的 Format、Word wrap 与 Save 在宿主文件标题栏的 More 菜单中（文件标题栏条目设计第 3 节）：Format 在没有格式化
 * 程序时不可用，Word wrap 为勾选项，Save 在没有未保存修改时不可用。编辑器显示文档时声明这三项，否则声明空数组；卸载时
 * 声明空数组。
 * Code's Format, Word wrap and Save live in the More menu of the host's file header (file header items design,
 * section 3): Format is unavailable without a formatter, Word wrap is a checkbox item, and Save is unavailable without
 * unsaved changes. The three are declared while the editor shows the document, otherwise an empty array; unmounting
 * declares an empty array.
 */
export function useCodeHeaderItems(
  api: ExtensionApi,
  session: DocumentSession,
  { shown, canFormat, wordWrap }: CodeHeaderState,
  actions: CodeHeaderActions,
): void {
  const dirty = useSessionValue(session, ({ state }) => state === "dirty");
  useEffect(() => {
    const items: FileHeaderItem[] = shown
      ? [
          { id: "format", placement: "top", label: "Format", disabled: !canFormat },
          { id: "word-wrap", placement: "top", label: "Word wrap", checked: wordWrap },
          { id: "save", placement: "top", label: "Save", disabled: !dirty },
        ]
      : [];
    void api.workspace.setFileHeaderItems(items).catch((error: unknown) => console.error(error));
  }, [api, shown, canFormat, wordWrap, dirty]);
  useEffect(
    () => () => {
      void api.workspace.setFileHeaderItems([]).catch((error: unknown) => console.error(error));
    },
    [api],
  );

  // 选择通知使用最近一次渲染的操作。Choices use the actions of the latest render.
  const latest = useRef(actions);
  useLayoutEffect(() => {
    latest.current = actions;
  });
  useEffect(() => {
    const subscription = api.workspace.onDidSelectFileHeaderItem(({ itemId }) => {
      if (itemId === "format") latest.current.format();
      else if (itemId === "word-wrap") latest.current.toggleWordWrap();
      else if (itemId === "save") notifyFailure(api, session.save());
    });
    return () => subscription.dispose();
  }, [api, session]);
}
