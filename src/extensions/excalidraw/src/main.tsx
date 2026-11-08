import "./asset-path";
import "@excalidraw/excalidraw/index.css";
import "./styles.css";

import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { App } from "./app";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

let extensionApi: ExtensionApi | null = null;

/**
 * 渲染一个文件。文件的读取、刷新与切换由 Editor 中的文档会话处理。
 * Renders a file. Reading, refreshing and switching files is handled by the document session in Editor.
 */
function showFile({ file }: FileViewerParams): void {
  if (!extensionApi) throw new Error("The file viewer received a file before onLoad.");
  root.render(<App api={extensionApi} file={file} />);
}

defineView({
  onLoad(api, params) {
    api.theme.apply();
    extensionApi = api;
    showFile(params as FileViewerParams);
  },
  onShow(params) {
    showFile(params as FileViewerParams);
  },
  onUnload() {
    root.unmount();
  },
});
