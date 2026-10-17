export type ExtensionRuntimeResetSteps = {
  destroyAllFrames(): void;
  releaseAllResources(): void;
  closeAllProxies(): Promise<void>;
  clearActivations(): void;
  clearHost(): void;
};

/**
 * 旧宿主页面结束时清理全部 extension：不调用钩子、不发送 `execution.closed`，旧页面已经无法处理消息。
 * Cleans up every extension when the old host page ended, without hooks or `execution.closed`.
 */
export class ExtensionRuntimeReset {
  public constructor(private readonly steps: ExtensionRuntimeResetSteps) {}

  public async run(): Promise<void> {
    this.steps.destroyAllFrames();
    this.steps.releaseAllResources();
    this.steps.clearActivations();
    this.steps.clearHost();
    await this.steps.closeAllProxies();
  }
}
