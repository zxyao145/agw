import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { applyHighlightTheme } from "../shared/theme";
import { MarkdownDocument } from "./markdown-document";
import "../shared/document-error.css";
import "./markdown.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);
let api: ExtensionApi | null = null;
// onLoad 与每次 onShow 都加 1，文档随之重新读取。Incremented by onLoad and every onShow, so the document reads again.
let shown = 0;

/**
 * 显示宿主提供的文件并重新读取，同一个文件也读取；URI 不同时重新挂载。
 * Shows the file from the host and reads it again, same file included; a new URI remounts.
 */
function render(params: unknown): void {
  if (!api) throw new Error("The view received a file before onLoad.");
  const { file } = params as FileViewerParams;
  shown += 1;
  root.render(<MarkdownDocument key={file.uri} api={api} file={file} shown={shown} />);
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
