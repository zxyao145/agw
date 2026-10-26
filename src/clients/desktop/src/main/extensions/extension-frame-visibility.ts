/**
 * 一个 view frame 的可见性：实际可见性始终为 `layoutVisible && !hidden`。遮挡分两步：先截图（view 仍显示），
 * 宿主画出截图后再隐藏，切换期间不出现空白。
 * Visibility of one view frame: the effective visibility is always `layoutVisible && !hidden`. Obscuring has two
 * steps: capture while the view still shows, then hide once the host painted the snapshot, so no blank shows between.
 */
export class FrameVisibility {
  private layoutVisible = false;
  private obscuring = false;
  private hidden = false;
  private obscureToken = 0;
  private destroyed = false;

  public get visible(): boolean {
    return this.layoutVisible && !this.hidden;
  }

  public get isObscured(): boolean {
    return this.obscuring;
  }

  /** `updateExtensionFrameLayout` 设置的可见性，返回新的实际可见性。Layout visibility; returns the new effective visibility. */
  public setLayoutVisible(visible: boolean): boolean {
    this.layoutVisible = visible;
    return this.visible;
  }

  /** 开始遮挡：标记遮挡但暂不隐藏，返回本次的序号。Begins obscuring without hiding yet; returns this request's token. */
  public beginObscure(): number {
    this.obscuring = true;
    this.obscureToken += 1;
    return this.obscureToken;
  }

  /** 截图完成：只有遮挡状态仍是这次请求的结果时才采用截图。Capture finished: the snapshot is used only if the state is still this request's. */
  public completeObscure(token: number): boolean {
    return !this.destroyed && this.obscuring && token === this.obscureToken;
  }

  /**
   * 隐藏 view：只对仍有效的遮挡请求生效，返回是否已隐藏。
   * Hides the view for a still-current obscure request; returns whether it is hidden now.
   */
  public hide(token: number): boolean {
    if (!this.completeObscure(token)) return false;
    this.hidden = true;
    return true;
  }

  /** 恢复显示，返回新的实际可见性。Reveals the frame; returns the new effective visibility. */
  public reveal(): boolean {
    this.obscuring = false;
    this.hidden = false;
    this.obscureToken += 1;
    return this.visible;
  }

  public destroy(): void {
    this.destroyed = true;
  }
}
