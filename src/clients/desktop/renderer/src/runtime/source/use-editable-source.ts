"use client";

import * as React from "react";
import { toast } from "sonner";
import type { FileViewerFile } from "@agw/extension-sdk";
import type { EditableSource, SourceDocumentState } from "@agw/projects";

import type { ExtensionHost } from "../extensions/extension-host";
import { SourceDocument, type SourceDocumentServices } from "./source-document";

const subscribeNothing = () => () => {};

/**
 * Files 视图的可编辑 Source（设计文档 5.3 节）：第一次显示 Source 时创建文档，之后在查看器之间切换都保留；选中的文件
 * 变化时切换会话，每次显示 Source 时刷新；没有选中文件或组件卸载时释放。
 * The Files view's editable Source (design section 5.3): the document is created on Source's first display and kept
 * while switching viewers; a new selected file switches the session and every Source display refreshes it; it is
 * released without a selected file or when the component unmounts.
 */
export function useEditableSource(
  host: ExtensionHost,
  file: FileViewerFile | null,
): EditableSource {
  const [document, setDocument] = React.useState<SourceDocument | null>(null);
  const opening = React.useRef(false);
  const mounted = React.useRef(true);
  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);
  const fileRef = React.useRef(file);
  fileRef.current = file;

  const services = React.useMemo<SourceDocumentServices>(
    () => ({
      readFile: (uri) => host.readHostFile(uri),
      writeFile: (uri, data, options) => host.writeHostFile(uri, data, options),
      // 只在读写时取得：普通浏览器中没有 Desktop bridge。Resolved on use: a plain browser has no Desktop bridge.
      codec: () => host.textCodec(),
      showMessageBox: (request, signal) => host.showHostMessageBox(request, signal),
      notifyError: (message) => toast.error(message),
      setUnsaved: (fileName) => host.setSourceUnsaved(fileName),
      platform: host.context.snapshot().app.platform,
    }),
    [host],
  );

  const state: SourceDocumentState | null = React.useSyncExternalStore(
    document === null ? subscribeNothing : (listener) => document.subscribe(listener),
    () => document?.state ?? null,
    () => null,
  );

  // 选中的文件变化：已有文档时切换到它（切换前宿主已经确认放弃修改）。A new selected file switches an existing
  // document to it (the host confirmed discarding before switching).
  const uri = file?.uri ?? null;
  React.useEffect(() => {
    const current = fileRef.current;
    if (document !== null && current !== null) document.show(current);
  }, [document, uri]);

  // 没有选中文件时释放文档。The document is released without a selected file.
  React.useEffect(() => {
    if (uri === null && document !== null) setDocument(null);
  }, [uri, document]);
  React.useEffect(() => () => document?.dispose(), [document]);

  const show = React.useCallback(() => {
    const current = fileRef.current;
    if (current === null) return;
    if (document !== null) {
      document.show(current);
      return;
    }
    if (opening.current) return;
    opening.current = true;
    SourceDocument.open(current, services).then(
      (opened) => {
        opening.current = false;
        // 打开完成之前组件已卸载，或文件已取消选择：直接释放。Unmounted or deselected before opening finished: release.
        if (!mounted.current || fileRef.current === null) opened.dispose();
        else setDocument(opened);
      },
      (error: unknown) => {
        opening.current = false;
        toast.error(error instanceof Error ? error.message : String(error));
      },
    );
  }, [document, services]);

  return React.useMemo(() => ({ document: state, show }), [state, show]);
}
