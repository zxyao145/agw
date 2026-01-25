"use client";

import { Send } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { KeyboardEvent, ReactNode, createContext, useContext } from "react";
import React from "react";

// Context for UserInput state
interface UserInputContextValue {
  isExecuting: boolean;
  onExecute?: (value: string) => void;
  placeholder: string;
  rows: number;
  maxHeight: string;
}

const UserInputContext = createContext<UserInputContextValue | null>(null);

// Main UserInput component
export interface UserInputProps {
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

function UserInputRoot({
  isExecuting = false,
  onExecute,
  placeholder = "Type your message...",
  rows = 1,
  maxHeight = "max-h-50",
  children,
}: UserInputProps) {
  const contextValue: UserInputContextValue = {
    isExecuting,
    onExecute,
    placeholder,
    rows,
    maxHeight,
  };
  const [input, setInput] = React.useState("");
  
  const onChange = (value: string) => {
    setInput(value);
  };

  const handleSend = () => {
    onExecute?.(input);
  };

  // Handle keyboard shortcuts
  const handleKeyDown = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    // Ctrl+Enter or Shift+Enter: Send message
    if (e.key === "Enter" && (e.ctrlKey || e.shiftKey)) {
      e.preventDefault();
      if (input && input.trim()) {
        onExecute?.(input.trim());
      }
      return;
    }

    // Enter (alone): New line (default behavior)
  };;

  // Extract children slots
  const topLeftSlots: ReactNode[] = [];
  const topRightSlots: ReactNode[] = [];
  const helpSlots: ReactNode[] = [];
  const senderSlots: ReactNode[] = [];

  if (children) {
    const childArray = Array.isArray(children) ? children : [children];
    childArray.forEach((child) => {
      if (!child) return;

      // Check if it's a valid React element with a type property
      if (typeof child === "object" && "type" in child) {
        const element = child as { type: { displayName?: string } };
        if (element.type?.displayName === "UserInput.TopLeft") {
          topLeftSlots.push(child);
        } else if (element.type?.displayName === "UserInput.TopRight") {
          topRightSlots.push(child);
        } else if (element.type?.displayName === "UserInput.Help") {
          helpSlots.push(child);
        } else if (element.type?.displayName === "UserInput.Sender") {
          senderSlots.push(child);
        }
      }
    });
  }

  return (
    <UserInputContext.Provider value={contextValue}>
      <div className="relative">
        {/* Top bar with tools and actions */}
        <div className="flex mb-2 gap-2 pointer-events-auto">
          <div className="bg-background border rounded-md flex gap-2 items-center p-0">
            {topLeftSlots.length > 0 && <>{topLeftSlots}</>}
          </div>
          <div className="flex-1" />
          <div className="bg-background border rounded-md flex gap-2 items-center p-0">
            {topRightSlots.length > 0 && <>{topRightSlots}</>}
          </div>
        </div>

        {/* Input area with textarea and action button */}
        <div className="relative">
          <div className="flex flex-row gap-0 items-end bg-background border rounded-lg pointer-events-auto">
            <Textarea
              value={input}
              onChange={(e) => onChange(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder={placeholder}
              rows={rows}
              className={`${maxHeight} min-h-12 flex-1 resize-none bg-background border-0 shadow-none focus-visible:ring-0 focus-visible:ring-offset-0`}
              disabled={isExecuting}
            />

            {/* Action button - comment mode or regular send */}
            <Button
              className="cursor-pointer m-2"
              onClick={handleSend}
              disabled={!input.trim() || isExecuting}
            >
              {senderSlots.length > 0 ? (
                <>{senderSlots}</>
              ) : (
                <Send className="w-5 h-5" />
              )}
            </Button>
          </div>
        </div>

        {/* Helper text */}
        <div className="text-xs text-muted-foreground mt-2">
          {helpSlots.length > 0 ? (
            <>{helpSlots}</>
          ) : (
            "Press Enter for new line • Enter/Shift+Enter to send"
          )}
        </div>
      </div>
    </UserInputContext.Provider>
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
export const UserInput = Object.assign(UserInputRoot, {
  TopLeft: TopLeft,
  TopRight: TopRight,
  Help: Help,
  Sender: Sender,
});
