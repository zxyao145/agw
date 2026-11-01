import * as React from "react";
import { createRoot } from "react-dom/client";
import { createUserMessage } from "@agw/chat-core";
import { EMPTY_EXECUTION_QUEUE, type AgentMode, type PermissionMode } from "@agw/chat-runtime";
import { ChatInputQueue } from "../../src/ui-web/components/message/chat-input-queue";
import { ChatInputToolbar } from "../../src/ui-web/components/message/chat-input-toolbar";
import { UserInput } from "../../src/ui-web/components/message/user-input";

const fixture = { submissions: [] as string[], queueEdits: [] as string[] };

function ComposerFixture() {
  const [isEmptyConversation, setIsEmptyConversation] = React.useState(
    new URLSearchParams(location.search).has("empty"),
  );
  const messageCount = new URLSearchParams(location.search).has("short") ? 2 : 40;
  const [permissionMode, setPermissionMode] = React.useState<PermissionMode>("fullAccess");
  const [agentMode, setAgentMode] = React.useState<AgentMode>("execute");
  const [sideActionCount, setSideActionCount] = React.useState(0);
  const [queue, setQueue] = React.useState(() => ({
    ...EMPTY_EXECUTION_QUEUE,
    items: new URLSearchParams(location.search).has("queue")
      ? Array.from({ length: 8 }, (_, index) => ({
          id: `queued-${index}`,
          turnId: `turn-${index}`,
          target: { agentId: "agent", agentType: 0 as const },
          text: `Queued message ${index + 1}`,
          attachments: [],
          fileCommentCount: 0,
          uncertain: false,
          createMessage: (text: string, messageId: string) => ({
            ...createUserMessage(text, []),
            messageId,
          }),
        }))
      : [],
  }));
  return (
    <div className="[container-type:size] relative h-full overflow-hidden" data-testid="chat-panel">
      <div className="h-full overflow-y-auto" data-testid="conversation-scroll">
        <div className="mx-auto flex min-h-full w-full justify-center">
          <div
            className="agw-chat-column-area relative flex min-h-full min-w-0 max-w-5xl flex-1"
            data-testid="chat-column-area"
          >
            <div
              className={`agw-chat-column mx-auto flex w-full max-w-3xl flex-col ${isEmptyConversation ? "self-center" : "min-h-full"}`}
            >
              {isEmptyConversation ? null : (
                <ol className="flex-1" data-testid="conversation-list">
                  {Array.from({ length: messageCount }, (_, index) => (
                    <li key={index} className="h-12">
                      Message {index + 1}
                    </li>
                  ))}
                </ol>
              )}
              <div
                className={`pointer-events-none sticky bottom-0 z-10 min-h-30 px-3 ${isEmptyConversation ? "py-3" : ""}`}
              >
                <UserInput
                  expandOverlay={!isEmptyConversation}
                  onExecute={(value) => {
                    fixture.submissions.push(value);
                    setIsEmptyConversation(false);
                    return true;
                  }}
                >
                  <UserInput.Queue>
                    <ChatInputQueue
                      queue={queue}
                      onEditStart={(editingItemId) => {
                        setQueue((current) => ({ ...current, editingItemId }));
                        return true;
                      }}
                      onEditCancel={() =>
                        setQueue((current) => ({ ...current, editingItemId: null }))
                      }
                      onEditSave={(itemId, text) => {
                        fixture.queueEdits.push(text);
                        setQueue((current) => ({
                          ...current,
                          editingItemId: null,
                          items: current.items.map((item) =>
                            item.id === itemId ? { ...item, text } : item,
                          ),
                        }));
                        return true;
                      }}
                      onRemove={(itemId) =>
                        setQueue((current) => ({
                          ...current,
                          items: current.items.filter((item) => item.id !== itemId),
                        }))
                      }
                      onResume={() => setQueue((current) => ({ ...current, paused: false }))}
                    />
                  </UserInput.Queue>
                  {new URLSearchParams(location.search).has("image") ? (
                    <UserInput.Context>
                      <img
                        src="data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLbtAAAAABJRU5ErkJggg=="
                        alt="draft.png"
                        className="size-20 object-cover"
                      />
                    </UserInput.Context>
                  ) : null}
                  <UserInput.BottomLeft>
                    <ChatInputToolbar
                      commandSource={{ mode: "unsupported" }}
                      isExecuting={false}
                      isTransitioning={false}
                      permissionMode={permissionMode}
                      agentMode={agentMode}
                      onCommandSelect={() => {}}
                      onPermissionModeChange={setPermissionMode}
                      onAgentModeChange={setAgentMode}
                    />
                  </UserInput.BottomLeft>
                </UserInput>
              </div>
            </div>
          </div>
          {isEmptyConversation ? null : (
            <aside
              className={`hidden shrink-0 @min-[64rem]:block ${sideActionCount % 2 === 0 ? "w-64" : "w-32"}`}
              data-testid="neighbor-panel"
            >
              <button type="button" onClick={() => setSideActionCount((count) => count + 1)}>
                Side action {sideActionCount}
              </button>
            </aside>
          )}
        </div>
      </div>
    </div>
  );
}

Object.assign(window, { composerFixture: fixture });
createRoot(document.getElementById("root")!).render(<ComposerFixture />);

declare global {
  interface Window {
    composerFixture: typeof fixture;
  }
}
