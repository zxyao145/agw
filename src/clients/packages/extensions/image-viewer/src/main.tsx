import "./styles.css";

import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { ImageViewer } from "./image-viewer";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

let extensionApi: ExtensionApi | null = null;
let shown: { uri: string; revision: number } | null = null;

/**
 * 显示一个文件：同一个文件再次显示时版本号加 1，查看器重新读取并保留缩放；另一个文件以新的 key 挂载，从适应显示区开始。
 * Shows a file: showing the same file again increments the revision, so the viewer reads it again and keeps the zoom;
 * another file mounts under a new key and starts fitted to the display area.
 */
function showFile({ file }: FileViewerParams): void {
  if (!extensionApi) throw new Error("The file viewer received a file before onLoad.");
  shown = { uri: file.uri, revision: shown?.uri === file.uri ? shown.revision + 1 : 0 };
  root.render(
    <ImageViewer key={file.uri} api={extensionApi} file={file} revision={shown.revision} />,
  );
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
