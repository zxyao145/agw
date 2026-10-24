import type { ExtensionPermission } from "@agw/extension-sdk";

/** 文件查看器中有未保存修改的文件。A file with unsaved changes in a file viewer. */
export type UnsavedFileItem = {
  fileName: string;
  viewerTitle: string;
  extensionId: string;
  extensionName: string;
};

/** 检查后的 extension 消息框。A checked extension message box. */
export type MessageBoxContent = {
  /** 未指定时为 null。Null when not given. */
  type: "info" | "warning" | "error" | null;
  message: string;
  detail: string | null;
  buttons: string[];
  /** 获得焦点的按钮；未指定时为 null。The focused button; null when not given. */
  defaultId: number | null;
  /** 按 `Esc` 或关闭窗口时的回答。The answer for `Esc` or closing the window. */
  cancelId: number;
};

/**
 * 宿主页面请求的确认：`message` 以按钮下标回答，其他以 true/false 回答。`clientId` 由宿主页面给出，用于页面离开时撤回消息框。
 * A confirmation the host page requests: `message` is answered with a button index, the others with true or false.
 * The host page gives `clientId`, which withdraws a message box when its page goes away.
 */
export type HostConfirmationRequest =
  | { kind: "discard-unsaved"; items: UnsavedFileItem[] }
  | { kind: "open-link"; extensionId: string; extensionName: string; url: string }
  | ({
      kind: "message";
      clientId: string;
      extensionId: string;
      extensionName: string;
    } & MessageBoxContent);

/** 安装或启用 extension 的权限批准；说明、动作含义与按钮由应用生成。A permission approval for installing or enabling an extension; descriptions, action meanings and buttons come from the app. */
export type PermissionApprovalView = {
  kind: "install" | "enable";
  extensionId: string;
  extensionName: string;
  version: string;
  /** 替换安装时已经安装的版本。The installed version when replacing one. */
  installedVersion: string | null;
  /** 安装的版本低于已经安装的版本。The version being installed is older than the installed one. */
  downgrade: boolean;
  permissions: { name: ExtensionPermission; description: string; highRisk: boolean }[];
  hostPermissions: { pattern: string; unencrypted: boolean }[];
  /** 读取数据的权限与网络访问同时声明。Data-reading permissions are declared together with network access. */
  sendsData: boolean;
  /** 声明了 `execution`。Declares `execution`. */
  runsAgents: boolean;
};

/** 确认窗口显示的内容。What the confirmation window shows. */
export type ConfirmationView =
  | { kind: "discard-unsaved"; items: UnsavedFileItem[] }
  | { kind: "open-link"; extensionId: string; extensionName: string; url: string }
  | ({ kind: "message"; extensionId: string; extensionName: string } & MessageBoxContent)
  | PermissionApprovalView;

export type ConfirmationAnswer = boolean | number;

/** 确认窗口的 preload 提供的接口，只在确认窗口中存在。The interface the confirmation window's preload exposes, only in that window. */
export type AgwConfirmationBridge = {
  /** 这个窗口的请求。The request of this window. */
  request(): Promise<{ requestId: string; view: ConfirmationView }>;
  /** 页面完成绘制：主进程按内容高度调整窗口并显示。The page rendered; main sizes the window to the content height and shows it. */
  ready(requestId: string, height: number): void;
  answer(requestId: string, answer: ConfirmationAnswer): void;
};
