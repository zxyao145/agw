import {
  NativeChatProvider,
  useNativeComposer,
  type NativeChatBindings,
} from "@agw/chat-native/provider";
import React from "react";

import { useWorkspace } from "@/features/workspace/workspace-provider";

export function ComposerProvider({ children }: { children: React.ReactNode }) {
  const workspace = useWorkspace();
  return (
    <NativeChatProvider bindings={workspace as NativeChatBindings}>{children}</NativeChatProvider>
  );
}

export const useComposer = useNativeComposer;
