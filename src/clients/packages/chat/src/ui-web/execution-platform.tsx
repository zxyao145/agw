"use client";

import * as React from "react";

type ExecutionPlatformContextValue = {
  serverId: string;
  onActiveCountChange?: (activeCount: number) => void;
};

const browserExecutionPlatform: ExecutionPlatformContextValue = {
  serverId: "browser",
};
const ExecutionPlatformContext = React.createContext(browserExecutionPlatform);

export function ExecutionPlatformProvider({
  children,
  serverId,
  onActiveCountChange,
}: React.PropsWithChildren<ExecutionPlatformContextValue>) {
  const value = React.useMemo(
    () => ({ serverId, onActiveCountChange }),
    [onActiveCountChange, serverId],
  );
  return (
    <ExecutionPlatformContext.Provider value={value}>{children}</ExecutionPlatformContext.Provider>
  );
}

export function useExecutionPlatform(): ExecutionPlatformContextValue {
  return React.useContext(ExecutionPlatformContext);
}
