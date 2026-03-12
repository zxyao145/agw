"use client";

import * as React from "react";

import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription as UiDialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle as UiDialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";

type TaskTargetOption = {
  id: string;
  name: string;
  enable: boolean;
  type: "agentflow" | "agent";
};

type CreateTaskValues = {
  target: string;
  description: string;
  input: string;
};

type CreateTaskDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  targets: TaskTargetOption[];
  isTargetsLoading: boolean;
  targetsErrorMessage: string | null;
  values: CreateTaskValues;
  onChange: (values: CreateTaskValues) => void;
  onCreate: (values: CreateTaskValues) => void;
  isCreating: boolean;
};

export function CreateTaskDialog({
  open,
  onOpenChange,
  targets,
  isTargetsLoading,
  targetsErrorMessage,
  values,
  onChange,
  onCreate,
  isCreating,
}: CreateTaskDialogProps) {
  const enabledTargets = React.useMemo(
    () => targets.filter((w) => w.enable),
    [targets]
  );
  const enabledAgentflows = React.useMemo(
    () => enabledTargets.filter((w) => w.type === "agentflow"),
    [enabledTargets]
  );
  const enabledAgents = React.useMemo(
    () => enabledTargets.filter((w) => w.type === "agent"),
    [enabledTargets]
  );

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <Button
        size="sm"
        onClick={() => {
          onOpenChange(true);
        }}
      >
        Create task
      </Button>
      <DialogContent>
        <DialogHeader>
          <UiDialogTitle>Create task</UiDialogTitle>
          <UiDialogDescription>
            Create a task under this project. Tasks execute asynchronously
            (scheduler will pick it up).
          </UiDialogDescription>
        </DialogHeader>

        {isTargetsLoading && enabledTargets.length === 0 ? (
          <div className="text-sm text-muted-foreground">Loading targets...</div>
        ) : targetsErrorMessage && enabledTargets.length === 0 ? (
          <div className="text-sm text-destructive">
            Failed to load targets: {targetsErrorMessage}
          </div>
        ) : enabledTargets.length === 0 ? (
          <div className="text-sm text-muted-foreground">
            No enabled agents or agentflows available.
          </div>
        ) : (
          <div className="grid gap-4">
            <div className="grid gap-2">
              <Label htmlFor="task-target">Execution target</Label>
              <select
                id="task-target"
                value={values.target}
                onChange={(e) =>
                  onChange({ ...values, target: e.target.value })
                }
                className="h-9 w-full rounded-md border bg-transparent px-3 text-sm shadow-sm"
              >
                <option value="" disabled>
                  Select an agentflow or agent...
                </option>
                {enabledAgentflows.length > 0 ? (
                  <optgroup label="Agentflows">
                    {enabledAgentflows.map((w) => (
                      <option key={`agentflow:${w.id}`} value={`agentflow:${w.id}`}>
                        {w.name}
                      </option>
                    ))}
                  </optgroup>
                ) : null}
                {enabledAgents.length > 0 ? (
                  <optgroup label="Agents">
                    {enabledAgents.map((w) => (
                      <option key={`agent:${w.id}`} value={`agent:${w.id}`}>
                        {w.name}
                      </option>
                    ))}
                  </optgroup>
                ) : null}
              </select>
              <div className="text-xs text-muted-foreground">
                Only enabled targets are shown.
              </div>
            </div>

            {targetsErrorMessage ? (
              <div className="text-xs text-amber-600 dark:text-amber-400">
                Some targets may be missing: {targetsErrorMessage}
              </div>
            ) : null}

            <div className="grid gap-2">
              <Label htmlFor="task-description">Description</Label>
              <Input
                id="task-description"
                value={values.description}
                onChange={(e) =>
                  onChange({ ...values, description: e.target.value })
                }
                placeholder="What to do"
              />
            </div>

            <div className="grid gap-2">
              <Label htmlFor="task-input">Input</Label>
              <Textarea
                id="task-input"
                value={values.input}
                onChange={(e) => onChange({ ...values, input: e.target.value })}
                placeholder="Task input"
                rows={6}
              />
            </div>
          </div>
        )}

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button
            type="button"
            onClick={() => onCreate(values)}
            disabled={
              isTargetsLoading ||
              !values.target ||
              !values.description.trim() ||
              !values.input.trim() ||
              isCreating
            }
          >
            {isCreating ? "Creating..." : "Create"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
