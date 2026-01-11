"use client";

import * as React from "react";
import { Settings, TriangleAlert } from "lucide-react";
import { toast } from "sonner";

import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { PermissionMode } from "../types";
import {
  Tooltip,
  TooltipContent,
  TooltipTrigger,
} from "@/components/ui/tooltip"

interface SettingsDialogProps {
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
}

export function SettingsDialog({
  workingDirectory,
  setWorkingDirectory,
  apiKey,
  setApiKey,
  apiBaseUrl,
  setApiBaseUrl,
  permissionMode,
  setPermissionMode,
}: SettingsDialogProps) {
  const [open, setOpen] = React.useState(false);

  const saveSettings = () => {
    localStorage.setItem("claudecode_workingDir", workingDirectory);
    localStorage.setItem("claudecode_apiKey", apiKey);
    localStorage.setItem("claudecode_apiBaseUrl", apiBaseUrl);
    localStorage.setItem("claudecode_permissionMode", permissionMode);
    setOpen(false);
    toast.success("Settings saved");
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="ghost" size="sm" className="cursor-pointer">
          <Settings className="w-4 h-4" />
        </Button>
      </DialogTrigger>

      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>ClaudeCode Settings</DialogTitle>
          <DialogDescription>
            Configure working directory, API key, and permission mode
          </DialogDescription>
        </DialogHeader>

        <div className="grid gap-4 py-4">
          <div className="grid gap-2">
            <Label htmlFor="settings-workingDir">
              Working Directory (Optional)
            </Label>
            <Input
              id="settings-workingDir"
              value={workingDirectory}
              onChange={(e) => setWorkingDirectory(e.target.value)}
              placeholder="e.g., /path/to/project"
            />
            <p className="text-xs text-muted-foreground">
              Directory where Claude Code will execute. Leave empty for current
              directory.
            </p>
          </div>

          <div className="grid gap-2">
            <Label htmlFor="settings-permissionMode">Permission Mode</Label>
            <Select value={permissionMode} onValueChange={setPermissionMode}>
              <SelectTrigger
                id="settings-permissionMode"
                className="w-full py-6"
              >
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
                      Execute all actions without any confirmation (use with
                      caution)
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
            <Label htmlFor="settings-apiBaseUrl">
              Anthropic API Base URL (Optional)
            </Label>
            <Input
              id="settings-apiBaseUrl"
              value={apiBaseUrl}
              onChange={(e) => setApiBaseUrl(e.target.value)}
              placeholder="https://api.anthropic.com"
            />
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
