"use client";

import * as React from "react";
import { toast } from "sonner";

import { getApiKey, setApiKey } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";

export default function SettingsPage() {
  const [value, setValue] = React.useState("");
  const [hydrated, setHydrated] = React.useState(false);

  React.useEffect(() => {
    setValue(getApiKey() ?? "");
    setHydrated(true);
  }, []);

  const handleSave = React.useCallback(() => {
    const trimmed = value.trim();
    setApiKey(trimmed.length > 0 ? trimmed : null);
    toast.success(trimmed.length > 0 ? "API key saved" : "API key cleared");
  }, [value]);

  const handleClear = React.useCallback(() => {
    setValue("");
    setApiKey(null);
    toast.success("API key cleared");
  }, []);

  return (
    <div className="w-full max-w-2xl py-6">
      <h1 className="mb-1 text-2xl font-semibold">Settings</h1>
      <p className="mb-6 text-sm text-muted-foreground">
        Configure local browser settings for this Agw admin UI.
      </p>

      <section className="rounded-lg border p-5">
        <Label htmlFor="api-key" className="text-base font-medium">
          API key
        </Label>
        <p className="mt-1 mb-3 text-sm text-muted-foreground">
          If the backend was initialized with an API key, every <code>/api</code> request must
          include an <code>X-API-Key</code> header matching that value. The key is stored in this
          browser&apos;s <code>localStorage</code>.
        </p>
        <Input
          id="api-key"
          type="password"
          autoComplete="off"
          placeholder="Enter your API key"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          disabled={!hydrated}
        />
        <div className="mt-4 flex gap-2">
          <Button onClick={handleSave} disabled={!hydrated}>
            Save
          </Button>
          <Button variant="outline" onClick={handleClear} disabled={!hydrated}>
            Clear
          </Button>
        </div>
      </section>
    </div>
  );
}
