import { defineView, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { applyDocumentFontSize } from "../shared/apply-font-sizes";
import { createSettingsGate } from "../shared/settings-gate";
import { applyHighlightTheme } from "../shared/theme";
import { applyWorkspaceBackdrop } from "../shared/workspace-backdrop";
import { MarkdownDocument } from "./markdown-document";
import "../shared/document-error.css";
import "./markdown.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

const gate = createSettingsGate(root, (api, settings, params) => {
  const fontSize = applyDocumentFontSize(api, settings);
  // 打开与每次 show 都加 1，文档随之重新读取。Incremented by the open and every show, so the document reads again.
  let shown = 0;
  /**
   * 显示宿主提供的文件并重新读取，同一个文件也读取；URI 不同时重新挂载。
   * Shows the file from the host and reads it again, same file included; a new URI remounts.
   */
  const render = (next: unknown) => {
    const { file } = next as FileViewerParams;
    shown += 1;
    root.render(<MarkdownDocument key={file.uri} api={api} file={file} shown={shown} />);
  };
  render(params);
  return {
    show: render,
    dispose() {
      fontSize.dispose();
      root.unmount();
    },
  };
});

defineView({
  async onLoad(api, params) {
    api.theme.apply();
    applyHighlightTheme(api);
    applyWorkspaceBackdrop(api);
    await gate.onLoad(api, params);
  },
  onShow: gate.onShow,
  onUnload: gate.onUnload,
});
