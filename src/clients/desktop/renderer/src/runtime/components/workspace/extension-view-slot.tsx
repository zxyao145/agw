"use client";

import * as React from "react";
import { cn } from "@agw/components";

import type { ExtensionHost } from "../../extensions/extension-host";
import type { HostOverlayTracker } from "../../extensions/host-overlay-tracker";

export type ExtensionViewSlotProps = {
  host: ExtensionHost;
  tracker: HostOverlayTracker;
  frameId: string;
  /** 截图（宿主弹出层遮住视图期间）。The snapshot shown while a host overlay covers the view. */
  snapshot: string | null;
};

/**
 * extension 视图的占位组件：把区域报告给主进程，`WebContentsView` 按它定位。卸载只让 frame 隐藏。
 * The extension view placeholder: reports its area so the `WebContentsView` follows it. Unmounting only hides the frame.
 */
export function ExtensionViewSlot({ host, tracker, frameId, snapshot }: ExtensionViewSlotProps) {
  const ref = React.useRef<HTMLDivElement>(null);

  React.useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    let frame = 0;
    const report = () => {
      cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        const rect = element.getBoundingClientRect();
        const visible = rect.width > 0 && rect.height > 0 && element.isConnected;
        host.reportLayout(
          frameId,
          { x: rect.left, y: rect.top, width: rect.width, height: rect.height },
          visible,
        );
        tracker.setFrameElement(frameId, visible ? element : null);
      });
    };
    const observer = new ResizeObserver(report);
    observer.observe(element);
    window.addEventListener("resize", report);
    report();
    return () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener("resize", report);
      tracker.setFrameElement(frameId, null);
      host.reportLayout(frameId, { x: 0, y: 0, width: 0, height: 0 }, false);
    };
  }, [host, tracker, frameId]);

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
