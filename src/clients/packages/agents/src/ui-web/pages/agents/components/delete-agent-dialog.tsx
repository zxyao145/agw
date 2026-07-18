import * as React from "react";
import { UseMutationResult } from "@agw/components/query";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import type { AgentDto } from "./types";

interface DeleteAgentDialogProps {
  open: boolean;
  setOpen: (open: boolean) => void;
  deletingAgent: AgentDto | null;
  deleteAgentMutation: UseMutationResult<unknown, Error, string, unknown>;
}

export function DeleteAgentDialog({
  open,
  setOpen,
  deletingAgent,
  deleteAgentMutation,
}: DeleteAgentDialogProps) {
  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogContent size="sm">
        <DialogHeader>
          <DialogTitle>Delete agent</DialogTitle>
          <DialogDescription>
            Are you sure you want to delete agent &quot;{deletingAgent?.name}
            &quot;? This action cannot be undone.
          </DialogDescription>
        </DialogHeader>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button
            type="button"
            variant="destructive"
            onClick={() => {
              if (deletingAgent) {
                deleteAgentMutation.mutate(deletingAgent.id);
              }
            }}
            disabled={deleteAgentMutation.isPending}
          >
            {deleteAgentMutation.isPending ? "Deleting..." : "Delete"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
