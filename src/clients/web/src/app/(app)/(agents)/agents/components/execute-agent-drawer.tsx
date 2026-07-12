import * as React from "react";
import {
  Drawer,
  DrawerClose,
  DrawerContent,
  DrawerDescription,
  DrawerHeader,
  DrawerTitle,
} from "@/components/ui/drawer";
import { X } from "lucide-react";
import type { AgentDto } from "./types";
import { Chat } from "@/components/message/chat";

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

  if (!executingAgent) return null;
  const projectId = `11111111-1111-1111-1111-000000000001`;

  return (
    <Drawer direction="right" open={open} onOpenChange={setOpen} modal={true}>
      <DrawerContent
        className="data-[vaul-drawer-direction=right]:sm:max-w-xl"
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
          className="px-4 pb-4 h-[calc(100vh-62px)]"
          targetId={executingAgent.id}
          agentType={0}
          projectId={projectId}
          active={open}
          resetSignal={`${executingAgent.id}:${resetSignal}`}
          placeholder="请输入要发送给 agent 的内容..."
        />
      </DrawerContent>
    </Drawer>
  );
}
