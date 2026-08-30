import * as React from "react";
import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerHeader,
  DrawerTitle,
} from "@agw/components";
import { X } from "lucide-react";
import type { AgentflowDto } from "../../../../types/agentflow";
import { Chat } from "@agw/chat";
import { EMPTY_TOKEN_USAGE } from "@agw/api";

interface ExecuteAgentflowDrawerProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  agentflow: AgentflowDto | null;
}

export function ExecuteAgentflowDrawer({
  open,
  onOpenChange,
  agentflow,
}: ExecuteAgentflowDrawerProps) {
  const [resetSignal, setResetSignal] = React.useState(0);

  React.useEffect(() => {
    if (open && agentflow) {
      setResetSignal((prev) => prev + 1);
    }
  }, [open, agentflow]);

  const sessionSeed = React.useMemo(
    () => ({
      revision: `${agentflow?.id ?? "none"}:${resetSignal}`,
      contextId: null,
      messages: [],
      usage: EMPTY_TOKEN_USAGE,
      olderMessagesCursor: null,
      hasOlderMessages: false,
      agentMode: null,
    }),
    [agentflow?.id, resetSignal],
  );

  if (!agentflow) return null;
  const projectId = "11111111-1111-1111-1111-000000000001";

  return (
    <Drawer direction="right" open={open} onOpenChange={onOpenChange} modal={true}>
      <DrawerContent
        className="data-[vaul-drawer-direction=right]:sm:max-w-xl pb-3"
        onPointerDownOutside={(e) => {
          e.preventDefault();
        }}
      >
        <DrawerHeader>
          <div className="flex item-center justify-between">
            <DrawerTitle>Agentflow: {agentflow.name}</DrawerTitle>
            <DrawerClose>
              <X size={20} className="cursor-pointer" />
            </DrawerClose>
          </div>
          <DrawerDescription />
        </DrawerHeader>

        <Chat
          className="h-[calc(100vh-62px)]"
          target={{ id: agentflow.id, type: "agentflow" }}
          projectId={projectId}
          conversationId={null}
          active={open}
          sessionSeed={sessionSeed}
          placeholder="请输入要发送给 agentflow 的内容..."
        />
      </DrawerContent>
    </Drawer>
  );
}
