import { ChatRouteBoundary } from "../../components/chat/chat-route-boundary";

import { ChatWorkspace } from "./chat-workspace";

export default function ChatPage() {
  return (
    <ChatRouteBoundary>
      <ChatWorkspace routeBasePath="/chat" showProjectSelect />
    </ChatRouteBoundary>
  );
}
