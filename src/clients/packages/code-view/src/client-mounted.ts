import * as React from "react";

const subscribe = () => () => {};

/**
 * 客户端挂载后为 true；服务端渲染与静态导出的预渲染中为 false，Monaco 只在客户端加载。
 * True once mounted on the client; false during server rendering and static-export prerendering, so Monaco loads
 * only on the client.
 */
export function useClientMounted(): boolean {
  return React.useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
}
