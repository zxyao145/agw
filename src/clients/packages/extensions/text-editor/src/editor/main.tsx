import { defineView, type ExtensionApi, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { notifyFailure } from "../shared/errors";
import { applyHighlightTheme } from "../shared/theme";
import { EditDocument } from "./edit-document";
import { EditView, unmountEditView } from "./edit-view";
import "../shared/document-error.css";
import "../shared/editor-chrome.css";
import "./edit.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);
let opened: { api: ExtensionApi; editDocument: EditDocument } | null = null;

defineView({
  onLoad(api, params) {
    api.theme.apply();
    applyHighlightTheme(api);
    const { file } = params as FileViewerParams;
    const editDocument = new EditDocument(api, file);
    opened = { api, editDocument };
    root.render(<EditView api={api} editDocument={editDocument} />);
  },
  // 每次显示都交给文档会话：同一个文件刷新，另一个文件切换。Every show goes to the session: refresh or switch.
  onShow(params) {
    if (!opened) throw new Error("The view received a file before onLoad.");
    const { file } = params as FileViewerParams;
    notifyFailure(opened.api, opened.editDocument.show(file));
  },
  onUnload() {
    if (opened) unmountEditView(root, opened.editDocument);
    else root.unmount();
    opened = null;
  },
});
