import { toast } from "sonner";

import { ExtensionHost } from "./extension-host";
import { ExtensionLayoutSampler } from "./extension-layout-sampler";
import { HostOverlayTracker } from "./host-overlay-tracker";

export type ExtensionRuntime = {
  host: ExtensionHost;
  tracker: HostOverlayTracker;
  layout: ExtensionLayoutSampler;
};

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
  const layout = createLayoutSampler(host, tracker);
  runtime = { host, tracker, layout };
  return runtime;
}

/** 测量结果交给宿主定位原生视图，并交给遮挡跟踪器。Measurements position the native views and feed the overlay tracker. */
export function createLayoutSampler(
  host: ExtensionHost,
  tracker: HostOverlayTracker,
): ExtensionLayoutSampler {
  return new ExtensionLayoutSampler(document, (frameId, layout, element) => {
    host.reportLayout(frameId, layout.bounds, layout.visible);
    tracker.setFrameElement(frameId, layout.visible ? element : null);
  });
}
