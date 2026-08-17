"use client";

import * as React from "react";
import { apiPut, getApiErrorMessage } from "@agw/api";
import {
  Button,
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Input,
  Label,
  Textarea,
} from "@agw/components";
import { useMutation, useQueryClient } from "@agw/components/query";
import { applyDialogOpenChange } from "@agw/integrations";
import { toast } from "sonner";

import { ModelTokenLimitFields } from "./model-token-limit-fields";
import { getModelTokenLimitError, type ModelDto, type ModelUpdateRequest } from "./types";

interface EditModelDialogProps {
  model: ModelDto | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function EditModelDialog({ model, open, onOpenChange }: EditModelDialogProps) {
  const queryClient = useQueryClient();
  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [maxContextWindowTokens, setMaxContextWindowTokens] = React.useState("");
  const [maxOutputTokens, setMaxOutputTokens] = React.useState("");

  React.useEffect(() => {
    if (!open || !model) {
      return;
    }

    setName(model.name);
    setDescription(model.description ?? "");
    setMaxContextWindowTokens(String(model.maxContextWindowTokens));
    setMaxOutputTokens(String(model.maxOutputTokens));
  }, [model, open]);

  const updateModelMutation = useMutation({
    mutationFn: async (body: ModelUpdateRequest) => {
      if (!model) {
        throw new Error("Model is required");
      }

      return await apiPut("/api/models/{id}", {
        params: { path: { id: model.id } },
        body,
      });
    },
    onSuccess: async () => {
      toast.success("Model updated");
      onOpenChange(false);
      await queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (error) => {
      toast.error(`Update failed: ${getApiErrorMessage(error)}`);
    },
  });

  const contextWindow = Number(maxContextWindowTokens);
  const maximumOutput = Number(maxOutputTokens);
  const tokenLimitError = getModelTokenLimitError(contextWindow, maximumOutput);
  const updateDisabled =
    model === null || !name.trim() || tokenLimitError !== null || updateModelMutation.isPending;

  return (
    <Dialog
      open={open}
      onOpenChange={(nextOpen) =>
        applyDialogOpenChange({
          isPending: updateModelMutation.isPending,
          nextOpen,
          setOpen: onOpenChange,
        })
      }
    >
      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>Edit model</DialogTitle>
          <DialogDescription>
            Keep these limits aligned with the model provider's published specifications.
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          <div className="grid gap-2">
            <Label htmlFor="edit-model-name">Name</Label>
            <Input
              id="edit-model-name"
              value={name}
              onChange={(event) => setName(event.target.value)}
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="edit-model-description">Description</Label>
            <Textarea
              id="edit-model-description"
              value={description}
              onChange={(event) => setDescription(event.target.value)}
              rows={3}
            />
          </div>

          <ModelTokenLimitFields
            idPrefix="edit-model-"
            maxContextWindowTokens={maxContextWindowTokens}
            maxOutputTokens={maxOutputTokens}
            onMaxContextWindowTokensChange={setMaxContextWindowTokens}
            onMaxOutputTokensChange={setMaxOutputTokens}
          />
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline" disabled={updateModelMutation.isPending}>
              Cancel
            </Button>
          </DialogClose>
          <Button
            type="button"
            disabled={updateDisabled}
            onClick={() =>
              updateModelMutation.mutate({
                name: name.trim(),
                description: description.trim() || null,
                maxContextWindowTokens: contextWindow,
                maxOutputTokens: maximumOutput,
              })
            }
          >
            {updateModelMutation.isPending ? "Updating..." : "Update"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
