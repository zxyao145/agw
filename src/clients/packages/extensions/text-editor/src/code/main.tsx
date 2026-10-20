import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { notifyFailure } from "../shared/errors";
import { CodeDocument } from "./code-document";
import { applyCodeTheme } from "./code-theme";
import { CodeView } from "./code-view";
import "../shared/document-error.css";
import "../shared/editor-chrome.css";
import "./code.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);
let opened: { api: ExtensionApi; codeDocument: CodeDocument } | null = null;

defineView({
  onLoad(api, params) {
    api.theme.apply();
    applyCodeTheme(api.theme.get());
    api.theme.onDidChange(applyCodeTheme);
    const { file } = params as FileViewerParams;
    const codeDocument = new CodeDocument(api, file);
    opened = { api, codeDocument };
    root.render(<CodeView api={api} codeDocument={codeDocument} />);
  },
  // 每次显示都交给文档：同一个文件刷新，另一个文件切换。Every show goes to the document: refresh or switch.
  onShow(params) {
    if (!opened) throw new Error("The view received a file before onLoad.");
    const { file } = params as FileViewerParams;
    notifyFailure(opened.api, opened.codeDocument.show(file));
  },
  onUnload() {
    root.unmount();
    opened?.codeDocument.dispose();
    opened = null;
  },
});
