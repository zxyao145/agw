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
import type { AgentDto } from "./types";
import { Chat } from "@agw/chat";
import { EMPTY_TOKEN_USAGE } from "@agw/api";

interface ExecuteAgentDrawerProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  executingAgent: AgentDto | null;
}

export function ExecuteAgentDrawer({ open, setOpen, executingAgent }: ExecuteAgentDrawerProps) {
  const [resetSignal, setResetSignal] = React.useState(0);

  React.useEffect(() => {
    if (open && executingAgent) {
      setResetSignal((prev) => prev + 1);
    }
  }, [open, executingAgent]);

  const sessionSeed = React.useMemo(
    () => ({
      revision: `${executingAgent?.id ?? "none"}:${resetSignal}`,
      contextId: null,
      messages: [],
      usage: EMPTY_TOKEN_USAGE,
    }),
    [executingAgent?.id, resetSignal],
  );

  if (!executingAgent) return null;
  const projectId = `11111111-1111-1111-1111-000000000001`;

  return (
    <Drawer direction="right" open={open} onOpenChange={setOpen} modal={true}>
      <DrawerContent
        className="data-[vaul-drawer-direction=right]:sm:max-w-xl pb-3"
        onPointerDownOutside={(e) => {
          e.preventDefault();
        }}
      >
        <DrawerHeader>
          <div className="flex item-center justify-between">
            <DrawerTitle>Agent: {executingAgent?.name}</DrawerTitle>
            <DrawerClose>
              <X size={20} className="cursor-pointer" />
            </DrawerClose>
          </div>
          <DrawerDescription />
        </DrawerHeader>

        <Chat
          className="h-[calc(100vh-62px)]"
          target={{ id: executingAgent.id, type: "agent" }}
          projectId={projectId}
          active={open}
          sessionSeed={sessionSeed}
          placeholder="请输入要发送给 agent 的内容..."
        />
      </DrawerContent>
    </Drawer>
  );
}
