"use client";

import { Send, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { KeyboardEvent, ReactNode } from "react";
import React from "react";
import {
  Item,
  ItemActions,
  ItemContent,
  ItemGroup,
  ItemTitle,
} from "@/components/ui/item";

export interface SuggestionItem {
  text: string;
}

// Main UserInput component
export interface UserInputProps {
  onSuggestion?: (value: string) => SuggestionItem[];
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
  suggestions: SuggestionItem[];
  slots: UserInputSlots;
  onInputChange: (value: string) => void;
  onKeyDown: (event: KeyboardEvent<HTMLTextAreaElement>) => void;
  onSend: () => void;
  onSuggestionClick: (suggestion: SuggestionItem) => void;
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
  onSuggestionClick,
}: UserInputRootProps) {
  const { topLeft, topRight, help, sender } = slots;

  return (
    <div className="relative">
      {suggestions.length > 0 && (
        <div className="pointer-events-auto mb-3 rounded-xl border bg-background/95 p-2 shadow-sm backdrop-blur-sm">
          <div className="px-2 pb-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
            Suggestions
          </div>
          <ItemGroup className="gap-1">
            {suggestions.map((suggestion, index) => (
              <Item
                key={`${suggestion.text}-${index}`}
                onClick={() => onSuggestionClick(suggestion)}
                variant="outline"
                size="sm"
                className="cursor-pointer border-transparent bg-transparent px-2 py-2 text-xs text-foreground transition hover:border-border hover:bg-accent/50"
              >
                <ItemContent>
                  <ItemTitle className="text-xs font-medium text-foreground">
                    {suggestion.text}
                  </ItemTitle>
                </ItemContent>
                <ItemActions>
                  <Sparkles className="size-3 text-muted-foreground" />
                </ItemActions>
              </Item>
            ))}
          </ItemGroup>
        </div>
      )}

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

  const handleSuggestionClick = (suggestion: SuggestionItem) => {
    const nextValue = `${suggestion.text} `;
    setInput(nextValue);
    if (onSuggestion) {
      setSuggestions(onSuggestion(nextValue) ?? []);
    }
  };

  const handleInputChange = (value: string) => {
    setInput(value);
    if (!onSuggestion) {
      if (suggestions.length > 0) {
        setSuggestions([]);
      }
      return;
    }

    setSuggestions(onSuggestion(value) ?? []);
  };

  const handleSend = () => {
    onExecute?.(input);
  };

  const handleKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && (event.ctrlKey || event.shiftKey)) {
      event.preventDefault();
      if (input && input.trim()) {
        onExecute?.(input.trim());
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
      suggestions={suggestions}
      slots={slots}
      onInputChange={handleInputChange}
      onKeyDown={handleKeyDown}
      onSend={handleSend}
      onSuggestionClick={handleSuggestionClick}
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
