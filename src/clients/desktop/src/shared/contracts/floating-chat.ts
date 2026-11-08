export type FloatingChatButtonRequest = {
  owner: string | null;
  background: string;
  foreground: string;
  border: string;
  focus?: boolean;
};

export type FloatingChatButtonViewState = {
  background: string;
  foreground: string;
  border: string;
  x: number;
  y: number;
  dragging: boolean;
};

export type FloatingChatButtonDrag = {
  phase: "start" | "move" | "end" | "cancel";
  screenX: number;
  screenY: number;
};

export type AgwFloatingChatButtonBridge = {
  getState(): Promise<FloatingChatButtonViewState>;
  ready(): void;
  drag(drag: FloatingChatButtonDrag): void;
  open(): void;
  onState(listener: (state: FloatingChatButtonViewState) => void): () => void;
};
