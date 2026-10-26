"use client";

import * as React from "react";
import { cn } from "@agw/components";

import type { ExtensionHost } from "../../extensions/extension-host";
import type { ExtensionLayoutSampler } from "../../extensions/extension-layout-sampler";

export type ExtensionViewSlotProps = {
  host: ExtensionHost;
  layout: ExtensionLayoutSampler;
  frameId: string;
  /** 截图（宿主弹出层遮住视图期间）。The snapshot shown while a host overlay covers the view. */
  snapshot: string | null;
};

/**
 * extension 视图的占位组件：登记到统一的测量，`WebContentsView` 按它的区域与可见性定位。卸载只让 frame 隐藏。
 * The extension view placeholder: registers with the shared measurement, and the `WebContentsView` follows its area
 * and visibility. Unmounting only hides the frame.
 */
export function ExtensionViewSlot({ host, layout, frameId, snapshot }: ExtensionViewSlotProps) {
  const ref = React.useRef<HTMLDivElement>(null);

  React.useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    return layout.register(frameId, element);
  }, [layout, frameId]);

  return (
    <div
      ref={ref}
      className={cn("agw-extension-view-slot", snapshot && "is-snapshot")}
      data-frame-id={frameId}
    >
      {snapshot ? (
        // 同步解码：加载完成后的下一次绘制一定包含截图，宿主随后隐藏 view。
        // Synchronous decoding: the paint after loading always includes the snapshot, then the host hides the view.
        <img
          src={snapshot}
          alt=""
          aria-hidden="true"
          decoding="sync"
          onLoad={() => host.snapshotShown(frameId, snapshot)}
        />
      ) : null}
    </div>
  );
}
