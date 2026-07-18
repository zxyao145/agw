"use client";

import * as React from "react";

type ExecutionPlatformContextValue = {
  isDesktop: boolean;
  serverId: string;
  onActiveCountChange?: (activeCount: number) => void;
};

const browserExecutionPlatform: ExecutionPlatformContextValue = {
  isDesktop: false,
  serverId: "browser",
};
const ExecutionPlatformContext = React.createContext(browserExecutionPlatform);

export function ExecutionPlatformProvider({
  children,
  isDesktop,
  serverId,
  onActiveCountChange,
}: React.PropsWithChildren<ExecutionPlatformContextValue>) {
  const value = React.useMemo(
    () => ({ isDesktop, serverId, onActiveCountChange }),
    [isDesktop, onActiveCountChange, serverId],
  );
  return (
    <ExecutionPlatformContext.Provider value={value}>{children}</ExecutionPlatformContext.Provider>
  );
}

export function useExecutionPlatform(): ExecutionPlatformContextValue {
  return React.useContext(ExecutionPlatformContext);
}
