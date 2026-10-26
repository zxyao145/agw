import type { ReactNode } from "react";

/** 宿主传入的扩展菜单项。A menu item supplied by the host. */
export interface ExtensionMenuItem<TArgs> {
  id: string;
  title: string;
  icon: ReactNode;
  onSelect(args: TArgs): void;
}

/** 对话列表项菜单收到的参数。Arguments of a conversation list item menu. */
export type ConversationItemActionArgs = { conversationId: string; title: string };

/** 文件浏览器右键菜单收到的参数。Arguments of a file explorer context menu. */
export type ExplorerContextMenuArgs = {
  path: string;
  name: string;
  type: "file" | "directory";
  directoryId: string | null;
};
