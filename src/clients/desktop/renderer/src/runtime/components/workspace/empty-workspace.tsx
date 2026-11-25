import { Button } from "@agw/components";

export function EmptyWorkspace({ onNewConversation }: { onNewConversation(): void }) {
  return (
    <div className="flex min-h-0 flex-1 items-center justify-center">
      <Button type="button" variant="outline" onClick={onNewConversation}>
        New conversation
      </Button>
    </div>
  );
}
