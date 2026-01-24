"use client";

import { Badge } from "@/components/ui/badge";
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
} from "@/components/ui/accordion";
import type { ChatMessageAreaProps } from "../types";
import { AiMessageComponent } from "./message";

export function ChatMessageArea({
  messages,
  messagesEndRef,
  processMessages,
}: ChatMessageAreaProps) {
  return (
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
              <div className="max-w-[80%]" key={index}>
                <Accordion
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
                          <AiMessageComponent
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
              <div className="max-w-[80%]" key={index}>
                <AiMessageComponent message={item.message} />
              </div>
            );
          }
        })}

        <div ref={messagesEndRef} />
      </div>
    </div>
  );
}
