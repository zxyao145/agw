"use client";

import * as React from "react";
import { Send, Info } from "lucide-react";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";
import {
  AiMessage,
  InitMessageContent,
  PermissionMode,
} from "../types";
import { AiMessageComponment } from "./message";
import { SettingsDialog } from "./settings-dialog";

interface ChatProps {
  messages: AiMessage[];
  input: string;
  setInput: (value: string) => void;
  isExecuting: boolean;
  workingDirectory: string;
  setWorkingDirectory: (value: string) => void;
  apiKey: string;
  setApiKey: (value: string) => void;
  apiBaseUrl: string;
  setApiBaseUrl: (value: string) => void;
  permissionMode: string;
  setPermissionMode: (value: string) => void;
  initContent: InitMessageContent | null;
  messagesEndRef: React.RefObject<HTMLDivElement>;
  onExecute: () => void;
  onClearSession: () => void;
  onKeyDown: (e: React.KeyboardEvent<HTMLTextAreaElement>) => void;
  processMessages: (msgs: AiMessage[]) => Array<
    | { type: "accordion"; messages: AiMessage[]; toolName: string }
    | { type: "normal"; message: AiMessage }
  >;
  createArr: (key: string, value: string[]) => React.ReactNode;
}

export function Chat({
  messages,
  input,
  setInput,
  isExecuting,
  workingDirectory,
  setWorkingDirectory,
  apiKey,
  setApiKey,
  apiBaseUrl,
  setApiBaseUrl,
  permissionMode,
  setPermissionMode,
  initContent,
  messagesEndRef,
  onExecute,
  onClearSession,
  onKeyDown,
  processMessages,
  createArr,
}: ChatProps) {
  return (
    <div className="flex flex-col min-h-[calc(100vh-96px)]">
      {/* Header Area */}
      <div className="flex items-center gap-2">
        <SettingsDialog
          workingDirectory={workingDirectory}
          setWorkingDirectory={setWorkingDirectory}
          apiKey={apiKey}
          setApiKey={setApiKey}
          apiBaseUrl={apiBaseUrl}
          setApiBaseUrl={setApiBaseUrl}
          permissionMode={permissionMode}
          setPermissionMode={setPermissionMode}
        />

        <Popover>
          <PopoverTrigger asChild>
            <Button
              disabled={!initContent}
              variant="ghost"
              className="cursor-pointer"
            >
              <Info className="h-4 w-4" />
            </Button>
          </PopoverTrigger>
          <PopoverContent className="w-120">
            <div className="grid gap-4">
              <div className="space-y-2">
                <h4 className="leading-none font-medium">
                  Claude Code Info
                </h4>
                <p className="text-muted-foreground text-sm">
                  Claude Code meta info
                </p>
              </div>
              {!initContent ? (
                <p className="text-muted-foreground text-sm">
                  not interactive
                </p>
              ) : (
                <div className="grid max-h-80 overflow-auto">
                  <div className="grid grid-cols-3 items-center py-2 border-b">
                    <Label>claudeCodeVersion</Label>
                    <div className="col-span-2">
                      {initContent?.claudeCodeVersion ?? "-"}
                    </div>
                  </div>
                  <div className="grid grid-cols-3 items-center py-2 border-b">
                    <Label>permissionMode</Label>
                    <div className="col-span-2">
                      {initContent?.permissionMode ?? "-"}
                    </div>
                  </div>
                  <div className="grid grid-cols-3 items-center py-2 border-b">
                    <Label>model</Label>
                    <div className="col-span-2">
                      {initContent?.model ?? "-"}
                    </div>
                  </div>
                  {createArr("tools", initContent?.tools)}
                  {createArr("slashCommands", initContent?.slashCommands)}
                  {createArr("agents", initContent?.agents)}
                  {createArr("plugins", initContent?.plugins)}
                  {createArr("mcpServers", initContent?.mcpServers)}
                </div>
              )}
            </div>
          </PopoverContent>
        </Popover>
      </div>

      <div className="flex-1 flex flex-col overflow-hidden">
        {/* Messages Area - Scrollable */}
        <div className="flex-1 flex overflow-hidden min-h-full">
          <div className="flex-1 overflow-y-auto space-y-4">
            {messages.length === 0 && (
              <div className="flex items-center justify-center h-40">
                <div className="text-center text-muted-foreground ">
                  <p className="text-lg mb-2">No messages yet</p>
                  <p className="text-sm">
                    Start a conversation by typing a message below
                  </p>
                </div>
              </div>
            )}

            {processMessages(messages).map((item, index) => {
              if (item.type === "accordion") {
                return (
                  <div className="max-w-[80%]">
                    <Accordion
                      key={index}
                      type="single"
                      collapsible
                      className="w-full"
                    >
                      <AccordionItem
                        value="item-1"
                        className="border rounded-lg px-2 last:border-b"
                      >
                        <AccordionTrigger>
                          <div className="flex items-center gap-2">
                            <Badge variant="secondary" className="text-xs">
                              {item.toolName}
                            </Badge>
                          </div>
                        </AccordionTrigger>
                        <AccordionContent>
                          <div className="space-y-4">
                            {item.messages.map((msg, msgIndex) => (
                              <AiMessageComponment
                                key={msgIndex}
                                message={msg}
                              />
                            ))}
                          </div>
                        </AccordionContent>
                      </AccordionItem>
                    </Accordion>
                  </div>
                );
              } else {
                return (
                  <div className="max-w-[80%]">
                    <AiMessageComponment key={index} message={item.message} />
                  </div>
                );
              }
            })}

            <div ref={messagesEndRef} />
          </div>
        </div>

        {/* Input Area - Fixed at Bottom */}
        <div className="border-t bg-background p-4">
          <div className="flex gap-2 items-end">
            <Textarea
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={onKeyDown}
              placeholder="Type your message... (Shift+Enter for new line)"
              rows={3}
              className="flex-1 resize-none"
              disabled={isExecuting}
            />
            <Button
              onClick={onExecute}
              disabled={!input.trim() || isExecuting}
              size="lg"
            >
              <Send className="w-5 h-5" />
            </Button>
            {messages.length > 0 && (
              <Button
                variant="outline"
                size="lg"
                onClick={onClearSession}
                disabled={isExecuting}
              >
                Clear Chat
              </Button>
            )}
          </div>
          <p className="text-xs text-muted-foreground mt-2">
            Press Enter to send • Shift+Enter for new line
          </p>
        </div>
      </div>
    </div>
  );
}
