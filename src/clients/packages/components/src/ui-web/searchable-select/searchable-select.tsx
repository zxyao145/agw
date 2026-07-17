"use client";

import * as React from "react";
import { CheckIcon, ChevronDownIcon } from "lucide-react";

import { Button } from "../shadcn/button";
import { Input } from "../shadcn/input";
import { Label } from "../shadcn/label";
import { Popover, PopoverContent, PopoverTrigger } from "../shadcn/popover";

export type SearchableSelectOption = {
  value: string;
  title: string;
  subtitle?: string;
  group?: string;
};

type SearchableSelectBaseProps = {
  id: string;
  label?: string;
  ariaLabel?: string;
  options: SearchableSelectOption[];
  placeholder: string;
  searchPlaceholder: string;
  disabled?: boolean;
  isLoading?: boolean;
  errorMessage?: string | null;
  clearable?: boolean;
  size?: "default" | "sm";
};

type SearchableSelectSingleProps = {
  multiple?: false;
  value: string;
  onValueChange: (value: string) => void;
};

type SearchableSelectMultipleProps = {
  multiple: true;
  value: string[];
  onValueChange: (value: string[]) => void;
  selectionText?: string;
};

type SearchableSelectProps = SearchableSelectBaseProps &
  (SearchableSelectSingleProps | SearchableSelectMultipleProps);

export function SearchableSelect(props: SearchableSelectProps) {
  const {
    id,
    label,
    ariaLabel,
    options,
    placeholder,
    searchPlaceholder,
    disabled,
    isLoading,
    errorMessage,
    clearable = true,
    size = "default",
  } = props;
  const [open, setOpen] = React.useState(false);
  const [search, setSearch] = React.useState("");
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);

  React.useEffect(() => {
    if (!open) return;

    const t = window.setTimeout(() => {
      searchInputRef.current?.focus();
    }, 0);

    return () => {
      window.clearTimeout(t);
    };
  }, [open]);

  const selectedValues = React.useMemo(
    () => (props.multiple ? props.value : props.value ? [props.value] : []),
    [props.multiple, props.value],
  );
  const selectedOptions = React.useMemo(
    () => options.filter((option) => selectedValues.includes(option.value)),
    [options, selectedValues],
  );

  const filteredOptions = React.useMemo(() => {
    const q = search.trim().toLowerCase();
    if (!q.length) return options;
    return options.filter((opt) => {
      const haystack =
        `${opt.title} ${opt.subtitle ?? ""} ${opt.group ?? ""} ${opt.value}`.toLowerCase();
      return haystack.includes(q);
    });
  }, [options, search]);
  const groupedOptions = React.useMemo(() => {
    const groups: { label: string | null; options: SearchableSelectOption[] }[] = [];

    for (const option of filteredOptions) {
      const groupLabel = option.group ?? null;
      let group = groups.find((item) => item.label === groupLabel);
      if (!group) {
        group = { label: groupLabel, options: [] };
        groups.push(group);
      }

      group.options.push(option);
    }

    return groups;
  }, [filteredOptions]);

  const triggerText = props.multiple
    ? (props.selectionText ??
      (selectedValues.length > 0 ? `${selectedValues.length} selected` : placeholder))
    : (selectedOptions[0]?.title ?? placeholder);
  const hasSelection = selectedValues.length > 0;

  const handleOpenChange = (nextOpen: boolean) => {
    setOpen(nextOpen);
    if (!nextOpen) {
      setSearch("");
    }
  };

  const handleClear = () => {
    if (props.multiple) {
      props.onValueChange([]);
      return;
    }

    props.onValueChange("");
    setOpen(false);
    setSearch("");
  };

  const handleOptionSelect = (optionValue: string) => {
    if (props.multiple) {
      props.onValueChange(
        props.value.includes(optionValue)
          ? props.value.filter((value) => value !== optionValue)
          : [...props.value, optionValue],
      );
      return;
    }

    props.onValueChange(optionValue);
    if (!props.multiple) {
      setOpen(false);
      setSearch("");
    }
  };

  const itemClassName =
    "flex w-full cursor-pointer select-none items-start gap-2 rounded-sm px-2 py-1.5 text-left text-sm outline-none hover:bg-accent hover:text-accent-foreground focus:bg-accent focus:text-accent-foreground";

  return (
    <div className="grid gap-2">
      {label ? <Label htmlFor={id}>{label}</Label> : null}

      <Popover modal open={open} onOpenChange={handleOpenChange}>
        <PopoverTrigger asChild>
          <Button
            id={id}
            type="button"
            variant="outline"
            size={size}
            className="w-full justify-between gap-2 overflow-hidden font-normal"
            disabled={disabled}
            aria-haspopup="listbox"
            aria-expanded={open}
            aria-label={ariaLabel ?? label}
          >
            <span className={hasSelection ? "truncate" : "truncate text-muted-foreground"}>
              {triggerText}
            </span>
            <ChevronDownIcon className="size-4 opacity-50" />
          </Button>
        </PopoverTrigger>

        <PopoverContent
          className="w-(--radix-popover-trigger-width) p-2"
          align="start"
          role="listbox"
          aria-label={ariaLabel ?? label}
          aria-multiselectable={props.multiple || undefined}
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
              {clearable && hasSelection && (
                <button
                  type="button"
                  className={`${itemClassName} text-muted-foreground`}
                  onClick={handleClear}
                >
                  Clear selection
                </button>
              )}

              {filteredOptions.length === 0 ? (
                <div className="px-2 py-1.5 text-sm text-muted-foreground">No results.</div>
              ) : (
                groupedOptions.map((group) => (
                  <React.Fragment key={group.label ?? "ungrouped"}>
                    {group.label ? (
                      <div className="mt-3 px-2 py-1.5 text-xs text-muted-foreground">
                        {group.label}
                      </div>
                    ) : null}
                    {group.options.map((opt) => {
                      const isSelected = selectedValues.includes(opt.value);

                      return (
                        <button
                          key={opt.value}
                          type="button"
                          role="option"
                          aria-selected={isSelected}
                          className={itemClassName}
                          onClick={() => handleOptionSelect(opt.value)}
                        >
                          <div className="min-w-0">
                            <div className="truncate text-sm">{opt.title}</div>
                            {opt.subtitle ? (
                              <div className="truncate font-mono text-xs text-muted-foreground">
                                {opt.subtitle}
                              </div>
                            ) : null}
                          </div>
                          <span className="ml-auto flex size-4 shrink-0 items-center justify-center">
                            {isSelected ? <CheckIcon className="size-4" /> : null}
                          </span>
                        </button>
                      );
                    })}
                  </React.Fragment>
                ))
              )}
            </div>
          )}
        </PopoverContent>
      </Popover>
    </div>
  );
}
