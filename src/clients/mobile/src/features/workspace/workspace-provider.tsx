import {
  NativeWorkspaceProvider,
  useNativeWorkspace,
  type NativeVerifiedServer,
} from "@agw/chat-native/workspace";
import React from "react";

import { useSession } from "@/features/servers/session-provider";

export function WorkspaceProvider({ children }: { children: React.ReactNode }) {
  const { verifiedServer } = useSession();
  return (
    <NativeWorkspaceProvider verifiedServer={verifiedServer as NativeVerifiedServer | null}>
      {children}
    </NativeWorkspaceProvider>
  );
}

export const useWorkspace = useNativeWorkspace;
export type { NativeWorkspaceContextValue as WorkspaceContextValue } from "@agw/chat-native/workspace";
