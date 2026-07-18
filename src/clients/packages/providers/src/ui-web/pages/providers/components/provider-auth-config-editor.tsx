"use client";

import { Plus, Trash2 } from "lucide-react";

import { Button } from "@agw/components";
import { Input } from "@agw/components";
import { Label } from "@agw/components";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@agw/components";
import { Switch } from "@agw/components";

import type { ProviderAuthConfigRequest, ProviderAuthType } from "./types";

const authTypeOptions: ProviderAuthType[] = ["ApiKey"];

interface ProviderAuthConfigEditorProps {
  value: ProviderAuthConfigRequest[];
  onChange: (nextValue: ProviderAuthConfigRequest[]) => void;
}

export function ProviderAuthConfigEditor({ value, onChange }: ProviderAuthConfigEditorProps) {
  const updateConfig = (index: number, patch: Partial<ProviderAuthConfigRequest>) => {
    onChange(value.map((item, idx) => (idx === index ? { ...item, ...patch } : item)));
  };

  return (
    <div className="grid gap-3">
      <div className="flex items-center justify-between">
        <Label>Provider auth configs</Label>
        <Button
          type="button"
          size="sm"
          variant="outline"
          onClick={() =>
            onChange([
              ...value,
              {
                authType: "ApiKey",
                apiKey: "",
                envKey: null,
                enable: true,
              },
            ])
          }
        >
          <Plus className="mr-1 h-4 w-4" />
        </Button>
      </div>

      {value.length === 0 ? (
        <div className="text-sm text-muted-foreground">
          No auth config. Add one if this provider requires authentication.
        </div>
      ) : (
        value.map((config, index) => (
          <div key={index} className="grid gap-3 rounded-md border p-3">
            <div className="grid grid-cols-[1fr_auto] gap-2">
              <div className="grid gap-2">
                <Label>Auth type</Label>
                <Select
                  value={config.authType}
                  onValueChange={(nextType) =>
                    updateConfig(index, {
                      authType: nextType as ProviderAuthType,
                      apiKey: config.apiKey ?? "",
                      envKey: null,
                    })
                  }
                >
                  <SelectTrigger>
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {authTypeOptions.map((option) => (
                      <SelectItem key={option} value={option}>
                        {option}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <Button
                type="button"
                variant="destructive"
                size="icon"
                className="self-end"
                onClick={() => onChange(value.filter((_, idx) => idx !== index))}
              >
                <Trash2 className="h-4 w-4" />
              </Button>
            </div>

            <div className="grid gap-2">
              <Label>API key / bearer token</Label>
              <Input
                value={config.apiKey ?? ""}
                onChange={(e) => updateConfig(index, { apiKey: e.target.value })}
                placeholder="sk-..."
              />
            </div>

            <div className="flex items-center gap-2">
              <Switch
                checked={config.enable}
                onCheckedChange={(checked) => updateConfig(index, { enable: checked })}
              />
              <Label>Enabled</Label>
            </div>
          </div>
        ))
      )}
    </div>
  );
}
