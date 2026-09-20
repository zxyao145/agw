import {
  NativeConversationHistoryHost,
  type NativeConversationHistoryHandle,
} from "@agw/chat-native/conversation";
import { getExecutionReconnectProgress } from "@agw/chat-native/execution";
import React from "react";

import { useWorkspace } from "@/features/workspace/workspace-provider";

export const ChatScreen = React.forwardRef<NativeConversationHistoryHandle>(
  function ChatScreen(_, ref) {
    const workspace = useWorkspace();
    return (
      <NativeConversationHistoryHost
        ref={ref}
        messages={workspace.messages}
        activeAgentId={
          workspace.selectedTarget?.type === "agent" ? workspace.selectedTarget.id : null
        }
        agentResultFormats={workspace.agents}
        pendingInteraction={workspace.pendingInteraction}
        checkpointAvailability={workspace.checkpointAvailability}
        loading={workspace.isChatLoading}
        reconnectState={workspace.reconnectState}
        error={workspace.error}
        permissionMode={workspace.activePermissionMode ?? undefined}
        showCheckpointResume={workspace.selectedTarget?.type === "agentflow"}
        checkpointResumeDisabled={
          workspace.isExecuting || getExecutionReconnectProgress(workspace.reconnectState) !== null
        }
        onCheckpointResume={(occurrenceId) => void workspace.resumeCheckpoint(occurrenceId)}
        onHumanResponse={(response) => void workspace.submitHumanResponse(response)}
      />
    );
  },
);
