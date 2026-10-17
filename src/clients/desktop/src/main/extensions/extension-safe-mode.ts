import type { ExtensionSafeModeState } from "../../shared/contracts";

/**
 * extension 激活期间宿主页面崩溃或失去响应时进入安全模式；只保存在内存中，重启后清除。
 * Enters safe mode when the host page crashes or hangs while extensions activate; kept in memory only.
 */
export class ExtensionSafeModeGuard {
  private readonly activating = new Set<string>();
  private current: ExtensionSafeModeState | null = null;

  public get state(): ExtensionSafeModeState | null {
    return this.current;
  }

  public get activatingIds(): string[] {
    return [...this.activating];
  }

  public beginActivation(extensionId: string): void {
    this.activating.add(extensionId);
  }

  public endActivation(extensionId: string): void {
    this.activating.delete(extensionId);
  }

  /** 宿主渲染进程结束时调用，激活期间才进入安全模式。Called when the host renderer ends; enters safe mode only during activation. */
  public recordHostFailure(reason: "crashed" | "unresponsive"): ExtensionSafeModeState | null {
    if (this.activating.size === 0) return null;
    this.current = { reason, extensionIds: [...this.activating] };
    return this.current;
  }

  /** 托盘 "Reload without extensions"。The tray's "Reload without extensions". */
  public enterManual(): ExtensionSafeModeState {
    this.current = { reason: "manual", extensionIds: [...this.activating] };
    return this.current;
  }

  public resume(): void {
    this.current = null;
  }

  /** `ExtensionRuntimeReset` 清空正在激活的集合。`ExtensionRuntimeReset` clears the activation set. */
  public clearActivations(): void {
    this.activating.clear();
  }
}
