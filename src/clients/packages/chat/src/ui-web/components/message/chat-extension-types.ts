export type { ExtensionMenuItem } from "@agw/projects";

/** 对话输入框句柄。Composer handle. */
export interface ChatComposerHandle {
  /** 在光标处插入文本并重新聚焦输入框。Inserts text at the cursor and refocuses the composer. */
  insertText(text: string): void;
}

/** 消息操作菜单项收到的参数。Arguments a message action receives. */
export type ChatMessageActionArgs = {
  messageId: string;
  role: string | null;
  text: string;
  turnId: string | null;
};
