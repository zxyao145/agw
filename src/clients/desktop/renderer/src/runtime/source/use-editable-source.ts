"use client";

import * as React from "react";
import { toast } from "sonner";
import type { FileViewerFile } from "@agw/extension-sdk";
import type { EditableSource, SourceDocumentState } from "@agw/projects";

import type { ExtensionHost } from "../extensions/extension-host";
import type { SourceDocument, SourceDocumentServices } from "./source-document";

const subscribeNothing = () => () => {};

/**
 * 文件标签首次显示 Source 时打开文档。文档由宿主保留和释放。
 * Opens the document on the file tab's first Source display. The host retains and releases it.
 */
export function useEditableSource(
  host: ExtensionHost,
  tabId: string,
  file: FileViewerFile | null,
): EditableSource {
  const [document, setDocument] = React.useState<SourceDocument | null>(() =>
    host.sourceDocuments.get(tabId),
  );
  const opening = React.useRef(false);
  const mounted = React.useRef(true);
  React.useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
    };
  }, []);

  const services = React.useMemo<SourceDocumentServices>(
    () => ({
      readFile: (uri) => host.readHostFile(uri),
      writeFile: (uri, data, options) => host.writeHostFile(uri, data, options),
      // 只在读写时取得：普通浏览器中没有 Desktop bridge。Resolved on use: a plain browser has no Desktop bridge.
      codec: () => host.textCodec(),
      showMessageBox: (request, signal) => host.showHostMessageBox(request, signal),
      notifyError: (message) => toast.error(message),
      setUnsaved: (fileName) => host.setSourceUnsaved(tabId, fileName),
      platform: host.context.snapshot().app.platform,
    }),
    [host, tabId],
  );

  const state: SourceDocumentState | null = React.useSyncExternalStore(
    document === null ? subscribeNothing : (listener) => document.subscribe(listener),
    () => document?.state ?? null,
    () => null,
  );

  const show = React.useCallback(() => {
    if (file === null) return;
    if (document !== null) {
      document.show(file);
      return;
    }
    if (opening.current) return;
    opening.current = true;
    void host.sourceDocuments.open(tabId, file, services).then(
      (opened) => {
        opening.current = false;
        if (mounted.current && host.sourceDocuments.get(tabId) === opened) {
          opened.show(file);
          setDocument(opened);
        }
      },
      (error: unknown) => {
        opening.current = false;
        toast.error(error instanceof Error ? error.message : String(error));
      },
    );
  }, [document, file, host, tabId, services]);

  return React.useMemo(() => ({ document: state, show }), [state, show]);
}
