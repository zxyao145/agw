import type { ExtensionApi, ExtensionError } from "@agw/extension-sdk";

export function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Extension API 错误的错误码；其他错误为 null。The code of an Extension API error, or null for other errors. */
export function errorCode(error: unknown): string | null {
  return error instanceof Error ? ((error as Partial<ExtensionError>).code ?? null) : null;
}

/**
 * 读取文本文档失败时显示的说明：`ERR_NOT_TEXT` 与 `ERR_UNKNOWN_ENCODING` 使用固定的文字，其他错误显示错误信息。
 * The explanation shown when reading a text document fails: fixed texts for `ERR_NOT_TEXT` and
 * `ERR_UNKNOWN_ENCODING`, the error message for other errors.
 */
export function documentErrorMessage(error: unknown): string {
  switch (errorCode(error)) {
    case "ERR_NOT_TEXT":
      return "This file is binary, so it can't be edited as text.";
    case "ERR_UNKNOWN_ENCODING":
      return "The encoding of this file couldn't be detected.";
    default:
      return errorMessage(error);
  }
}

/**
 * 等待宿主操作完成；用户取消（`ERR_CANCELED`）时不提示，其他错误用通知显示。
 * Awaits a host operation; a user cancel (`ERR_CANCELED`) is silent, other errors are shown as notifications.
 */
export function notifyFailure(api: ExtensionApi, operation: Promise<void>): void {
  void operation.catch((error: unknown) => {
    if (error instanceof Error && (error as Partial<ExtensionError>).code === "ERR_CANCELED")
      return;
    api.ui.notify({ type: "error", message: errorMessage(error) });
  });
}
