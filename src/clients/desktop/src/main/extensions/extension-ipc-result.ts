import type { ExtensionIpcError, ExtensionIpcResult } from "../../shared/contracts";

/** 主进程检查失败时抛出的错误，`code` 原样交给 extension。A main-process check failure whose `code` reaches the extension unchanged. */
export class ExtensionOperationError extends Error {
  public constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ExtensionError";
  }
}

/** 把任意抛出值转换为可结构化克隆的错误数据。Converts any thrown value into structured-clone-safe error data. */
export function toExtensionIpcError(error: unknown): ExtensionIpcError {
  if (error instanceof Error) {
    const details = error as NodeJS.ErrnoException;
    return {
      name: error.name,
      message: error.message,
      code: typeof details.code === "string" ? details.code : null,
      syscall: typeof details.syscall === "string" ? details.syscall : null,
      path: typeof details.path === "string" ? details.path : null,
    };
  }
  return { name: "Error", message: String(error), code: null, syscall: null, path: null };
}

/** 能力通道 handler 的最外层包装。The outermost wrapper of a capability-channel handler. */
export async function toExtensionIpcResult<T>(
  operation: () => Promise<T>,
): Promise<ExtensionIpcResult<T>> {
  try {
    return { ok: true, value: await operation() };
  } catch (error) {
    return { ok: false, error: toExtensionIpcError(error) };
  }
}
