"use client";

import * as React from "react";

import type { ProjectDetails } from "./project-details";
import type { ChatTargetOption } from "@agw/api";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import {
  Select,
  SelectContent,
  SelectGroup,
  SelectItem,
  SelectLabel,
  SelectTrigger,
  SelectValue,
} from "@agw/components";
import { Textarea } from "@agw/components";
import {
  CREATE_TASK_DIALOG_DESCRIPTION,
  CREATE_TASK_DIALOG_TITLE,
  CREATE_TASK_PROMPT_HELPER_TEXT,
  createDefaultTaskJobName,
} from "./project-details";
import { getTargetValue } from "@agw/api";

type CreateTaskDialogProps = {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  project: ProjectDetails | undefined;
  targetOptions: ChatTargetOption[];
  targetsError: string | null;
  areTargetsReady: boolean;
  isSubmitting: boolean;
  onSubmit: (values: { jobName: string; prompt: string; targetValue: string }) => void;
};

export function CreateTaskDialog({
  open,
  onOpenChange,
  project,
  targetOptions,
  targetsError,
  areTargetsReady,
  isSubmitting,
  onSubmit,
}: CreateTaskDialogProps) {
  const [targetValue, setTargetValue] = React.useState("");
  const [jobName, setJobName] = React.useState("");
  const [prompt, setPrompt] = React.useState("");

  React.useEffect(() => {
    if (!open) {
      setTargetValue("");
      setJobName("");
      setPrompt("");
      return;
    }

    setTargetValue("");
    setJobName(createDefaultTaskJobName());
    setPrompt("");
  }, [open]);

  const canSubmit =
    Boolean(project) &&
    areTargetsReady &&
    !targetsError &&
    targetValue.trim().length > 0 &&
    jobName.trim().length > 0 &&
    prompt.trim().length > 0 &&
    !isSubmitting;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>{CREATE_TASK_DIALOG_TITLE}</DialogTitle>
          <DialogDescription>{CREATE_TASK_DIALOG_DESCRIPTION}</DialogDescription>
        </DialogHeader>

        <div className="grid gap-4">
          <div className="space-y-2">
            <Label htmlFor="create-task-target">Project</Label>
            <div className="border rounded-md px-3 py-2 mt-2 space-y-1">
              <div className="text-muted-foreground">{project?.name ?? "Loading project..."}</div>
              <div className="font-mono text-xs text-muted-foreground">{project?.id ?? "-"}</div>
            </div>
          </div>

          <div className="space-y-2">
            <Label htmlFor="create-task-target">Agent / Agentflow</Label>
            <Select value={targetValue || undefined} onValueChange={setTargetValue}>
              <SelectTrigger id="create-task-target" className="w-full">
                <SelectValue placeholder="Select agent or agentflow" />
              </SelectTrigger>
              <SelectContent position="popper" side="bottom" align="start" sideOffset={4}>
                <SelectGroup>
                  <SelectLabel>Agent</SelectLabel>
                  {targetOptions
                    .filter((option) => option.type === "agent")
                    .map((option) => (
                      <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                        {option.label}
                      </SelectItem>
                    ))}
                </SelectGroup>
                <SelectGroup>
                  <SelectLabel>Agentflow</SelectLabel>
                  {targetOptions
                    .filter((option) => option.type === "agentflow")
                    .map((option) => (
                      <SelectItem key={getTargetValue(option)} value={getTargetValue(option)}>
                        {option.label}
                      </SelectItem>
                    ))}
                </SelectGroup>
              </SelectContent>
            </Select>
            {targetsError ? <p className="text-xs text-destructive">{targetsError}</p> : null}
          </div>

          <div className="space-y-2">
            <Label htmlFor="create-task-job-name">Job Name</Label>
            <Input
              id="create-task-job-name"
              value={jobName}
              onChange={(event) => setJobName(event.target.value)}
            />
          </div>

          <div className="space-y-2">
            <Label htmlFor="create-task-prompt">Prompt</Label>
            <Textarea
              id="create-task-prompt"
              rows={6}
              value={prompt}
              onChange={(event) => setPrompt(event.target.value)}
            />
            <p className="text-xs text-muted-foreground">{CREATE_TASK_PROMPT_HELPER_TEXT}</p>
          </div>
        </div>

        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            Cancel
          </Button>
          <Button
            type="button"
            disabled={!canSubmit}
            onClick={() => onSubmit({ jobName, prompt, targetValue })}
          >
            {isSubmitting ? "Creating..." : "Create Task"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
