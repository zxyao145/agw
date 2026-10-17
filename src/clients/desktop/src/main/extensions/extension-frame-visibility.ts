/**
 * 一个 view frame 的可见性：实际可见性始终为 `layoutVisible && !obscured`。
 * Visibility of one view frame: the effective visibility is always `layoutVisible && !obscured`.
 */
export class FrameVisibility {
  private layoutVisible = false;
  private obscured = false;
  private obscureToken = 0;
  private destroyed = false;

  public get visible(): boolean {
    return this.layoutVisible && !this.obscured;
  }

  public get isObscured(): boolean {
    return this.obscured;
  }

  /** `updateExtensionFrameLayout` 设置的可见性，返回新的实际可见性。Layout visibility; returns the new effective visibility. */
  public setLayoutVisible(visible: boolean): boolean {
    this.layoutVisible = visible;
    return this.visible;
  }

  /** 开始遮挡：标记遮挡但暂不隐藏，返回本次的序号。Begins obscuring without hiding yet; returns this request's token. */
  public beginObscure(): number {
    this.obscured = true;
    this.obscureToken += 1;
    return this.obscureToken;
  }

  /** 截图完成：只有遮挡状态仍是这次请求的结果时才采用截图。Capture finished: the snapshot is used only if the state is still this request's. */
  public completeObscure(token: number): boolean {
    return !this.destroyed && this.obscured && token === this.obscureToken;
  }

  /** 恢复显示，返回新的实际可见性。Reveals the frame; returns the new effective visibility. */
  public reveal(): boolean {
    this.obscured = false;
    this.obscureToken += 1;
    return this.visible;
  }

  public destroy(): void {
    this.destroyed = true;
  }
}
