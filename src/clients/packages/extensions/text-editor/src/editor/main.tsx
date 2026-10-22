import { defineView, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { applyDocumentFontSize } from "../shared/apply-font-sizes";
import { createSettingsGate } from "../shared/settings-gate";
import { applyHighlightTheme } from "../shared/theme";
import { EditDocument } from "./edit-document";
import { EditView, unmountEditView } from "./edit-view";
import "../shared/document-error.css";
import "../shared/editor-chrome.css";
import "./edit.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

const gate = createSettingsGate(root, (api, settings, params) => {
  const fontSize = applyDocumentFontSize(api, settings);
  const { file } = params as FileViewerParams;
  const editDocument = new EditDocument(api, file);
  root.render(<EditView api={api} editDocument={editDocument} />);
  return {
    // 每次显示都交给文档会话：同一个文件刷新，另一个文件切换。Every show goes to the session: refresh or switch.
    show: (next) => editDocument.show((next as FileViewerParams).file),
    dispose() {
      fontSize.dispose();
      unmountEditView(root, editDocument);
    },
  };
});

defineView({
  async onLoad(api, params) {
    api.theme.apply();
    applyHighlightTheme(api);
    await gate.onLoad(api, params);
  },
  onShow: gate.onShow,
  onUnload: gate.onUnload,
});
