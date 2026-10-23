import { toast } from "sonner";

import { ExtensionHost } from "./extension-host";
import { HostOverlayTracker } from "./host-overlay-tracker";

export type ExtensionRuntime = { host: ExtensionHost; tracker: HostOverlayTracker };

// 模块级单例：生命周期不绑定 effect 的清理，宿主外壳卸载不会关闭 frame。
// Module-level singletons: their lifetime is not bound to effect cleanup, so shell unmounts never close frames.
let runtime: ExtensionRuntime | null = null;

/** 只在浏览器中调用：静态导出预渲染阶段没有 window。Call in the browser only; prerendering has no window. */
export function getExtensionRuntime(): ExtensionRuntime {
  if (runtime) return runtime;
  const host = new ExtensionHost({
    // 普通浏览器中没有 bridge，布局照常工作且不加载 extension。Without a bridge the layout works and no extension loads.
    bridge: window.agwDesktop ?? null,
    app: { version: "", platform: "darwin", architecture: "" },
    notify: (extensionName, notification) => {
      const message = `${extensionName}: ${notification.message}`;
      if (notification.type === "error") toast.error(message);
      else if (notification.type === "success") toast.success(message);
      else toast(message);
    },
  });
  const tracker = new HostOverlayTracker(document, (frameIds) => host.setObscuredFrames(frameIds));
  runtime = { host, tracker };
  return runtime;
}
