import {
  NativeWorkspaceProvider,
  useNativeWorkspace,
  type NativeVerifiedServer,
} from "@agw/chat-native/workspace";
import React from "react";

import { useTurnNotification } from "@/features/chat/use-turn-notification";
import { useSession } from "@/features/servers/session-provider";

export function WorkspaceProvider({ children }: { children: React.ReactNode }) {
  const { verifiedServer } = useSession();
  const onTurnFinished = useTurnNotification();
  return (
    <NativeWorkspaceProvider
      verifiedServer={verifiedServer as NativeVerifiedServer | null}
      onTurnFinished={onTurnFinished}
    >
      {children}
    </NativeWorkspaceProvider>
  );
}

export const useWorkspace = useNativeWorkspace;
export type { NativeWorkspaceContextValue as WorkspaceContextValue } from "@agw/chat-native/workspace";
