import {
  NativeConversationHistoryHost,
  type NativeConversationHistoryHandle,
} from "@agw/chat-native/conversation";
import React from "react";

import { useWorkspace } from "@/features/workspace/workspace-provider";

export const ChatScreen = React.forwardRef<NativeConversationHistoryHandle>(
  function ChatScreen(_, ref) {
    const workspace = useWorkspace();
    return (
      <NativeConversationHistoryHost
        ref={ref}
        messages={workspace.messages}
        pendingHumanGate={workspace.pendingHumanGate}
        checkpointAvailability={workspace.checkpointAvailability}
        loading={workspace.isChatLoading}
        reconnecting={workspace.reconnectState !== null}
        error={workspace.error}
        permissionMode={workspace.permissionMode}
        showCheckpointResume={workspace.selectedTarget?.type === "agentflow"}
        checkpointResumeDisabled={workspace.isExecuting || workspace.reconnectState !== null}
        onCheckpointResume={(occurrenceId) => void workspace.resumeCheckpoint(occurrenceId)}
        onHumanResponse={(response) => void workspace.submitHumanResponse(response)}
      />
    );
  },
);
