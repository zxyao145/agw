"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@agw/components/query";
import { toast } from "sonner";

import { apiPost } from "@agw/api";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Textarea } from "@agw/components";

import type { ModelCreateRequest } from "./types";
import { getApiErrorMessage } from "@agw/api";

interface CreateModelDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CreateModelDialog({ open, onOpenChange }: CreateModelDialogProps) {
  const queryClient = useQueryClient();

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");

  const createModelMutation = useMutation({
    mutationFn: async (body: ModelCreateRequest) => {
      return await apiPost("/api/models", { body });
    },
    onSuccess: async () => {
      toast.success("Model created");
      onOpenChange(false);
      setName("");
      setDescription("");
      await queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const createDisabled = !name.trim() || createModelMutation.isPending;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>Create model</DialogTitle>
          {/* <UiDialogDescription>
            Uses <code>/api/models</code> with{" "}
            <code>ModelCreateRequest</code>.
          </UiDialogDescription> */}
        </DialogHeader>

        <div className="grid gap-4">
          <div className="grid gap-2">
            <Label htmlFor="name">Name</Label>
            <Input
              id="name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="gpt-4o-mini"
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="description">Description</Label>
            <Textarea
              id="description"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              rows={3}
            />
          </div>
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>

          <Button
            type="button"
            disabled={createDisabled}
            onClick={() =>
              createModelMutation.mutate({
                name,
                description: description.length ? description : null,
                maxTokens: 4096,
              })
            }
          >
            {createModelMutation.isPending ? "Creating..." : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
