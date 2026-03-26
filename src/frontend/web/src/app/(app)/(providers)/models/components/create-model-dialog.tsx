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
import { Checkbox } from "@/components/ui/checkbox";

import { MODEL_TYPE_OPTIONS, getModelTypeLabel } from "./types";
import type { ModelCreateRequest } from "./types";
import { getApiErrorMessage, parseIntOrNull } from "./utils";

interface CreateModelDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

export function CreateModelDialog({ open, onOpenChange }: CreateModelDialogProps) {
  const queryClient = useQueryClient();

  const [name, setName] = React.useState("");
  const [description, setDescription] = React.useState("");
  const [selectedTypes, setSelectedTypes] = React.useState<number>(0);
  const [maxTokens, setMaxTokens] = React.useState("4096");

  const toggleType = (typeValue: number) => {
    setSelectedTypes((prev) => prev ^ typeValue); // XOR to toggle bit
  };

  const isTypeSelected = (typeValue: number) => {
    return (selectedTypes & typeValue) === typeValue;
  };

  const createModelMutation = useMutation({
    mutationFn: async (body: ModelCreateRequest) => {
      return await apiPost("/api/models", { body });
    },
    onSuccess: async () => {
      toast.success("Model created");
      onOpenChange(false);
      setName("");
      setDescription("");
      setSelectedTypes(0);
      setMaxTokens("4096");
      await queryClient.invalidateQueries({ queryKey: ["models"] });
    },
    onError: (error) => {
      toast.error(`Create failed: ${getApiErrorMessage(error)}`);
    },
  });

  const parsedMaxTokens = parseIntOrNull(maxTokens);

  const createDisabled =
    !name.trim() ||
    selectedTypes === 0 ||
    parsedMaxTokens === null ||
    createModelMutation.isPending;

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
            <Label>Type (Flags enum - select one or more)</Label>
            <div className="flex items-center gap-4">
              {MODEL_TYPE_OPTIONS.map((option) => (
                <div key={option.value} className="inline-flex items-center space-x-1">
                  <Checkbox
                    id={`type-${option.value}`}
                    checked={isTypeSelected(option.value)}
                    onCheckedChange={() => toggleType(option.value)}
                  />
                  <label
                    htmlFor={`type-${option.value}`}
                    className="text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70 cursor-pointer"
                  >
                    {option.label}
                  </label>
                </div>
              ))}
            </div>
            <p className="text-xs text-muted-foreground">
              Selected value: {selectedTypes} ({getModelTypeLabel(selectedTypes)})
            </p>
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
                type: selectedTypes,
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
