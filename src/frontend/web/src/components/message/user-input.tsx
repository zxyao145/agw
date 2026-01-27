"use client";

import { Send, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { KeyboardEvent, ReactNode, useRef } from "react";
import React from "react";
import {
  Item,
  ItemActions,
  ItemContent,
  ItemDescription,
  ItemGroup,
  ItemMedia,
  ItemTitle,
} from "@/components/ui/item";

export interface SuggestionItem {
  text: string;
  description?: string;
}

// Main UserInput component
export interface UserInputProps {
  onSuggestion?: (value: string) => SuggestionItem[] | Promise<SuggestionItem[]>;
  // Execution state
  isExecuting?: boolean;

  // Actions
  onExecute?: (value: string) => void;

  // Textarea configuration
  placeholder?: string;
  rows?: number;
  maxHeight?: string;

  // Children
  children?: ReactNode;
}

interface UserInputSlots {
  topLeft: ReactNode[];
  topRight: ReactNode[];
  help: ReactNode[];
  sender: ReactNode[];
}

interface UserInputRootProps {
  isExecuting: boolean;
  placeholder: string;
  rows: number;
  maxHeight: string;
  input: string;
  suggestions?: ReactNode;
  slots: UserInputSlots;
  onInputChange: (value: string) => void;
  onKeyDown: (event: KeyboardEvent<HTMLTextAreaElement>) => void;
  onSend: () => void;
  textareaRef?: React.RefObject<HTMLTextAreaElement | null>;
}

function UserInputRoot({
  isExecuting,
  placeholder,
  rows,
  maxHeight,
  input,
  suggestions,
  slots,
  onInputChange,
  onKeyDown,
  onSend,
  textareaRef,
}: UserInputRootProps) {
  const { topLeft, topRight, help, sender } = slots;

  return (
    <div className="relative">
      {suggestions}

      {/* Top bar with tools and actions */}
      <div className="flex mb-2 gap-2 pointer-events-auto">
        <div className="bg-background border rounded-md flex gap-2 items-center p-0">
          {topLeft.length > 0 && <>{topLeft}</>}
        </div>
        <div className="flex-1" />
        <div className="bg-background border rounded-md flex gap-2 items-center p-0">
          {topRight.length > 0 && <>{topRight}</>}
        </div>
      </div>
      {/* Input area with textarea and action button */}
      <div className="relative">
        <div className="flex flex-row gap-0 items-end bg-background border rounded-lg pointer-events-auto">
          <Textarea
            ref={textareaRef}
            value={input}
            onChange={(e) => onInputChange(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={placeholder}
            rows={rows}
            className={`${maxHeight} min-h-12 flex-1 resize-none bg-background border-0 shadow-none focus-visible:ring-0 focus-visible:ring-offset-0`}
            disabled={isExecuting}
          />

          {/* Action button - comment mode or regular send */}
          <Button
            className="cursor-pointer m-2"
            onClick={onSend}
            disabled={!input.trim() || isExecuting}
          >
            {sender.length > 0 ? <>{sender}</> : <Send className="w-5 h-5" />}
          </Button>
        </div>
      </div>
      {/* Helper text */}
      <div className="text-xs text-muted-foreground mt-2">
        {help.length > 0 ? (
          <>{help}</>
        ) : (
          "Press Enter for new line • Enter/Shift+Enter to send"
        )}
      </div>
    </div>
  );
}

function getUserInputSlots(children?: ReactNode): UserInputSlots {
  const slots: UserInputSlots = {
    topLeft: [],
    topRight: [],
    help: [],
    sender: [],
  };

  if (!children) {
    return slots;
  }

  const childArray = Array.isArray(children) ? children : [children];
  childArray.forEach((child) => {
    if (!child || typeof child !== "object" || !("type" in child)) {
      return;
    }

    const element = child as { type: { displayName?: string } };
    switch (element.type?.displayName) {
      case "UserInput.TopLeft":
        slots.topLeft.push(child);
        break;
      case "UserInput.TopRight":
        slots.topRight.push(child);
        break;
      case "UserInput.Help":
        slots.help.push(child);
        break;
      case "UserInput.Sender":
        slots.sender.push(child);
        break;
      default:
        break;
    }
  });

  return slots;
}

interface UserInputSuggestionsProps {
  suggestions: SuggestionItem[];
  onSelect: (suggestion: SuggestionItem) => void;
}

function UserInputSuggestions({
  suggestions,
  onSelect,
}: UserInputSuggestionsProps) {
  if (suggestions.length === 0) {
    return null;
  }

  return (
    <div className="absolute z-99 bottom-18 left-0  right-0 pointer-events-auto mb-3 rounded-xl border bg-background/95 p-2 shadow-sm backdrop-blur-sm">
      <div className="flex items-center justify-between px-2 pb-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Suggestions
        {/* <span className="rounded-full border px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
          Tab
        </span> */}
      </div>
      <ItemGroup className="gap-1">
        {suggestions.map((suggestion, index) => (
          <Item
            key={`${suggestion.text}-${index}`}
            onClick={() => onSelect(suggestion)}
            variant="outline"
            size="sm"
            className="cursor-pointer border-transparent bg-transparent px-2 py-2 text-xs text-foreground transition hover:border-border hover:bg-accent/50"
          >
            <ItemMedia
              variant="icon"
              className="size-7 border-border/60 bg-muted/60"
            >
              <Sparkles className="size-3 text-muted-foreground" />
            </ItemMedia>
            <ItemContent className="flex flex-row items-center justify-between">
              <ItemTitle className="text-xs font-medium text-foreground">
                {suggestion.text}
              </ItemTitle>
              {suggestion.description && (
                <ItemDescription className="text-[11px]">
                  {suggestion.description}
                </ItemDescription>
              )}
            </ItemContent>
          </Item>
        ))}
      </ItemGroup>
    </div>
  );
}

function UserInputContainer({
  isExecuting = false,
  onExecute,
  placeholder = "Type your message...",
  rows = 1,
  maxHeight = "max-h-50",
  children,
  onSuggestion,
}: UserInputProps) {
  const [input, setInput] = React.useState("");
  const [suggestions, setSuggestions] = React.useState<SuggestionItem[]>([]);
  const slots = getUserInputSlots(children);
  const textareaRef = useRef<HTMLTextAreaElement>(null);

  const handleSuggestionClick = (suggestion: SuggestionItem) => {
    const nextValue = `${suggestion.text} `;
    setInput(nextValue);
    setSuggestions([]);

    // Focus textarea and move cursor to end
    setTimeout(() => {
      const textarea = textareaRef.current;
      if (textarea) {
        textarea.focus();
        textarea.setSelectionRange(nextValue.length, nextValue.length);
      }
    }, 0);
  };

  const handleInputChange = (value: string) => {
    setInput(value);
    if (!onSuggestion) {
      if (suggestions.length > 0) {
        setSuggestions([]);
      }
      return;
    }

    const result = onSuggestion(value);
    if (result instanceof Promise) {
      // Async suggestion - clear current suggestions while loading
      setSuggestions([]);
      result.then((suggestions) => setSuggestions(suggestions)).catch(() => setSuggestions([]));
    } else {
      // Sync suggestion
      setSuggestions(result ?? []);
    }
  };

  const suggestionContent = (
    <UserInputSuggestions
      suggestions={suggestions}
      onSelect={handleSuggestionClick}
    />
  );

  const handleSend = () => {
    onExecute?.(input);
    setInput("");
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && (event.ctrlKey || event.shiftKey)) {
      event.preventDefault();
      if (input && input.trim()) {
        handleSend();
      }
    }
  };

  return (
    <UserInputRoot
      isExecuting={isExecuting}
      placeholder={placeholder}
      rows={rows}
      maxHeight={maxHeight}
      input={input}
      suggestions={suggestionContent}
      slots={slots}
      onInputChange={handleInputChange}
      onKeyDown={handleKeyDown}
      onSend={handleSend}
      textareaRef={textareaRef}
    />
  );
}

// TopLeftSlots component
interface TopLeftProps {
  children: ReactNode;
}

function TopLeft({ children }: TopLeftProps) {
  return <>{children}</>;
}
TopLeft.displayName = "UserInput.TopLeft";

// TopRightActions component
interface TopRightProps {
  children: ReactNode;
}

function TopRight({ children }: TopRightProps) {
  return <>{children}</>;
}
TopRight.displayName = "UserInput.TopRight";

// Help component
interface HelpProps {
  children: ReactNode;
}

function Help({ children }: HelpProps) {
  return <>{children}</>;
}
Help.displayName = "UserInput.Help";

// Help component
interface SenderProps {
  children: ReactNode;
}

function Sender({ children }: SenderProps) {
  return <>{children}</>;
}
Sender.displayName = "UserInput.Sender";

// Export compound component
export const UserInput = Object.assign(UserInputContainer, {
  TopLeft: TopLeft,
  TopRight: TopRight,
  Help: Help,
  Sender: Sender,
});
