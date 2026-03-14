"use client";

import { Plus, Trash2 } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";

import type { ProviderAuthConfigRequest, ProviderAuthType } from "./types";

const authTypeOptions: ProviderAuthType[] = ["ApiKey", "EnvVariable"];

interface ProviderAuthConfigEditorProps {
  value: ProviderAuthConfigRequest[];
  onChange: (nextValue: ProviderAuthConfigRequest[]) => void;
}

export function ProviderAuthConfigEditor({
  value,
  onChange,
}: ProviderAuthConfigEditorProps) {
  const updateConfig = (
    index: number,
    patch: Partial<ProviderAuthConfigRequest>,
  ) => {
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
                      apiKey: nextType === "ApiKey" ? config.apiKey ?? "" : null,
                      envKey:
                        nextType === "EnvVariable" ? config.envKey ?? "" : null,
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

            {config.authType === "ApiKey" ? (
              <div className="grid gap-2">
                <Label>API key / bearer token</Label>
                <Input
                  value={config.apiKey ?? ""}
                  onChange={(e) => updateConfig(index, { apiKey: e.target.value })}
                  placeholder="sk-..."
                />
              </div>
            ) : (
              <div className="grid gap-2">
                <Label>Environment variable Name (get api key from this variable)</Label>
                <Input
                  value={config.envKey ?? ""}
                  onChange={(e) => updateConfig(index, { envKey: e.target.value })}
                  placeholder="OPENAI_API_KEY"
                />
              </div>
            )}

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
