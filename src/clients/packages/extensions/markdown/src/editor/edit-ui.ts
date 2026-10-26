// 编辑器命令与 React 界面之间的请求：`Mod+K` 打开链接输入框，`/` 菜单的 Image 打开图片地址输入框。
// Requests from editor commands to the React UI: `Mod+K` opens the link input, the `/` menu's Image opens the image
// address input.

type Listener<T> = (value: T) => void;

export type Subscription = { dispose(): void };

function subscribe<T>(listeners: Set<Listener<T>>, listener: Listener<T>): Subscription {
  listeners.add(listener);
  return { dispose: () => listeners.delete(listener) };
}

export class EditUi {
  private readonly linkListeners = new Set<Listener<void>>();
  private readonly imageListeners = new Set<Listener<number>>();

  /** 打开选区的链接输入框；没有界面处理时返回 false。Opens the link input for the selection; false without a UI. */
  public requestLinkEditor(): boolean {
    for (const listener of this.linkListeners) listener();
    return this.linkListeners.size > 0;
  }

  public onLinkEditor(listener: () => void): Subscription {
    return subscribe(this.linkListeners, listener);
  }

  /** 在 `position` 打开图片地址输入框。Opens the image address input at `position`. */
  public requestImageInsert(position: number): void {
    for (const listener of this.imageListeners) listener(position);
  }

  public onImageInsert(listener: (position: number) => void): Subscription {
    return subscribe(this.imageListeners, listener);
  }
}
