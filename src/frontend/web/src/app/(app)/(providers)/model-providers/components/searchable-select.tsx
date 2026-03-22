"use client";

import * as React from "react";
import { ChevronDownIcon } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { SearchableSelectOption } from "./types";

type SearchableSelectProps = {
  id: string;
  label: string;
  value: string;
  onValueChange: (value: string) => void;
  options: SearchableSelectOption[];
  placeholder: string;
  searchPlaceholder: string;
  disabled?: boolean;
  isLoading?: boolean;
  errorMessage?: string | null;
};

export function SearchableSelect({
  id,
  label,
  value,
  onValueChange,
  options,
  placeholder,
  searchPlaceholder,
  disabled,
  isLoading,
  errorMessage,
}: SearchableSelectProps) {
  const [open, setOpen] = React.useState(false);
  const [search, setSearch] = React.useState("");
  const rootRef = React.useRef<HTMLDivElement | null>(null);
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);

  React.useEffect(() => {
    if (!open) return;

    const t = window.setTimeout(() => {
      searchInputRef.current?.focus();
    }, 0);

    const onPointerDown = (e: MouseEvent) => {
      const el = rootRef.current;
      if (!el) return;
      if (e.target instanceof Node && !el.contains(e.target)) {
        setOpen(false);
        setSearch("");
      }
    };

    document.addEventListener("mousedown", onPointerDown, true);

    return () => {
      window.clearTimeout(t);
      document.removeEventListener("mousedown", onPointerDown, true);
    };
  }, [open]);

  const selected = React.useMemo(
    () => options.find((x) => x.value === value) ?? null,
    [options, value],
  );

  const filteredOptions = React.useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q.length) return options;
    return options.filter((opt) => {
      const haystack = `${opt.title} ${opt.subtitle ?? ""} ${opt.value}`.toLowerCase();
      return haystack.includes(q);
    });
  }, [options, search]);

  const triggerText = selected ? selected.title : placeholder;

  const itemClassName =
    "flex w-full cursor-pointer select-none items-start gap-2 rounded-sm px-2 py-1.5 text-left text-sm outline-none hover:bg-accent hover:text-accent-foreground focus:bg-accent focus:text-accent-foreground";

  return (
    <div ref={rootRef} className="grid gap-2">
      <Label htmlFor={id}>{label}</Label>

      <div className="relative">
        <Button
          id={id}
          type="button"
          variant="outline"
          className="w-full justify-between gap-2 overflow-hidden font-normal"
          disabled={disabled}
          aria-haspopup="listbox"
          aria-expanded={open}
          onClick={() => setOpen((x) => !x)}
        >
          <span className={selected ? "truncate" : "truncate text-muted-foreground"}>
            {triggerText}
          </span>
          <ChevronDownIcon className="size-4 opacity-50" />
        </Button>

        {open && (
          <div
            className="bg-popover text-popover-foreground absolute left-0 top-full z-50 mt-1 w-full rounded-md border p-2 shadow-md"
            role="listbox"
            aria-label={label}
          >
            <div className="pb-2">
              <Input
                ref={searchInputRef}
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder={searchPlaceholder}
                onKeyDown={(e) => {
                  e.stopPropagation();
                  if (e.key === "Escape") {
                    setOpen(false);
                    setSearch("");
                  }
                }}
              />
            </div>

            {errorMessage ? (
              <div className="px-2 py-1.5 text-sm text-destructive">{errorMessage}</div>
            ) : isLoading ? (
              <div className="px-2 py-1.5 text-sm text-muted-foreground">Loading...</div>
            ) : (
              <div className="max-h-64 overflow-auto">
                {value.trim().length > 0 && (
                  <button
                    type="button"
                    className={`${itemClassName} text-muted-foreground`}
                    onClick={() => {
                      onValueChange("");
                      setOpen(false);
                      setSearch("");
                    }}
                  >
                    Clear selection
                  </button>
                )}

                {filteredOptions.length === 0 ? (
                  <div className="px-2 py-1.5 text-sm text-muted-foreground">No results.</div>
                ) : (
                  filteredOptions.map((opt) => (
                    <button
                      key={opt.value}
                      type="button"
                      className={itemClassName}
                      onClick={() => {
                        onValueChange(opt.value);
                        setOpen(false);
                        setSearch("");
                      }}
                    >
                      <div className="min-w-0">
                        <div className="truncate text-sm">{opt.title}</div>
                        {opt.subtitle ? (
                          <div className="truncate font-mono text-xs text-muted-foreground">
                            {opt.subtitle}
                          </div>
                        ) : null}
                      </div>
                    </button>
                  ))
                )}
              </div>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
