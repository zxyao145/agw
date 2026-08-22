import { NativeContextHistory } from "@agw/chat-native/history";
import { NativeChatProvider, type NativeChatBindings } from "@agw/chat-native/provider";
import { router } from "expo-router";
import React from "react";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { useWorkspace } from "@/features/workspace/workspace-provider";

export function HistoryScreen(): React.JSX.Element {
  const insets = useSafeAreaInsets();
  const workspace = useWorkspace();
  return (
    <NativeChatProvider bindings={workspace as NativeChatBindings}>
      <NativeContextHistory
        safeTop={insets.top}
        safeBottom={insets.bottom}
        onClose={() => router.back()}
        onOpenChat={() => router.replace("/chat")}
        onOpenSettings={() => router.push("/settings")}
      />
    </NativeChatProvider>
  );
}
