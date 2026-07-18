"use client";

import * as React from "react";
import { Settings, TriangleAlert, Plus, Trash2 } from "lucide-react";
import { toast } from "sonner";
import { Autocomplete } from "@agw/components";
import { Button } from "@agw/components";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { RadioGroup, RadioGroupItem } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";
import { Tooltip, TooltipContent, TooltipTrigger } from "@agw/components";
import type { ClaudeSettingsStorageValues, EnvVar, SettingsDialogProps } from "./type";
import { DirectoryMode, PermissionMode } from "./type";
import { claudeSettingsStorage } from "./settings-storage";

export function SettingsDialog({
  workingDirectory,
  setWorkingDirectory,
  gitAddress,
  setGitAddress,
  directoryMode,
  setDirectoryMode,

  apiKey,
  setApiKey,

  apiBaseUrl,
  setApiBaseUrl,

  permissionMode,
  setPermissionMode,

  envVars,
  setEnvVars,
}: SettingsDialogProps) {
  const [open, setOpen] = React.useState(false);
  const [workingDirHistory, setWorkingDirHistory] = React.useState<string[]>([]);
  const [gitAddressHistory, setGitAddressHistory] = React.useState<string[]>([]);

  // Load env vars on mount
  React.useEffect(() => {
    if (open) {
      const {
        envVars: savedEnvVars,
        workingDirHistory,
        gitAddressHistory,
      } = claudeSettingsStorage.get();
      if (savedEnvVars) {
        setEnvVars(savedEnvVars);
      }
      if (workingDirHistory) {
        setWorkingDirHistory(workingDirHistory);
      }
      if (gitAddressHistory) {
        setGitAddressHistory(gitAddressHistory);
      }
    }
  }, [open]);

  // Persist a recent history list (working dir or git address) while de-duplicating and capping entries.
  const updateHistory = React.useCallback(
    (
      value: string,
      storageKey: keyof Pick<
        ClaudeSettingsStorageValues,
        "workingDirHistory" | "gitAddressHistory"
      >,
      setHistory: React.Dispatch<React.SetStateAction<string[]>>,
    ) => {
      const trimmed = value.trim();
      if (!trimmed) {
        return;
      }
      setHistory((prev) => {
        const next = [trimmed, ...prev.filter((item) => item !== trimmed)].slice(0, 20);
        const storageUpdate: ClaudeSettingsStorageValues =
          storageKey === "workingDirHistory"
            ? { workingDirHistory: next }
            : { gitAddressHistory: next };
        claudeSettingsStorage.set(storageUpdate);
        return next;
      });
    },
    [],
  );

  const saveSettings = () => {
    claudeSettingsStorage.set({
      workingDirectory,
      gitAddress,
      directoryMode,
      apiKey,
      apiBaseUrl,
      permissionMode,
      envVars,
    });
    updateHistory(workingDirectory, "workingDirHistory", setWorkingDirHistory);
    updateHistory(gitAddress, "gitAddressHistory", setGitAddressHistory);
    setOpen(false);
    toast.success("Settings saved");
  };

  const addEnvVar = () => {
    setEnvVars([...envVars, { key: "", value: "" }]);
  };

  const removeEnvVar = (index: number) => {
    setEnvVars(envVars.filter((_, i) => i !== index));
  };

  const updateEnvVar = (index: number, field: keyof EnvVar, value: string) => {
    const updated = [...envVars];
    updated[index][field] = value;
    setEnvVars(updated);
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm" className="cursor-pointer">
          <Settings className="w-4 h-4" />
        </Button>
      </DialogTrigger>

      <DialogContent size="xl" className="max-h-[90vh] overflow-y-auto agw-scrollbar">
        <DialogHeader>
          <DialogTitle>Settings</DialogTitle>
          <DialogDescription>Configure settings</DialogDescription>
        </DialogHeader>

        <div className="grid gap-3 py-2">
          <div className="grid gap-2">
            <Label>Input Source</Label>
            <RadioGroup
              value={directoryMode}
              onValueChange={(value) => setDirectoryMode(value as DirectoryMode)}
              className="flex flex-row gap-4"
            >
              <div className="flex items-center space-x-2">
                <RadioGroupItem value={DirectoryMode.workingDirectory} id="working-dir" />
                <Label htmlFor="working-dir" className="font-normal cursor-pointer">
                  Working Directory
                </Label>
              </div>
              <div className="flex items-center space-x-2">
                <RadioGroupItem value={DirectoryMode.gitAddress} id="git-addr" />
                <Label htmlFor="git-addr" className="font-normal cursor-pointer">
                  Git Address
                </Label>
              </div>
            </RadioGroup>
          </div>

          {directoryMode === DirectoryMode.workingDirectory ? (
            <div className="grid gap-2">
              <Label htmlFor="settings-workingDir">Working Directory (Optional)</Label>
              <Autocomplete
                id="settings-workingDir"
                value={workingDirectory}
                onChange={(e) => setWorkingDirectory(e.target.value)}
                placeholder="e.g., /path/to/project"
                options={workingDirHistory}
              />
            </div>
          ) : (
            <div className="grid gap-2">
              <Label htmlFor="settings-gitAddress">Git Address</Label>
              <Autocomplete
                id="settings-gitAddress"
                value={gitAddress}
                onChange={(e) => setGitAddress(e.target.value)}
                placeholder="e.g., https://github.com/org/repo.git"
                options={gitAddressHistory}
              />
            </div>
          )}

          <div className="grid gap-2">
            <Label htmlFor="settings-permissionMode">Permission Mode</Label>
            <Select value={permissionMode} onValueChange={setPermissionMode}>
              <SelectTrigger id="settings-permissionMode" className="w-full">
                <SelectValue placeholder="Select permission mode" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={PermissionMode.default}>
                  <div className="flex flex-col items-start">
                    <span className="font-medium">Default</span>
                    <span className="text-xs text-muted-foreground">
                      Prompt for confirmation before executing actions
                    </span>
                  </div>
                </SelectItem>
                <SelectItem value={PermissionMode.acceptEdits}>
                  <div className="flex flex-col items-start">
                    <span className="font-medium">Accept Edits</span>
                    <span className="text-xs text-muted-foreground">
                      Automatically accept file edits without confirmation
                    </span>
                  </div>
                </SelectItem>
                <SelectItem value={PermissionMode.plan}>
                  <div className="flex flex-col items-start">
                    <span className="font-medium">Plan</span>
                    <span className="text-xs text-muted-foreground">
                      Generate execution plan before taking action
                    </span>
                  </div>
                </SelectItem>
                <SelectItem value={PermissionMode.bypassPermissions}>
                  <div className="flex flex-col items-start">
                    <span className="font-medium">Bypass Permissions</span>
                    <span className="text-xs text-muted-foreground">
                      Execute all actions without any confirmation (use with caution)
                    </span>
                  </div>
                </SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div className="grid gap-2">
            <Label htmlFor="settings-apiKey">
              Anthropic API Key (Optional)
              <Tooltip>
                <TooltipTrigger asChild>
                  <TriangleAlert className="w-4 h-4 text-red-400" />
                </TooltipTrigger>
                <TooltipContent>
                  <p className="text-red-400">
                    WARNING: The API key will be transmitted over the network
                  </p>
                </TooltipContent>
              </Tooltip>
            </Label>
            <Input
              id="settings-apiKey"
              type="password"
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder="sk-ant-..."
            />
          </div>

          <div className="grid gap-2">
            <Label htmlFor="settings-apiBaseUrl">Anthropic API Base URL (Optional)</Label>
            <Input
              id="settings-apiBaseUrl"
              value={apiBaseUrl}
              onChange={(e) => setApiBaseUrl(e.target.value)}
              placeholder="https://api.anthropic.com"
            />
          </div>

          <div className="grid gap-2">
            <div className="flex items-center justify-between">
              <Label>Environment Variables</Label>
              <Button
                type="button"
                variant="ghost"
                size="sm"
                onClick={addEnvVar}
                className="h-7 px-2"
              >
                <Plus className="w-4 h-4 mr-1" />
                Add
              </Button>
            </div>
            {envVars.length === 0 ? (
              <div className="text-sm text-muted-foreground py-2">No environment variables set</div>
            ) : (
              <div className="border rounded-md">
                <div className="grid grid-cols-12 gap-2 p-2 border-b bg-muted/50 text-xs font-medium text-muted-foreground">
                  <div className="col-span-5">Key</div>
                  <div className="col-span-6">Value</div>
                  <div className="col-span-1"></div>
                </div>
                {envVars.map((env, index) => (
                  <div key={index} className="grid grid-cols-12 gap-2 p-2 border-b last:border-b-0">
                    <Input
                      value={env.key}
                      onChange={(e) => updateEnvVar(index, "key", e.target.value)}
                      placeholder="KEY"
                      className="col-span-5 h-8 text-xs md:text-xs"
                    />
                    <Input
                      value={env.value}
                      onChange={(e) => updateEnvVar(index, "value", e.target.value)}
                      placeholder="value"
                      className="col-span-6 h-8 text-xs md:text-xs"
                    />
                    <div className="col-span-1 flex items-center">
                      <Button
                        type="button"
                        variant="ghost"
                        size="sm"
                        onClick={() => removeEnvVar(index)}
                        className="h-8 w-8 p-0"
                      >
                        <Trash2 className="w-4 h-4" />
                      </Button>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button type="button" variant="outline">
              Cancel
            </Button>
          </DialogClose>
          <Button type="button" onClick={saveSettings}>
            Save Settings
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
