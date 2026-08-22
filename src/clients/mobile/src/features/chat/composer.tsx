import { NativeChatComposer } from "@agw/chat-native/composer";
import { NativeChatProvider, type NativeChatBindings } from "@agw/chat-native/provider";
import React from "react";

import { useComposer } from "@/features/chat/composer-provider";
import { useWorkspace } from "@/features/workspace/workspace-provider";

export function Composer(props: React.ComponentProps<typeof NativeChatComposer>) {
  const workspace = useWorkspace();
  const composer = useComposer();
  return (
    <NativeChatProvider bindings={workspace as NativeChatBindings} composer={composer}>
      <NativeChatComposer {...props} />
    </NativeChatProvider>
  );
}
