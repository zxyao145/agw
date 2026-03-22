"use client";

type SwitchProps = {
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
  label?: string;
};

export function Switch({ checked, onCheckedChange, disabled, label }: SwitchProps) {
  return (
    <label className="inline-flex items-center gap-2" aria-label={label} title={label}>
      <input
        type="checkbox"
        className="peer sr-only"
        checked={checked}
        disabled={disabled}
        onChange={(e) => onCheckedChange(e.target.checked)}
      />
      <span
        className={[
          "relative inline-flex h-5 w-9 items-center rounded-full border transition-colors",
          "bg-muted peer-checked:bg-primary",
          "peer-disabled:cursor-not-allowed peer-disabled:opacity-50",
        ].join(" ")}
      >
        <span
          className={[
            "absolute left-0.5 top-0.5 h-4 w-4 rounded-full bg-background shadow-sm transition-transform",
            checked ? "translate-x-4" : "translate-x-0",
          ].join(" ")}
        />
      </span>
    </label>
  );
}
