"use client";

import { ChatMessageArea } from "./chat-message-area";
import type { ChatMessageAreaProps } from "../../types";

export function Chat(props: ChatMessageAreaProps) {
  return (
    <div className="flex flex-col">
      <div className="flex-1 flex flex-col overflow-hidden">
        <ChatMessageArea {...props} />
      </div>
    </div>
  );
}
