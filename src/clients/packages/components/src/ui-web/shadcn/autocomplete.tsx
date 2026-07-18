"use client";

import * as React from "react";
import { Input } from "./input";

export interface AutocompleteProps extends Omit<React.ComponentProps<typeof Input>, "list"> {
  options?: string[];
}

export const Autocomplete = React.forwardRef<HTMLInputElement, AutocompleteProps>(
  ({ options = [], ...props }, ref) => {
    const listId = React.useId();

    return (
      <>
        <Input ref={ref} list={listId} {...props} />
        <datalist id={listId}>
          {options.map((option, index) => (
            <option key={`${option}-${index}`} value={option} />
          ))}
        </datalist>
      </>
    );
  },
);

Autocomplete.displayName = "Autocomplete";
