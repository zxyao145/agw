"use client";

import * as React from "react";

import { Button } from "../shadcn/button";
import {
  Combobox,
  ComboboxCollection,
  ComboboxContent,
  ComboboxEmpty,
  ComboboxGroup,
  ComboboxInput,
  ComboboxItem,
  ComboboxLabel,
  ComboboxList,
  ComboboxTrigger,
} from "../shadcn/combobox";
import { Label } from "../shadcn/label";

const MODAL_CONTENT_SELECTOR =
  '[data-slot="dialog-content"], [data-slot="sheet-content"], [data-slot="drawer-content"]';

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
  const rootRef = React.useRef<HTMLDivElement | null>(null);
  const searchInputRef = React.useRef<HTMLInputElement | null>(null);
  const [portalContainer, setPortalContainer] = React.useState<HTMLElement | null>(null);

  const selectedValues = React.useMemo(
    () => (props.multiple ? props.value : props.value ? [props.value] : []),
    [props.multiple, props.value],
  );
  const selectedOptions = React.useMemo(
    () => options.filter((option) => selectedValues.includes(option.value)),
    [options, selectedValues],
  );

  const optionsByValue = React.useMemo(
    () => new Map(options.map((option) => [option.value, option])),
    [options],
  );
  const groupedOptions = React.useMemo(() => {
    const groups: { value: string; label: string | null; items: string[] }[] = [];

    for (const option of options) {
      const groupLabel = option.group ?? null;
      let group = groups.find((item) => item.label === groupLabel);
      if (!group) {
        group = {
          value: groupLabel ?? "__searchable-select-ungrouped__",
          label: groupLabel,
          items: [],
        };
        groups.push(group);
      }

      group.items.push(option.value);
    }

    return groups;
  }, [options]);

  const triggerText = props.multiple
    ? (props.selectionText ??
      (selectedValues.length > 0 ? `${selectedValues.length} selected` : placeholder))
    : (selectedOptions[0]?.title ?? placeholder);
  const hasSelection = selectedValues.length > 0;

  const handleOpenChange = (nextOpen: boolean) => {
    if (nextOpen) {
      setPortalContainer(rootRef.current?.closest<HTMLElement>(MODAL_CONTENT_SELECTOR) ?? null);
    }

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

  const itemClassName =
    "cursor-pointer items-start py-1.5 text-left data-highlighted:bg-accent data-highlighted:text-accent-foreground";

  const comboboxContent = (
    <>
      <ComboboxTrigger
        render={
          <Button
            id={id}
            type="button"
            variant="outline"
            size={size}
            className="w-full justify-between gap-2 overflow-hidden font-normal"
            disabled={disabled}
            aria-label={ariaLabel ?? label}
          />
        }
      >
        <span className={hasSelection ? "truncate" : "truncate text-muted-foreground"}>
          {triggerText}
        </span>
      </ComboboxTrigger>

      <ComboboxContent
        portalContainer={portalContainer}
        initialFocus={searchInputRef}
        className="min-w-(--anchor-width)"
        aria-label={ariaLabel ?? label}
      >
        <ComboboxInput
          ref={searchInputRef}
          showTrigger={false}
          placeholder={searchPlaceholder}
          aria-label={searchPlaceholder}
          onKeyDown={(event) => {
            if (event.key === "Escape") {
              event.stopPropagation();
            }
          }}
        />

        {errorMessage ? (
          <div className="px-3 py-2 text-sm text-destructive">{errorMessage}</div>
        ) : isLoading ? (
          <div className="px-3 py-2 text-sm text-muted-foreground">Loading...</div>
        ) : (
          <>
            {clearable && hasSelection ? (
              <button
                type="button"
                className="mx-1 mt-1 flex w-[calc(100%-0.5rem)] cursor-pointer items-center rounded-sm px-2 py-1.5 text-left text-sm text-muted-foreground outline-none hover:bg-accent hover:text-accent-foreground focus-visible:bg-accent focus-visible:text-accent-foreground"
                onClick={handleClear}
              >
                Clear selection
              </button>
            ) : null}

            <ComboboxEmpty>No results.</ComboboxEmpty>
            <ComboboxList
              className="max-h-64 agw-scrollbar"
              aria-label={ariaLabel ?? label}
              aria-multiselectable={props.multiple || undefined}
            >
              {(group: (typeof groupedOptions)[number]) => (
                <ComboboxGroup key={group.value} items={group.items}>
                  {group.label ? <ComboboxLabel>{group.label}</ComboboxLabel> : null}
                  <ComboboxCollection>
                    {(optionValue: string) => {
                      const option = optionsByValue.get(optionValue);
                      if (!option) return null;

                      return (
                        <ComboboxItem
                          key={option.value}
                          value={option.value}
                          className={itemClassName}
                        >
                          <div className="min-w-0">
                            <div className="truncate text-sm">{option.title}</div>
                            {option.subtitle ? (
                              <div className="truncate font-mono text-xs text-muted-foreground">
                                {option.subtitle}
                              </div>
                            ) : null}
                          </div>
                        </ComboboxItem>
                      );
                    }}
                  </ComboboxCollection>
                </ComboboxGroup>
              )}
            </ComboboxList>
          </>
        )}
      </ComboboxContent>
    </>
  );

  const comboboxProps = {
    items: groupedOptions,
    open,
    onOpenChange: handleOpenChange,
    inputValue: search,
    onInputValueChange: (nextSearch: string, details: { reason: string }) => {
      if (details.reason !== "item-press") {
        setSearch(nextSearch);
      }
    },
    filter: (optionValue: string, query: string) => {
      const option = optionsByValue.get(optionValue);
      if (!option) return false;

      const haystack =
        `${option.title} ${option.subtitle ?? ""} ${option.group ?? ""} ${option.value}`.toLowerCase();
      return haystack.includes(query.trim().toLowerCase());
    },
    itemToStringLabel: (optionValue: string) =>
      optionsByValue.get(optionValue)?.title ?? optionValue,
    itemToStringValue: (optionValue: string) => optionValue,
    disabled,
    modal: true,
  };

  return (
    <div ref={rootRef} className="grid gap-2">
      {label ? <Label htmlFor={id}>{label}</Label> : null}
      {props.multiple ? (
        <Combobox<string, true>
          {...comboboxProps}
          multiple
          value={props.value}
          onValueChange={props.onValueChange}
        >
          {comboboxContent}
        </Combobox>
      ) : (
        <Combobox<string>
          {...comboboxProps}
          value={props.value || null}
          onValueChange={(nextValue) => {
            props.onValueChange(nextValue ?? "");
            setOpen(false);
            setSearch("");
          }}
        >
          {comboboxContent}
        </Combobox>
      )}
    </div>
  );
}
