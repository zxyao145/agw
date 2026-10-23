"use client";

import * as React from "react";
import Link from "next/link";
import { RotateCcw } from "lucide-react";
import { toast } from "sonner";

import {
  BASE_FONT_SIZES,
  CHAT_FONT_SIZES,
  DEFAULT_APPEARANCE,
  DEFAULT_MODE,
  DEFAULT_THEME_ID,
  resolveFontSizes,
} from "@desktop/shared/appearance";
import type { AppearanceMode, AppearanceSettings } from "@desktop/shared/contracts";
import { Button, Card, CardContent, cn, Label } from "@agw/components";
import { type AvailableTheme, DEFAULT_THEME_TITLE } from "./resolve-theme";

const FOLLOW = "follow";

const MODES: readonly { mode: AppearanceMode; title: string }[] = [
  { mode: "system", title: "System" },
  { mode: "light", title: "Light" },
  { mode: "dark", title: "Dark" },
];

/** 缩略图中的一个窗口：侧栏与两行文字。One window in a thumbnail: a sidebar and two text lines. */
function ModePreview({ kind }: { kind: "light" | "dark" }) {
  const bar = kind === "light" ? "bg-zinc-200" : "bg-zinc-800";
  return (
    <span
      className={cn(
        "flex min-w-0 flex-1 gap-1 p-1.5",
        kind === "light" ? "bg-white" : "bg-zinc-900",
      )}
    >
      <span className={cn("w-3 shrink-0 rounded-sm", bar)} />
      <span className="flex min-w-0 flex-1 flex-col gap-1">
        <span className={cn("h-1.5 w-full rounded-full", bar)} />
        <span className={cn("h-1.5 w-2/3 rounded-full", bar)} />
      </span>
    </span>
  );
}

function ModeButton({
  mode,
  title,
  selected,
  onSelect,
}: {
  mode: AppearanceMode;
  title: string;
  selected: boolean;
  onSelect: () => void;
}) {
  return (
    <button
      type="button"
      className="group grid cursor-pointer gap-1.5 text-left text-xs outline-none"
      aria-label={`${title} mode`}
      aria-pressed={selected}
      data-appearance-mode={mode}
      onClick={onSelect}
    >
      <span
        className={cn(
          "flex h-16 w-28 overflow-hidden rounded-lg border-2 transition-colors group-focus-visible:ring-[3px] group-focus-visible:ring-ring/50",
          selected ? "border-primary" : "border-border group-hover:border-muted-foreground/40",
        )}
      >
        {mode === "dark" ? null : <ModePreview kind="light" />}
        {mode === "light" ? null : <ModePreview kind="dark" />}
      </span>
      <span className={selected ? "font-medium text-foreground" : "text-muted-foreground"}>
        {title}
      </span>
    </button>
  );
}

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
  themes,
  manageThemesHref,
  save,
}: {
  /** `mode` 为有效 Mode。`mode` holds the effective mode. */
  appearance: AppearanceSettings;
  themes: readonly AvailableTheme[];
  manageThemesHref: string;
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
  // 选中的主题不在列表中时（extension 状态还没有到达）仍显示它的 id。
  // The selected theme stays shown by id while it is not listed (before the extension state arrives).
  const missingTheme =
    value.theme !== DEFAULT_THEME_ID && !themes.some((theme) => theme.id === value.theme);

  return (
    <div className="space-y-6">
      <Card className="gap-0 overflow-hidden p-4">
        <CardContent className="space-y-5 px-0">
          <h2 className="text-sm font-semibold">Visual style</h2>
          <div className="grid gap-2">
            <p className="text-sm font-medium" id="appearance-mode-label">
              Mode
            </p>
            <div
              className="flex flex-wrap gap-3"
              role="group"
              aria-labelledby="appearance-mode-label"
            >
              {MODES.map(({ mode, title }) => (
                <ModeButton
                  key={mode}
                  mode={mode}
                  title={title}
                  selected={value.mode === mode}
                  onSelect={() => void apply({ ...value, mode })}
                />
              ))}
            </div>
          </div>
          <div className="grid gap-2">
            <Label htmlFor="appearance-theme">Theme</Label>
            <div className="flex items-center gap-2">
              <select
                id="appearance-theme"
                className="h-9 min-w-48 rounded-md border bg-background px-3 text-sm"
                value={value.theme}
                onChange={(event) => void apply({ ...value, theme: event.target.value })}
              >
                <option value={DEFAULT_THEME_ID}>{DEFAULT_THEME_TITLE}</option>
                {themes.map((theme) => (
                  <option key={theme.id} value={theme.id}>
                    {theme.label}
                  </option>
                ))}
                {missingTheme ? <option value={value.theme}>{value.theme}</option> : null}
              </select>
              <Button type="button" size="sm" variant="outline" asChild>
                <Link href={manageThemesHref}>Manage</Link>
              </Button>
            </div>
          </div>
        </CardContent>
      </Card>

      <Card className="gap-0 overflow-hidden p-4">
        <CardContent className="space-y-5 px-0">
          <h2 className="text-sm font-semibold">Font sizes</h2>
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
        <Button
          type="button"
          variant="outline"
          onClick={() => void apply({ ...DEFAULT_APPEARANCE, mode: DEFAULT_MODE })}
        >
          <RotateCcw className="h-4 w-4" />
          Reset all
        </Button>
      </div>
    </div>
  );
}
