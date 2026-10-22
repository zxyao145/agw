import { defineView, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { createSettingsGate } from "../shared/settings-gate";
import { CodeDocument } from "./code-document";
import { applyCodeTheme } from "./code-theme";
import { CodeView } from "./code-view";
import "../shared/document-error.css";
import "../shared/editor-chrome.css";
import "./code.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

const gate = createSettingsGate(root, (api, settings, params) => {
  const { file } = params as FileViewerParams;
  const codeDocument = new CodeDocument(api, file);
  root.render(<CodeView api={api} codeDocument={codeDocument} settings={settings} />);
  return {
    // 每次显示都交给文档：同一个文件刷新，另一个文件切换。Every show goes to the document: refresh or switch.
    show: (next) => codeDocument.show((next as FileViewerParams).file),
    dispose() {
      root.unmount();
      codeDocument.dispose();
    },
  };
});

defineView({
  async onLoad(api, params) {
    api.theme.apply();
    applyCodeTheme(api.theme.get());
    api.theme.onDidChange(applyCodeTheme);
    await gate.onLoad(api, params);
  },
  onShow: gate.onShow,
  onUnload: gate.onUnload,
});
