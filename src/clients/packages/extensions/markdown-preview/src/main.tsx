import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { applyHighlightTheme } from "./highlight-theme";
import { MarkdownDocument } from "./markdown-document";
import "./markdown.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);
let api: ExtensionApi | null = null;

/** 显示宿主提供的文件；URI 不同时重新挂载并重新读取。Shows the file from the host; a new URI remounts and re-reads. */
function render(params: unknown): void {
  if (!api) throw new Error("The view received a file before onLoad.");
  const { file } = params as FileViewerParams;
  root.render(<MarkdownDocument key={file.uri} api={api} file={file} />);
}

defineView({
  onLoad(extensionApi, params) {
    api = extensionApi;
    extensionApi.theme.apply();
    applyHighlightTheme(extensionApi);
    render(params);
  },
  onShow: render,
  onUnload() {
    root.unmount();
  },
});
