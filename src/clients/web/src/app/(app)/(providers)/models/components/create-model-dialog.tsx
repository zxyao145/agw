"use client";

import * as React from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";

import { apiPost } from "@/api/client";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

import type { ModelCreateRequest } from "./types";
import { parseIntOrNull } from "./utils";
import { getApiErrorMessage } from "@/api/utils";

interface CreateModelDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CreateModelDialog({ open, onOpenChange }: CreateModelDialogProps) {
  const queryClient = useQueryClient();

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [maxTokens, setMaxTokens] = React.useState("4096");

  const createModelMutation = useMutation({
    mutationFn: async (body: ModelCreateRequest) => {
      return await apiPost("/api/models", { body });
    },
    onSuccess: async () => {
      toast.success("Model created");
      onOpenChange(false);
      setName("");
      setDescription("");
      setMaxTokens("4096");
      await queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const parsedMaxTokens = parseIntOrNull(maxTokens);

  const createDisabled = !name.trim() || parsedMaxTokens === null || createModelMutation.isPending;

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
            <Label htmlFor="maxTokens">Max tokens (int)</Label>
            <Input
              id="maxTokens"
              inputMode="numeric"
              value={maxTokens}
              onChange={(e) => setMaxTokens(e.target.value)}
              placeholder="4096"
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
                maxTokens: parsedMaxTokens ?? 0,
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
