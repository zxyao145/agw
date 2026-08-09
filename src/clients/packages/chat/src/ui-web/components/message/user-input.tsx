"use client";

import { ArrowUp } from "lucide-react";
import { Button } from "@agw/components";
import { Textarea } from "@agw/components";
import { KeyboardEvent, ReactNode, useRef, useImperativeHandle, forwardRef } from "react";
import React from "react";
import { Item, ItemContent, ItemDescription, ItemGroup, ItemTitle } from "@agw/components";
import { Badge } from "@agw/components";
import { replaceTrailingSuggestion } from "./suggestion-trigger";

export interface SuggestionItem {
  text: string;
  kind?: string;
  description?: string;
}

// Main UserInput component
export interface UserInputProps {
  onSuggestion?: (value: string) => SuggestionItem[] | Promise<SuggestionItem[]>;
  // Execution state
  isExecuting?: boolean;

  // Actions
  onExecute?: (value: string) => void;
  onStop?: () => void;

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
  bottomLeft: ReactNode[];
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
  onStop?: () => void;
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
  onStop,
  textareaRef,
}: UserInputRootProps) {
  const { topLeft, topRight, bottomLeft, help, sender } = slots;
  const canStop = isExecuting && Boolean(onStop);
  const isDisabled = isExecuting ? !canStop : !input.trim();
  const handleClick = () => {
    if (canStop) {
      onStop?.();
      return;
    }
    onSend();
  };

  return (
    <div className="relative">
      {suggestions}

      {/* Top bar with tools and actions */}
      <div className="flex mb-2 gap-2 pointer-events-auto">
        <div
          className={`bg-background rounded-md flex items-center p-0 ${topLeft.length > 0 ? "border" : ""}`}
        >
          {topLeft.length > 0 && <>{topLeft}</>}
        </div>
        <div className="flex-1" />
        <div
          className={`bg-background rounded-md flex items-center p-0 ${topRight.length > 0 ? "border" : ""}`}
        >
          {topRight.length > 0 && <>{topRight}</>}
        </div>
      </div>
      {/* Input area with textarea and action button */}
      <div className="relative">
        <div className="pointer-events-auto relative px-2 pt-2 pb-11 rounded-xl border bg-background shadow-sm">
          <Textarea
            ref={textareaRef}
            value={input}
            onChange={(e) => onInputChange(e.target.value)}
            onKeyDown={onKeyDown}
            placeholder={placeholder}
            rows={rows}
            className={`${maxHeight} agw-scrollbar min-h-[1lh] resize-none overflow-x-hidden
            overflow-y-auto rounded-none 
            border-0 p-0 mb-4
            shadow-none 
            focus-visible:ring-0 
            focus-visible:ring-offset-0`}
            disabled={isExecuting}
          />

          {/* Action button - comment mode or regular send */}
          <div className="absolute left-2 right-2 bottom-2 h-7 flex justify-between">
            <div className="flex min-w-0 items-center">{bottomLeft}</div>
            <Button
              size="icon-sm"
              className="rounded-full size-7"
              onClick={handleClick}
              disabled={isDisabled}
            >
              {sender.length > 0 ? <>{sender}</> : <ArrowUp className="size-5" />}
            </Button>
          </div>
        </div>
      </div>
      {/* Helper text */}
      <div className="text-xs text-muted-foreground mt-2">
        {help.length > 0 ? <>{help}</> : "Press Enter for new line • Ctrl/Shift+Enter to send"}
      </div>
    </div>
  );
}

function getUserInputSlots(children?: ReactNode): UserInputSlots {
  const slots: UserInputSlots = {
    topLeft: [],
    topRight: [],
    bottomLeft: [],
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
      case "UserInput.BottomLeft":
        slots.bottomLeft.push(child);
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

function UserInputSuggestions({ suggestions, onSelect }: UserInputSuggestionsProps) {
  if (suggestions.length === 0) {
    return null;
  }

  return (
    <div className="absolute z-99 bottom-18 left-0 right-0 pointer-events-auto mb-3 py-2 px-1 rounded-md border bg-background/95 shadow-sm backdrop-blur-sm">
      <div className="flex items-center justify-between px-2 pb-1 text-[11px] font-semibold uppercase tracking-wide text-muted-foreground">
        Suggestions
      </div>
      <ItemGroup className="gap-0">
        {suggestions.map((suggestion, index) => (
          <Item
            key={`${suggestion.text}-${index}`}
            onClick={() => onSelect(suggestion)}
            variant="outline"
            size="sm"
            className="p-1  cursor-pointer border-transparent rounded-sm bg-transparent text-xs text-foreground transition hover:border-border hover:bg-accent/50"
          >
            <ItemContent className="">
              <ItemTitle className="font-medium text-foreground">
                <h3 className="leading-none">{suggestion.text}</h3>
              </ItemTitle>
              {suggestion.description && (
                <ItemDescription>
                  <span className="flex item-start">
                    {suggestion.kind ? (
                      <Badge className="px-1.5 h-fit self-start text-xs leading-none border-0 mr-2">
                        {suggestion.kind}
                      </Badge>
                    ) : null}

                    <span className="text-[11px]">{suggestion.description}</span>
                  </span>
                </ItemDescription>
              )}
            </ItemContent>
          </Item>
        ))}
      </ItemGroup>
    </div>
  );
}

export interface UserInputRef {
  value: string;
  setInput: (value: string) => void;
  insertText: (text: string) => void;
}

function UserInputContainer({
  isExecuting = false,
  onExecute,
  onStop,
  placeholder = "Type your message...",
  rows = 1,
  maxHeight = "max-h-60",
  children,
  onSuggestion,
  inputRef,
}: UserInputProps & { inputRef: React.RefObject<UserInputRef | null> }) {
  const [input, setInput] = React.useState("");
  const [suggestions, setSuggestions] = React.useState<SuggestionItem[]>([]);
  const slots = getUserInputSlots(children);
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const suggestionRequestRef = useRef(0);

  useImperativeHandle(
    inputRef,
    () => ({
      get value() {
        return input;
      },
      setInput: (value: string) => {
        suggestionRequestRef.current += 1;
        setInput(value);
        setSuggestions([]);
      },
      insertText: (text: string) => {
        const textarea = textareaRef.current;
        const selectionStart = textarea?.selectionStart ?? input.length;
        const selectionEnd = textarea?.selectionEnd ?? selectionStart;
        const insertedText = `${text} `;
        const newValue = input.slice(0, selectionStart) + insertedText + input.slice(selectionEnd);
        const nextCursor = selectionStart + insertedText.length;
        suggestionRequestRef.current += 1;
        setInput(newValue);
        setSuggestions([]);
        setTimeout(() => {
          const currentTextarea = textareaRef.current;
          if (currentTextarea) {
            currentTextarea.focus();
            currentTextarea.setSelectionRange(nextCursor, nextCursor);
          }
        }, 0);
      },
    }),
    [input],
  );

  const handleSuggestionClick = (suggestion: SuggestionItem) => {
    const nextValue = replaceTrailingSuggestion(input, suggestion.text);
    suggestionRequestRef.current += 1;
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
    const requestId = suggestionRequestRef.current + 1;
    suggestionRequestRef.current = requestId;
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
      result
        .then((nextSuggestions) => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions(nextSuggestions);
          }
        })
        .catch(() => {
          if (suggestionRequestRef.current === requestId) {
            setSuggestions([]);
          }
        });
    } else {
      // Sync suggestion
      setSuggestions(result ?? []);
    }
  };

  const suggestionContent = (
    <UserInputSuggestions suggestions={suggestions} onSelect={handleSuggestionClick} />
  );

  const handleSend = () => {
    console.log("handleSend onExecute input", input);
    onExecute?.(input);
    suggestionRequestRef.current += 1;
    setInput("");
    setSuggestions([]);
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
      onStop={onStop}
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

interface BottomLeftProps {
  children: ReactNode;
}

function BottomLeft({ children }: BottomLeftProps) {
  return <>{children}</>;
}
BottomLeft.displayName = "UserInput.BottomLeft";

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
const UserInputWithRef = forwardRef<UserInputRef, UserInputProps>((props, ref) => {
  const internalRef = useRef<UserInputRef | null>(null);

  // Forward the internal ref to the parent ref
  useImperativeHandle(ref, () => internalRef.current!);

  return <UserInputContainer {...props} inputRef={internalRef} />;
});

UserInputWithRef.displayName = "UserInput";

export const UserInput = Object.assign(UserInputWithRef, {
  TopLeft: TopLeft,
  TopRight: TopRight,
  BottomLeft: BottomLeft,
  Help: Help,
  Sender: Sender,
});
