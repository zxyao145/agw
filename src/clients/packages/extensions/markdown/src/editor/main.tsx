import { defineView, type FileViewerParams } from "@agw/extension-sdk";
import { createRoot } from "react-dom/client";

import { applyDocumentFontSize } from "../shared/apply-font-sizes";
import { notifyFailure } from "../shared/errors";
import { createSettingsGate } from "../shared/settings-gate";
import { applyHighlightTheme } from "../shared/theme";
import { applyWorkspaceBackdrop } from "../shared/workspace-backdrop";
import { EditDocument } from "./edit-document";
import { EditView, unmountEditView } from "./edit-view";
import { floatingChatContext } from "./floating-chat-context";
import { publishHeaderItems } from "./header-items";
import "../shared/document-error.css";
import "../shared/editor-chrome.css";
import "../shared/mermaid-diagram.css";
import "./edit.css";

const container = document.getElementById("root");
if (!container) throw new Error("The #root element is missing.");
const root = createRoot(container);

const gate = createSettingsGate(root, async (api, settings, params) => {
  const { file } = params as FileViewerParams;
  const floatingChat = await api.chat.createFloatingChat({ context: floatingChatContext(file) });
  let fontSize: ReturnType<typeof applyDocumentFontSize> | null = null;
  let editDocument: EditDocument | null = null;
  let headerItems: ReturnType<typeof publishHeaderItems> | null = null;
  try {
    fontSize = applyDocumentFontSize(api, settings);
    editDocument = new EditDocument(api, file);
    headerItems = publishHeaderItems(api, editDocument.session);
    root.render(<EditView api={api} editDocument={editDocument} />);
    const document = editDocument;
    const font = fontSize;
    const headers = headerItems;
    return {
      // 每次显示都交给文档会话：同一个文件刷新，另一个文件切换。Every show goes to the session: refresh or switch.
      show(next) {
        const nextFile = (next as FileViewerParams).file;
        notifyFailure(api, floatingChat.setContext(floatingChatContext(nextFile)));
        return document.show(nextFile);
      },
      hide() {
        return floatingChat.setContext({});
      },
      async dispose() {
        font.dispose();
        headers.dispose();
        unmountEditView(root, document);
        await floatingChat.dispose();
      },
    };
  } catch (error) {
    headerItems?.dispose();
    fontSize?.dispose();
    if (editDocument) unmountEditView(root, editDocument);
    await floatingChat.dispose();
    throw error;
  }
});

defineView({
  async onLoad(api, params) {
    api.theme.apply();
    applyHighlightTheme(api);
    applyWorkspaceBackdrop(api);
    await gate.onLoad(api, params);
  },
  onShow: gate.onShow,
  onHide: gate.onHide,
  onUnload: gate.onUnload,
});
