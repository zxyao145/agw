"use client";

import * as React from "react";
import { RotateCcw } from "lucide-react";
import { toast } from "sonner";

import {
  BASE_FONT_SIZES,
  CHAT_FONT_SIZES,
  DEFAULT_APPEARANCE,
  resolveFontSizes,
} from "@desktop/shared/appearance";
import type { AppearanceSettings } from "@desktop/shared/contracts";
import { Button, Card, CardContent, Label } from "@agw/components";

const FOLLOW = "follow";

type SizeFieldProps = {
  id: string;
  label: string;
  description: string;
  value: string;
  sizes: readonly number[];
  followText?: string;
  resetLabel: string;
  onChange: (value: string) => void;
  onReset: () => void;
};

function SizeField({
  id,
  label,
  description,
  value,
  sizes,
  followText,
  resetLabel,
  onChange,
  onReset,
}: SizeFieldProps) {
  return (
    <div className="grid gap-2">
      <Label htmlFor={id}>{label}</Label>
      <div className="flex items-center gap-2">
        <select
          id={id}
          className="h-9 min-w-48 rounded-md border bg-background px-3 text-sm"
          value={value}
          aria-describedby={`${id}-description`}
          onChange={(event) => onChange(event.target.value)}
        >
          {followText ? <option value={FOLLOW}>{followText}</option> : null}
          {sizes.map((size) => (
            <option key={size} value={String(size)}>
              {size} px
            </option>
          ))}
        </select>
        <Button
          type="button"
          size="icon-sm"
          variant="outline"
          aria-label={resetLabel}
          title={resetLabel}
          onClick={onReset}
        >
          <RotateCcw />
        </Button>
      </div>
      <p id={`${id}-description`} className="text-xs text-muted-foreground">
        {description}
      </p>
    </div>
  );
}

const nullableSize = (value: string): number | null => (value === FOLLOW ? null : Number(value));

export function AppearanceSettingsForm({
  appearance,
  save,
}: {
  appearance: AppearanceSettings;
  save: (next: AppearanceSettings) => Promise<void>;
}) {
  const [value, setValue] = React.useState(appearance);
  const savedRef = React.useRef(appearance);
  savedRef.current = appearance;

  React.useEffect(() => {
    setValue(appearance);
  }, [appearance]);

  // 先更新本地状态再保存，保存失败时回到 appearance 属性。
  // Update the local state, then save; a failed save returns to the appearance prop.
  const apply = async (next: AppearanceSettings) => {
    setValue(next);
    try {
      await save(next);
    } catch (error) {
      toast.error(error instanceof Error ? error.message : "Unable to save appearance settings");
      setValue(savedRef.current);
    }
  };

  const sizes = resolveFontSizes(value);

  return (
    <div className="space-y-6">
      <Card className="gap-0 overflow-hidden p-4">
        <CardContent className="space-y-5 px-0">
          <SizeField
            id="appearance-base-font-size"
            label="Base font size"
            description="Scales the Desktop interface."
            value={String(value.baseFontSize)}
            sizes={BASE_FONT_SIZES}
            resetLabel="Reset base font size"
            onChange={(next) => void apply({ ...value, baseFontSize: Number(next) })}
            onReset={() => void apply({ ...value, baseFontSize: DEFAULT_APPEARANCE.baseFontSize })}
          />
          <SizeField
            id="appearance-chat-font-size"
            label="Chat font size"
            description="Used by Chat messages."
            value={value.chatFontSize === null ? FOLLOW : String(value.chatFontSize)}
            sizes={CHAT_FONT_SIZES}
            followText={`Follow base size (${sizes.chat} px)`}
            resetLabel="Reset chat font size"
            onChange={(next) => void apply({ ...value, chatFontSize: nullableSize(next) })}
            onReset={() => void apply({ ...value, chatFontSize: null })}
          />
        </CardContent>
      </Card>

      <Card className="gap-2 p-4">
        <p className="text-xs font-semibold uppercase tracking-[0.12em] text-muted-foreground">
          Chat preview
        </p>
        <div className="agw-prose" data-density="compact" data-appearance-preview="compact">
          <h3>Heading</h3>
          <p>
            Body text with <code>inline code</code> in a Chat message.
          </p>
          <ul>
            <li>First item</li>
            <li>Second item</li>
          </ul>
        </div>
      </Card>

      <div className="flex justify-end">
        <Button type="button" variant="outline" onClick={() => void apply(DEFAULT_APPEARANCE)}>
          <RotateCcw className="h-4 w-4" />
          Reset all
        </Button>
      </div>
    </div>
  );
}
