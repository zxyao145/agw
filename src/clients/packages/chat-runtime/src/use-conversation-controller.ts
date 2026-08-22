import * as React from "react";

import {
  ConversationController,
  type ConversationControllerOptions,
  type ConversationControllerState,
} from "./conversation-controller";

export type UseConversationControllerResult = ConversationControllerState & {
  controller: ConversationController;
};

export function useConversationController(
  options: ConversationControllerOptions,
): UseConversationControllerResult {
  const controller = React.useMemo(
    () => new ConversationController(options),
    [options.adapter, options.projectId, options.target?.id, options.target?.type],
  );
  React.useEffect(() => () => void controller.dispose(), [controller]);
  React.useEffect(() => controller.updateOptions(options), [controller, options]);
  React.useEffect(
    () => controller.hydrate(options.sessionSeed),
    [controller, options.sessionSeed.revision],
  );
  const state = React.useSyncExternalStore(
    controller.subscribe,
    controller.getSnapshot,
    controller.getSnapshot,
  );
  return React.useMemo(() => ({ ...state, controller }), [controller, state]);
}
