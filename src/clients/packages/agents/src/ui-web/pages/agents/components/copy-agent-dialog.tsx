import * as React from "react";
import type { UseMutationResult } from "@agw/components/query";
import { getApiErrorMessage } from "@agw/api";
import {
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
} from "@agw/components";

import { createAgentCopyRequest } from "../../copy-requests";
import type { AgentCreateRequest, AgentDto } from "./types";

interface CopyAgentDialogProps {
  agent: AgentDto;
  onClose: () => void;
  copyAgentMutation: UseMutationResult<unknown, Error, AgentCreateRequest, unknown>;
}

export function CopyAgentDialog({ agent, onClose, copyAgentMutation }: CopyAgentDialogProps) {
  const [request, setRequest] = React.useState(() => createAgentCopyRequest(agent));
  const nameInputRef = React.useRef<HTMLInputElement>(null);
  const isPending = copyAgentMutation.isPending;

  const updateField = (field: "name" | "displayName", value: string) => {
    setRequest((current) => ({ ...current, [field]: value }));
    if (copyAgentMutation.error) copyAgentMutation.reset();
  };

  return (
    <Dialog open onOpenChange={(open) => !open && !isPending && onClose()}>
      <DialogContent
        size="sm"
        showCloseButton={!isPending}
        onOpenAutoFocus={(event) => {
          event.preventDefault();
          nameInputRef.current?.focus();
          nameInputRef.current?.select();
        }}
      >
        <form
          className="space-y-5"
          onSubmit={(event) => {
            event.preventDefault();
            if (isPending || !request.name.trim()) return;
            copyAgentMutation.mutate({
              ...request,
              name: request.name.trim(),
              displayName: request.displayName.trim(),
            });
          }}
        >
          <DialogHeader>
            <DialogTitle>Copy agent</DialogTitle>
            <DialogDescription>
              Choose a unique name for the copy. The name cannot be changed after creation.
            </DialogDescription>
          </DialogHeader>

          <div className="grid gap-2">
            <Label htmlFor="copy-agent-name">Name</Label>
            <Input
              ref={nameInputRef}
              id="copy-agent-name"
              value={request.name}
              onChange={(event) => updateField("name", event.target.value)}
              required
              maxLength={200}
              disabled={isPending}
            />
          </div>
          <div className="grid gap-2">
            <Label htmlFor="copy-agent-display-name">Display Name (Optional)</Label>
            <Input
              id="copy-agent-display-name"
              value={request.displayName}
              onChange={(event) => updateField("displayName", event.target.value)}
              maxLength={200}
              disabled={isPending}
            />
          </div>

          {copyAgentMutation.error ? (
            <p role="alert" className="text-sm text-destructive">
              {getApiErrorMessage(copyAgentMutation.error)}
            </p>
          ) : null}

          <DialogFooter>
            <Button type="button" variant="outline" disabled={isPending} onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" disabled={isPending || !request.name.trim()}>
              {isPending ? "Copying..." : "Copy agent"}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
