import type { ExtensionPermission } from "@agw/extension-sdk";

/** 应用给出的权限说明，Extensions 页面与权限批准窗口共用。Permission descriptions written by the app, shared by the Extensions page and the approval window. */
export const PERMISSION_DESCRIPTIONS: Record<ExtensionPermission, string> = {
  "context.user": "Read your user information.",
  "context.project": "Read the current Project and conversation, and list your Projects.",
  "context.agent": "Read the current Agent or Agentflow, and list available Agents and Agentflows.",
  "fs.local.read": "Read any file or folder on this computer.",
  "fs.local.write": "Create, change, delete or move any file or folder on this computer.",
  "fs.cloud.read": "Read files in your Projects on the current Server.",
  "fs.cloud.write": "Write, delete or move files in your Projects on the current Server.",
  execution:
    "Run your Agents and Agentflows, and answer tool approvals and input requests for you.",
  a2a: "Call the A2A endpoints of the current Server.",
  "chat.composer": "Insert text into the chat composer.",
  "chat.observe":
    "Observe chat traffic on the current Server: requests sent by chat and every message the Server returns, in all conversations.",
  "clipboard.read": "Read the clipboard.",
  "clipboard.write": "Write to the clipboard.",
};

/** 读取用户数据的权限；与网络访问同时声明时提示数据可能被发送。Permissions that read user data; with network access the data may leave. */
export const READ_PERMISSIONS: readonly ExtensionPermission[] = [
  "fs.local.read",
  "fs.cloud.read",
  "clipboard.read",
  "context.user",
  "context.project",
  "context.agent",
];
