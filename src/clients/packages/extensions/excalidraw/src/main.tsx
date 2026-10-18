import "./asset-path";
import "@excalidraw/excalidraw/index.css";
import "./styles.css";

import {
  defineView,
  type ExtensionApi,
  type FileViewerFile,
  type FileViewerParams,
} from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { App } from "./app";
import { createTransitionReporter } from "./unsaved-changes";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

let view: {
  api: ExtensionApi;
  reportUnsaved: (unsaved: boolean) => void;
  uri: string | null;
} | null = null;

/**
 * 显示文件。同一文件再次到达（视图从隐藏恢复）时保留编辑；新文件替换场景、文件与历史。
 * Shows a file. The same file arriving again (the view is shown after being hidden) keeps the edits; a new file
 * replaces the scene, files and history.
 */
function showFile(file: FileViewerFile): void {
  if (!view) throw new Error("The file viewer received a file before onLoad.");
  if (file.uri === view.uri) return;
  view.uri = file.uri;
  // 宿主发送新文件前已确认放弃修改。The host confirmed discarding changes before sending a new file.
  view.reportUnsaved(false);
  root.render(<App key={file.uri} api={view.api} file={file} reportUnsaved={view.reportUnsaved} />);
}

defineView({
  onLoad(api, params) {
    api.theme.apply();
    view = {
      api,
      reportUnsaved: createTransitionReporter(
        (unsaved) => void api.workspace.setUnsavedChanges(unsaved),
      ),
      uri: null,
    };
    showFile((params as FileViewerParams).file);
  },
  onShow(params) {
    showFile((params as FileViewerParams).file);
  },
  onUnload() {
    root.unmount();
  },
});
