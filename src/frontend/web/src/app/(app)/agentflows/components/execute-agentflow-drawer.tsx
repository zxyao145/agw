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
import type { AgentflowDto } from "@/types/agentflow";
import { Conversation } from "@/components/message/conversation";

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

  if (!agentflow) return null;
  const projectId = `agentflow-${agentflow.id}`;

  return (
    <Drawer
      direction="right"
      open={open}
      onOpenChange={onOpenChange}
      modal={true}
    >
      <DrawerContent
        className="data-[vaul-drawer-direction=right]:sm:max-w-xl"
        onPointerDownOutside={(e) => {
          e.preventDefault();
        }}
      >
        <DrawerHeader>
          <div className="flex item-center justify-between">
            <DrawerTitle>
              Agentflow: {agentflow.name}
            </DrawerTitle>
            <DrawerClose>
              <X size={20} className="cursor-pointer" />
            </DrawerClose>
          </div>
          <DrawerDescription />
        </DrawerHeader>

        <Conversation
          className="px-4 pb-4 h-[calc(100vh-62px)]"
          executionId={agentflow.id}
          agentType={0}
          projectId={projectId}
          resetSignal={`${agentflow?.id ?? "none"}:${resetSignal}`}
          placeholder="请输入要发送给 agentflow 的内容..."
        />
      </DrawerContent>
    </Drawer>
  );
}
