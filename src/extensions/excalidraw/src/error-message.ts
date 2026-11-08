import type { ExtensionApi, ExtensionError } from "@agw/extension-sdk";

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * 等待操作完成；用户取消（`ERR_CANCELED`）时不提示，其他错误用通知显示（与 Markdown extension 相同）。
 * Awaits an operation; a user cancel (`ERR_CANCELED`) is silent, other errors are shown as notifications (as in the
 * Markdown extension).
 */
export function notifyFailure(api: ExtensionApi, operation: Promise<void>): void {
  void operation.catch((error: unknown) => {
    if (error instanceof Error && (error as Partial<ExtensionError>).code === "ERR_CANCELED") {
      return;
    }
    api.ui.notify({ type: "error", message: errorMessage(error) });
  });
}
