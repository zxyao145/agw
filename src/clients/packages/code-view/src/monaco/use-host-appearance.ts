import * as React from "react";

import { useCodeViewAppearanceRevision } from "../appearance";
import { applyHostAppearance, type HostFont, readHostAppearance } from "./host-appearance";

/**
 * 外观版本变化后的下一帧重新读取并应用宿主外观（宿主在自己的 effect 中更新 class 与变量，晚于子组件）。
 * Reads and applies the host appearance on the frame after the revision changes (hosts update classes and variables
 * in their own effects, after the children).
 */
export function useHostAppearanceUpdates(
  hostRef: React.RefObject<HTMLElement | null>,
  apply: (font: HostFont) => void,
): void {
  const revision = useCodeViewAppearanceRevision();
  const update = React.useEffectEvent(() => {
    const host = hostRef.current;
    if (host !== null) apply(applyHostAppearance(readHostAppearance(host)));
  });
  React.useEffect(() => {
    const frame = requestAnimationFrame(() => update());
    return () => cancelAnimationFrame(frame);
  }, [revision]);
}
